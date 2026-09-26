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

    /// <summary>
    /// Same contract as <see cref="UnhandledErrorCount()"/>, but an unhandled-error incident whose
    /// full text satisfies <paramref name="isBenignIncident"/> is excluded from the count. Added
    /// for issue #10/#26's Browser scenarios (see
    /// <see cref="Conformance.Tests.Scenarios.Browser.BrowserConformanceFixture"/>) to recognise
    /// one narrow, well-known, Windows-only CPython event-loop teardown race by its exact logged
    /// content — never by count alone — without weakening <see cref="UnhandledErrorCount()"/>'s
    /// own zero-tolerance default for every other scenario. Default-implemented as a passthrough
    /// (ignoring the filter) so <see cref="ExternalBackend"/> and any future non-Python
    /// <see cref="IBackendUnderTest"/> — neither of which can ever emit a Python-shaped traceback
    /// for the filter to match against in the first place — need no changes at all; only
    /// <see cref="ProcessBackend"/> overrides this meaningfully.
    /// </summary>
    int UnhandledErrorCount(Func<IReadOnlyList<string>, bool> isBenignIncident) => UnhandledErrorCount();

    /// <summary>
    /// Waits for a diagnostics snapshot satisfying <paramref name="predicate"/> to appear —
    /// event-driven, not an instantaneous <see cref="DumpDiagnostics"/> read (#55: that read can
    /// race a still-in-flight, asynchronously-captured stderr line under load even though the
    /// condition it is proving already genuinely happened). Default-implemented as a single
    /// instantaneous check so <see cref="ExternalBackend"/> (nothing is captured there) needs no
    /// changes; only <see cref="ProcessBackend"/> overrides this to actually wait.
    /// </summary>
    Task<bool> WaitForDiagnosticsAsync(
        Func<string, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(predicate(DumpDiagnostics()));

    /// <summary>
    /// Waits for this backend's captured stdout/stderr to go quiet for a bit — see #62: draining
    /// any output still in flight from a *previous* scenario before a caller snapshots a baseline
    /// error count closes the window where that in-flight line could land just after the baseline
    /// read and be misattributed to whatever runs next. Default-implemented as a no-op (nothing is
    /// captured for <see cref="ExternalBackend"/>, so it is trivially already quiescent); only
    /// <see cref="ProcessBackend"/> overrides this meaningfully.
    /// </summary>
    Task WaitForOutputQuiescenceAsync(
        TimeSpan idleWindow,
        TimeSpan maxWait,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
