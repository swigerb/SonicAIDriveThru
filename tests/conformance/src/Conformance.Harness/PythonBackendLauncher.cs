using System.Diagnostics;
using System.Net.Http;

namespace Conformance.Harness;

/// <summary>Thrown by <see cref="BackendLauncherFactory"/> for CONFORMANCE_BACKEND=dotnet when
/// <see cref="DotnetPlaceholderPolicy.ShouldSkip"/> allows skipping (PR #22 review item 15:
/// CONFORMANCE_ALLOW_SKIP=1 and not CI) — a placeholder until the S2 .NET backend exists.
/// <see cref="ConformanceFixture"/> catches only this specific type and turns it into a skip; any
/// other exception type (see <see cref="ConformanceBackendUnavailableException"/>) fails the
/// suite normally.</summary>
public sealed class ConformanceBackendNotImplementedException(string message) : Exception(message);

/// <summary>Thrown by <see cref="BackendLauncherFactory"/> for CONFORMANCE_BACKEND=dotnet when
/// <see cref="DotnetPlaceholderPolicy.ShouldSkip"/> does not allow skipping (the default, and
/// always in CI) -- deliberately a *different* type than
/// <see cref="ConformanceBackendNotImplementedException"/> so <see cref="ConformanceFixture"/>'s
/// narrow catch clause never accidentally swallows it: it propagates out of
/// <c>InitializeAsync</c> and fails every test in the collection (PR #22 review item 15 --
/// CI must never silently skip real backend coverage just because the S2 .NET backend doesn't
/// exist yet).</summary>
public sealed class ConformanceBackendUnavailableException(string message) : Exception(message);

/// <summary>Thrown internally by <see cref="PythonBackendLauncher"/> when an early process exit
/// looks like a TCP port-bind race rather than a real backend crash (see
/// <see cref="PortRaceDetection"/>) — caught only by <see cref="PythonBackendLauncher.StartAsync"/>'s
/// own bounded retry loop and never allowed to escape to a caller.</summary>
internal sealed class PortBindRaceException(string message) : Exception(message);

/// <summary>Starts the Python backend (app/backend, via .venv) on a free port, pointed at the fakes.</summary>
public static class PythonBackendLauncher
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Bounded so a genuinely unbindable environment (e.g. loopback sockets exhausted) fails
    /// loudly instead of retrying forever (PR #22 review item 17). One real port race is already
    /// an unlikely coincidence on a CI runner or dev box; three in a row means something else is
    /// wrong and the real error should surface.
    /// </summary>
    private const int MaxStartAttempts = 3;

    public static async Task<IBackendUnderTest> StartAsync(
        BackendContract contract, PythonBackendOptions options, CancellationToken cancellationToken = default)
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var backendDir = RepoPaths.BackendDirectory(repoRoot);
        var pythonExe = RepoPaths.PythonExecutable(repoRoot);
        if (!File.Exists(pythonExe))
        {
            throw new FileNotFoundException(
                $"Python venv interpreter not found at '{pythonExe}'. Expected a venv at " +
                $"{Path.Combine(repoRoot, ".venv")} (see the repo README for setup).", pythonExe);
        }

        var staticIndexHtml = RepoPaths.FrontendStaticIndexHtmlPath(repoRoot);
        if (!File.Exists(staticIndexHtml))
        {
            throw new InvalidOperationException(
                $"'{staticIndexHtml}' does not exist. app/backend/static is gitignored and only " +
                "populated by building the frontend (vite's outDir points there) -- run " +
                "`npm ci && npm run build` in app/frontend before running this suite. Without it, " +
                "the Python backend's aiohttp app.router.add_static(...) raises at startup and the " +
                "process exits immediately, which otherwise surfaces here only as an opaque " +
                "\"backend exited early\" failure.");
        }

        var attemptContract = contract;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await StartAttemptAsync(attemptContract, options, backendDir, pythonExe, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PortBindRaceException) when (attempt < MaxStartAttempts)
            {
                // NetworkUtils.GetFreeTcpPort() has an inherent TOCTOU race between releasing the
                // probe socket and the backend's own bind — pick a fresh port and try again.
                attemptContract = attemptContract with { Port = NetworkUtils.GetFreeTcpPort() };
            }
        }
    }

    private static async Task<IBackendUnderTest> StartAttemptAsync(
        BackendContract contract, PythonBackendOptions options, string backendDir, string pythonExe,
        CancellationToken cancellationToken)
    {
        var env = BackendEnvironment.Build(contract, options);
        var startInfo = new ProcessStartInfo(pythonExe, "app.py")
        {
            WorkingDirectory = backendDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // startInfo.Environment starts out as a *copy of this test process's own environment*
        // (not a blank slate) -- so anything ambient in the dev machine's or CI runner's shell
        // (leftover CONFORMANCE_* from a prior manual run, AZURE_* from an unrelated az-cli
        // session, corporate *_PROXY vars used by the NuGet/npm/pip proxy) would otherwise leak
        // straight into the Python child process unmodified, on top of whatever we explicitly
        // set below. Strip those categories first so every var the backend sees in these
        // categories either comes from `env` (explicit, known-good) or wasn't set at all.
        InheritedEnvironmentFilter.Apply(startInfo);

        foreach (var (key, value) in env)
        {
            startInfo.Environment[key] = value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var output = new CapturedProcessOutput();
        output.Attach(process);

        var startedAt = DateTimeOffset.UtcNow;
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start Python backend process '{pythonExe} app.py'.");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Defence-in-depth against orphaned python.exe processes (PR #22 review item 17): if this
        // .NET test process itself is killed forcibly (Stop-Process, a crash, a CI runner reaping
        // an orphaned job) with no chance to run IAsyncDisposable/ProcessExit cleanup, Windows
        // closes every handle the killed process owned -- including this job handle -- which
        // (because of KILL_ON_JOB_CLOSE) makes the OS itself kill the Python process tree. No-op
        // on non-Windows (see WindowsJobObject's own docs for why).
        var jobObject = WindowsJobObject.TryCreateAndAssign(process.Id);

        // Belt-and-suspenders for the *graceful* exit paths that skip normal disposal (e.g. an
        // unhandled exception unwinding past IAsyncDisposable, or a hard Environment.Exit call
        // elsewhere in the process) -- runs on every platform, unlike the job object.
        EventHandler? processExitHandler = null;
        processExitHandler = (_, _) => TryKill(process);
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;

        var baseUri = new Uri($"http://{BackendContract.Host}:{contract.Port}/");

        try
        {
            await WaitForHealthAsync(baseUri, process, output, startedAt, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
            TryKill(process);
            jobObject?.Dispose();
            throw;
        }

        return new ProcessBackend(process, baseUri, output, jobObject, processExitHandler);
    }

    private static async Task WaitForHealthAsync(
        Uri baseUri, Process process, CapturedProcessOutput output, DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var healthUri = new Uri(baseUri, "/health");
        var deadline = DateTimeOffset.UtcNow + HealthTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                var elapsed = DateTimeOffset.UtcNow - startedAt;
                var dump = output.Dump();
                if (PortRaceDetection.ShouldRetry(elapsed, dump))
                {
                    throw new PortBindRaceException(
                        $"Python backend exited immediately (code {process.ExitCode}), " +
                        $"{elapsed.TotalSeconds:F1}s after starting, with output matching a TCP " +
                        $"port-bind failure signature -- treating as a port race between " +
                        $"NetworkUtils.GetFreeTcpPort() and the backend's own bind.\n" +
                        $"--- backend stdout/stderr ---\n{dump}");
                }

                throw new InvalidOperationException(
                    $"Python backend exited early (code {process.ExitCode}) before becoming healthy.\n" +
                    $"--- backend stdout/stderr ---\n{dump}");
            }

            try
            {
                using var response = await http.GetAsync(healthUri, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet — keep polling until the deadline.
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Per-request timeout, not overall cancellation — keep polling.
            }

            await Task.Delay(HealthPollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Python backend did not report healthy at {healthUri} within {HealthTimeout.TotalSeconds:F0}s.\n" +
            $"--- backend stdout/stderr ---\n{output.Dump()}");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill — fine.
        }
    }
}

internal sealed class ProcessBackend(
    Process process, Uri baseUri, CapturedProcessOutput output, WindowsJobObject? jobObject,
    EventHandler? processExitHandler) : IBackendUnderTest
{
    public Uri BaseUri { get; } = baseUri;

    public string DumpDiagnostics() => output.Dump();

    public int UnhandledErrorCount() => output.CountUnhandledErrors();

    public async ValueTask DisposeAsync()
    {
        if (processExitHandler is not null)
        {
            AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited — fine.
        }
        finally
        {
            process.Dispose();
            jobObject?.Dispose();
        }
    }
}
