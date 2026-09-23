namespace Conformance.Harness;

/// <summary>A pre-existing backend at a fixed URL (CONFORMANCE_BACKEND_URL) — the harness starts nothing and cleans up nothing.</summary>
internal sealed class ExternalBackend(Uri baseUri) : IBackendUnderTest
{
    public Uri BaseUri { get; } = baseUri;
    public string DumpDiagnostics() => "(external backend — no captured output)";
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Chooses which backend implementation the conformance suite talks to, per `CONFORMANCE_BACKEND`
/// (python|dotnet) or an explicit `CONFORMANCE_BACKEND_URL` override. `dotnet` is an S2 placeholder
/// (issue #7): it FAILS the suite by default (PR #22 review item 15) so CI can never silently skip
/// real backend coverage, and only skips when a developer opts in locally via
/// CONFORMANCE_ALLOW_SKIP=1 (see <see cref="DotnetPlaceholderPolicy"/> -- ignored in CI even then).
/// </summary>
public static class BackendLauncherFactory
{
    public static async Task<IBackendUnderTest> StartAsync(
        Uri realtimeBaseUri, Uri searchBaseUri, int port,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        string? deployment = null,
        CancellationToken cancellationToken = default)
    {
        var explicitUrl = Environment.GetEnvironmentVariable("CONFORMANCE_BACKEND_URL");
        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            return new ExternalBackend(new Uri(explicitUrl));
        }

        var target = (Environment.GetEnvironmentVariable("CONFORMANCE_BACKEND") ?? "python").Trim().ToLowerInvariant();
        var contract = BackendContract.ForPort(realtimeBaseUri, searchBaseUri, port, deployment);
        var options = new PythonBackendOptions
        {
            ExtraEnvironment = extraEnvironment ?? new Dictionary<string, string>(),
        };

        return target switch
        {
            "python" => await PythonBackendLauncher.StartAsync(contract, options, cancellationToken).ConfigureAwait(false),
            "dotnet" => throw DotnetPlaceholderPolicy.BuildException(
                Environment.GetEnvironmentVariable("CONFORMANCE_ALLOW_SKIP"), CiEnvironment.IsCi),
            _ => throw new InvalidOperationException(
                $"Unknown CONFORMANCE_BACKEND '{target}' — expected 'python' or 'dotnet'."),
        };
    }
}
