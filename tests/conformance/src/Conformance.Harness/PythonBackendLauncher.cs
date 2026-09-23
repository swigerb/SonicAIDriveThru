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

/// <summary>Starts the Python backend (app/backend, via .venv) on a free port, pointed at the fakes.</summary>
public static class PythonBackendLauncher
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(250);

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

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start Python backend process '{pythonExe} app.py'.");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var baseUri = new Uri($"http://{BackendContract.Host}:{contract.Port}/");

        try
        {
            await WaitForHealthAsync(baseUri, process, output, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        return new ProcessBackend(process, baseUri, output);
    }

    private static async Task WaitForHealthAsync(
        Uri baseUri, Process process, CapturedProcessOutput output, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var healthUri = new Uri(baseUri, "/health");
        var deadline = DateTimeOffset.UtcNow + HealthTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Python backend exited early (code {process.ExitCode}) before becoming healthy.\n" +
                    $"--- backend stdout/stderr ---\n{output.Dump()}");
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

internal sealed class ProcessBackend(Process process, Uri baseUri, CapturedProcessOutput output) : IBackendUnderTest
{
    public Uri BaseUri { get; } = baseUri;

    public string DumpDiagnostics() => output.Dump();

    public async ValueTask DisposeAsync()
    {
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
        }
    }
}
