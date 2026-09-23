namespace Conformance.Harness;

/// <summary>A running backend instance the conformance suite talks to over HTTP/WebSocket only.</summary>
public interface IBackendUnderTest : IAsyncDisposable
{
    Uri BaseUri { get; }

    /// <summary>Recent captured stdout/stderr, for embedding in failure diagnostics. Empty for an externally-provided backend.</summary>
    string DumpDiagnostics();
}
