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
    /// condition it is proving already genuinely happened). <paramref name="sinceWatermark"/>
    /// (#66 S2) scopes the predicate to output captured at or after a prior <see
    /// cref="DiagnosticsWatermark"/> reading — 0, the default, means "the whole history", matching
    /// this method's original (PR #66) behaviour. Default-implemented as a single instantaneous,
    /// unscoped check so <see cref="ExternalBackend"/> (nothing is captured there) needs no
    /// changes; only <see cref="ProcessBackend"/> overrides this to actually wait or scope.
    /// </summary>
    Task<bool> WaitForDiagnosticsAsync(
        Func<string, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        int sinceWatermark = 0) =>
        Task.FromResult(predicate(DumpDiagnostics()));

    /// <summary>
    /// Opaque low/high-water mark for <see cref="WaitForDiagnosticsAsync"/>'s
    /// <paramref name="sinceWatermark"/> (see <see cref="Harness.CapturedProcessOutput.Watermark"/>)
    /// — take this before triggering the condition under test, then pass it back in so the
    /// predicate only ever sees output captured at or after that point (#66 S2), not (for example)
    /// an identically-worded diagnostic line an earlier scenario sharing this collection's backend
    /// process happened to log first. Always 0 for <see cref="ExternalBackend"/> (nothing is
    /// captured there, so there is nothing to scope).
    /// </summary>
    int DiagnosticsWatermark => 0;

    /// <summary>
    /// Waits for this backend's captured stdout/stderr to go quiet for a bit — see #62: draining
    /// any output still in flight from a *previous* scenario before a caller snapshots a baseline
    /// error count closes the window where that in-flight line could land just after the baseline
    /// read and be misattributed to whatever runs next. Returns <c>true</c> once genuinely
    /// quiescent, <c>false</c> if <paramref name="maxWait"/> elapsed first without ever observing a
    /// full <paramref name="idleWindow"/> of silence (#66 S4) — callers decide how loudly to
    /// surface that rather than have it silently mean "quiescent". Default-implemented as a no-op
    /// returning <c>true</c> (nothing is captured for <see cref="ExternalBackend"/>, so it is
    /// trivially already quiescent); only <see cref="ProcessBackend"/> overrides this meaningfully.
    /// </summary>
    Task<bool> WaitForOutputQuiescenceAsync(
        TimeSpan idleWindow,
        TimeSpan maxWait,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}
