using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Conformance.Harness;

/// <summary>
/// Wraps a Windows Job Object configured with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE so the launched
/// Python backend process (and any of its own children) is killed automatically by the OS the
/// instant this test process's last handle to the job is closed — including when this process
/// itself is killed forcibly (Stop-Process, a crash, a CI runner reaping an orphaned job) and
/// never gets a chance to run <c>IAsyncDisposable</c>/<c>ProcessExit</c> cleanup code at all (PR
/// #22 review item 17). No-op on non-Windows platforms — the CI runner (<c>ubuntu-latest</c>)
/// instead relies on <c>Process.Kill(entireProcessTree: true)</c> in
/// <c>ProcessBackend.DisposeAsync</c> and the <c>AppDomain.ProcessExit</c> handler registered in
/// <see cref="PythonBackendLauncher"/> for the graceful-shutdown paths; POSIX has no exact
/// equivalent primitive that survives a SIGKILL of the parent for free, so this class is
/// deliberately Windows-only hardening rather than a cross-platform abstraction.
/// </summary>
public sealed class WindowsJobObject : IDisposable
{
    private nint _handle;

    private WindowsJobObject(nint handle) => _handle = handle;

    /// <summary>
    /// Creates a job object with KILL_ON_JOB_CLOSE and assigns <paramref name="processId"/> to
    /// it. Returns <see langword="null"/> on non-Windows platforms or if any step fails for any
    /// reason — this is a defence-in-depth hardening layer, never a hard requirement for starting
    /// the backend at all, so failures here must never surface as a suite failure.
    /// </summary>
    public static WindowsJobObject? TryCreateAndAssign(int processId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return TryCreateAndAssignOnWindows(processId);
    }

    [SupportedOSPlatform("windows")]
    private static WindowsJobObject? TryCreateAndAssignOnWindows(int processId)
    {
        try
        {
            var jobHandle = NativeMethods.CreateJobObject(nint.Zero, null);
            if (jobHandle == nint.Zero)
            {
                return null;
            }

            if (!TrySetKillOnJobClose(jobHandle))
            {
                NativeMethods.CloseHandle(jobHandle);
                return null;
            }

            var processHandle = NativeMethods.OpenProcess(NativeMethods.ProcessAllAccess, false, processId);
            if (processHandle == nint.Zero)
            {
                NativeMethods.CloseHandle(jobHandle);
                return null;
            }

            try
            {
                if (!NativeMethods.AssignProcessToJobObject(jobHandle, processHandle))
                {
                    NativeMethods.CloseHandle(jobHandle);
                    return null;
                }
            }
            finally
            {
                NativeMethods.CloseHandle(processHandle);
            }

            return new WindowsJobObject(jobHandle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            // Should be unreachable on any real Windows box — kernel32 always exports these — but
            // this class must never be the reason the suite fails to start the backend at all.
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TrySetKillOnJobClose(nint jobHandle)
    {
        var extendedInfo = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose,
            },
        };

        var length = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var extendedInfoPtr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, fDeleteOld: false);
            return NativeMethods.SetInformationJobObject(
                jobHandle, NativeMethods.JobObjectExtendedLimitInformation, extendedInfoPtr, (uint)length);
        }
        finally
        {
            Marshal.FreeHGlobal(extendedInfoPtr);
        }
    }

    public void Dispose()
    {
        if (_handle != nint.Zero && OperatingSystem.IsWindows())
        {
            CloseHandleOnWindows(_handle);
            _handle = nint.Zero;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CloseHandleOnWindows(nint handle) => NativeMethods.CloseHandle(handle);

    [SupportedOSPlatform("windows")]
    private static class NativeMethods
    {
        public const uint JobObjectLimitKillOnJobClose = 0x2000;
        public const int JobObjectExtendedLimitInformation = 9;
        public const int ProcessAllAccess = 0x1F0FFF;

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint CreateJobObject(nint lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(
            nint hJob, int infoType, nint lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(nint hObject);
    }
}
