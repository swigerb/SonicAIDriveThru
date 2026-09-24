namespace Conformance.Harness;

/// <summary>A running backend instance the conformance suite talks to over HTTP/WebSocket only.</summary>
public interface IBackendUnderTest : IAsyncDisposable
{
    Uri BaseUri { get; }

    /// <summary>Recent captured stdout/stderr, for embedding in failure diagnostics. Empty for an externally-provided backend.</summary>
    string DumpDiagnostics();

    /// <summary>
    /// Count of unhandled-error incidents this backend process has logged so far (PR #22 review
    /// item N5) — a language-neutral signal scenarios can assert is zero without coupling
    /// themselves to Python's log format (previously they asserted directly on
    /// <see cref="DumpDiagnostics"/> not containing the literal string "Traceback", which a future
    /// C# S2 backend could never satisfy the same way). The Python implementation
    /// (<see cref="ProcessBackend"/>) scans captured stderr for tracebacks and bare ERROR-level
    /// lines; a C# backend under test will eventually count unhandled exceptions from its own
    /// structured logs instead. Zero for an externally-provided backend (nothing is captured).
    /// </summary>
    int UnhandledErrorCount();
}
