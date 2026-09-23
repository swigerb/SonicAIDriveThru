using System.Diagnostics;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Proves the core mechanism behind PR #22 review item 17's "no orphaned python.exe after a
/// killed test run" requirement: closing the last handle to a KILL_ON_JOB_CLOSE job object kills
/// every process assigned to it. That is exactly what Windows does automatically to this
/// process's open handles (including any job handle) when this process itself is killed forcibly
/// (Stop-Process, a crash) with no chance to run any cleanup code — so proving
/// <c>Dispose()</c> kills the assigned process is a faithful, deterministic, automatable proxy for
/// that scenario. Windows-only; skips on the CI runner (ubuntu-latest).
/// </summary>
public sealed class WindowsJobObjectTests
{
    [Fact]
    public async Task Disposing_the_job_object_kills_the_assigned_process()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows Job Objects are a Windows-only mechanism (PR #22 review item 17); CI runs this suite on ubuntu-latest.");
            return;
        }

        using var process = StartLongRunningPythonProcess();
        var jobObject = WindowsJobObject.TryCreateAndAssign(process.Id);
        Assert.NotNull(jobObject);
        Assert.False(process.HasExited);

        jobObject!.Dispose();

        var exited = await WaitForExitWithTimeoutAsync(process, TimeSpan.FromSeconds(10));
        Assert.True(exited, "closing the job object's last handle should kill the assigned process (KILL_ON_JOB_CLOSE).");
    }

    [Fact]
    public void TryCreateAndAssign_returns_null_for_a_process_id_that_does_not_exist()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows Job Objects are a Windows-only mechanism (PR #22 review item 17); CI runs this suite on ubuntu-latest.");
            return;
        }

        // Defensive: OpenProcess must fail cleanly for a bogus PID, never throw. int.MaxValue is
        // never a real live PID.
        var jobObject = WindowsJobObject.TryCreateAndAssign(int.MaxValue);

        Assert.Null(jobObject);
    }

    private static Process StartLongRunningPythonProcess()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var pythonExe = RepoPaths.PythonExecutable(repoRoot);
        var startInfo = new ProcessStartInfo(pythonExe, "-c \"import time; time.sleep(60)\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var process = Process.Start(startInfo)!;
        return process;
    }

    private static async Task<bool> WaitForExitWithTimeoutAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
