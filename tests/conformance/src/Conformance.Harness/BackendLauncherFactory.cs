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
/// (python|dotnet) or an explicit `CONFORMANCE_BACKEND_URL` override.
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
        var options = new PythonBackendOptions
        {
            RealtimeBaseUri = realtimeBaseUri,
            SearchBaseUri = searchBaseUri,
            Port = port,
            ExtraEnvironment = extraEnvironment ?? new Dictionary<string, string>(),
            Deployment = deployment ?? "gpt-realtime-2.1-conformance",
        };

        return target switch
        {
            "python" => await PythonBackendLauncher.StartAsync(options, cancellationToken).ConfigureAwait(false),
            "dotnet" => throw new ConformanceBackendNotImplementedException(
                "CONFORMANCE_BACKEND=dotnet is a placeholder until the S2 .NET backend exists (see issue #7). " +
                "Skipping — set CONFORMANCE_BACKEND=python (default) or CONFORMANCE_BACKEND_URL to run this suite."),
            _ => throw new InvalidOperationException(
                $"Unknown CONFORMANCE_BACKEND '{target}' — expected 'python' or 'dotnet'."),
        };
    }
}
