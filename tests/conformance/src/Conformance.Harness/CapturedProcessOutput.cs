using System.Collections.Concurrent;
using System.Diagnostics;

namespace Conformance.Harness;

/// <summary>
/// Captures a child process's stdout/stderr into a bounded ring buffer (interleaved, timestamped)
/// so it can be dumped into a test failure message without needing the process to still be
/// alive or the test to have redirected output itself.
/// </summary>
public sealed class CapturedProcessOutput
{
    private const int MaxLines = 4000;
    private readonly ConcurrentQueue<string> _lines = new();
    private int _count;

    // #28 N13: this scan state and running total are updated incrementally, one line at a time,
    // by Append (see CountUnhandledErrors's doc comment for why -- the dump buffer above is
    // bounded and wraps, so re-deriving the count from it on every call is not an option).
    private readonly Lock _scanGate = new();
    private ErrorScanState _scanState = ErrorScanState.Idle;
    private int _unhandledErrorCount;

    // #55/#62: stdout/stderr arrives on a ThreadPool callback (Process.OutputDataReceived /
    // ErrorDataReceived) some indeterminate time after the child process actually wrote the
    // line -- under CPU/ThreadPool contention that lag can be large enough to race a caller
    // that reads Dump()/CountUnhandledErrors() as an instantaneous snapshot right after some
    // *other*, unrelated signal (a frame arriving over a different channel; a previous
    // scenario's teardown finishing). Mirrors FrameLog's TaskCompletionSource-swap idiom
    // (Conformance.Fakes/FrameLog.cs) so callers can react to new captured output the instant
    // it lands instead of polling or guessing how long to sleep.
    private readonly Lock _signalGate = new();
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Attach(Process process)
    {
        process.OutputDataReceived += (_, e) => Append("OUT", e.Data);
        process.ErrorDataReceived += (_, e) => Append("ERR", e.Data);
    }

    private void Append(string stream, string? line)
    {
        if (line is null)
        {
            return;
        }

        _lines.Enqueue($"[{DateTimeOffset.UtcNow:HH:mm:ss.fff} {stream}] {line}");
        if (Interlocked.Increment(ref _count) > MaxLines)
        {
            _lines.TryDequeue(out _);
        }

        ScanLine(stream, line);

        TaskCompletionSource released;
        lock (_signalGate)
        {
            released = _signal;
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        released.TrySetResult();
    }

    public string Dump() => string.Join(Environment.NewLine, _lines);

    /// <summary>
    /// Awaits the first already-captured or future output snapshot satisfying
    /// <paramref name="predicate"/> (evaluated against the full <see cref="Dump"/> text, the same
    /// shape scenarios already assert against) -- event-driven, not a fixed-interval poll or an
    /// instantaneous single read. Fixes #55: a synchronous <c>Dump()</c> read taken immediately
    /// after an unrelated signal (e.g. a frame arriving on a different channel) can race a
    /// still-in-flight stderr line under load; this reacts the instant the line actually lands
    /// instead. Returns the last-seen snapshot's match result on timeout/cancellation rather than
    /// throwing, so callers can assert with a clear message.
    /// </summary>
    public async Task<bool> WaitForDiagnosticsAsync(
        Func<string, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            Task signalTask;
            lock (_signalGate)
            {
                if (predicate(Dump()))
                {
                    return true;
                }
                signalTask = _signal.Task;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return predicate(Dump());
            }

            cancellationToken.ThrowIfCancellationRequested();
            var delayTask = Task.Delay(remaining, cancellationToken);
            var completed = await Task.WhenAny(signalTask, delayTask).ConfigureAwait(false);
            if (completed != signalTask)
            {
                return predicate(Dump());
            }
        }
    }

    /// <summary>
    /// Waits until no new stdout/stderr line has been captured for at least
    /// <paramref name="idleWindow"/> (an explicit quiet-period signal, not a blind sleep), giving
    /// up after <paramref name="maxWait"/> total regardless. Fixes #62: <see
    /// cref="Conformance.Tests.ConformanceFixture.RunAsync(Func{Task}, int)"/> snapshots a
    /// per-scenario baseline error count before running the scenario body; if a *previous*
    /// scenario's own already-accounted-for stderr line was still in flight through the
    /// ThreadPool callback at that instant, it could land just after this baseline snapshot and
    /// get misattributed as a *new* error this scenario introduced. Draining any in-flight output
    /// before the baseline is read closes that window without changing any existing timeout or
    /// retrying the scenario itself. <paramref name="maxWait"/> is a safety cap only -- a
    /// chatty-but-legitimate backend does not fail this wait, it just returns once the cap is hit.
    /// </summary>
    public async Task WaitForQuiescenceAsync(
        TimeSpan idleWindow,
        TimeSpan maxWait,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + maxWait;
        while (true)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            Task signalTask;
            lock (_signalGate)
            {
                signalTask = _signal.Task;
            }

            var waitFor = remaining < idleWindow ? remaining : idleWindow;
            cancellationToken.ThrowIfCancellationRequested();
            var idleTask = Task.Delay(waitFor, cancellationToken);
            var completed = await Task.WhenAny(signalTask, idleTask).ConfigureAwait(false);
            if (completed == idleTask && waitFor == idleWindow)
            {
                // No new output arrived during a full idle window -- quiescent.
                return;
            }
            if (completed == idleTask)
            {
                // Hit maxWait before ever observing a full idle window -- give up, but never
                // throw: this is a best-effort settling wait, not a correctness gate.
                return;
            }
            // A new line landed (signalTask completed first) -- loop and re-arm a fresh idle
            // window instead of returning early on a stale one.
        }
    }

    /// <summary>
    /// Counts unhandled-error incidents in captured stderr — a python.exe subprocess implementation
    /// of the language-neutral <see cref="IBackendUnderTest.UnhandledErrorCount"/> signal (PR #22
    /// review item N5). Before this, scenarios asserted directly on <c>DumpDiagnostics()</c>
    /// containing/not-containing the literal string "Traceback", coupling every scenario to
    /// Python's exact log format. A C# backend (S2) will never print a Python traceback, so those
    /// assertions could never be ported — this method is the one place that stays Python-specific;
    /// the assertions themselves become <c>Assert.Equal(0, backend.UnhandledErrorCount())</c>.
    ///
    /// One incident is either a Python traceback (`logger.exception(...)`, or asyncio's default
    /// unhandled-exception/unhandled-rejection handler, both of which end up going through
    /// `logging.basicConfig`'s default `"%(levelname)s:%(name)s:%(message)s"` formatter) or a bare
    /// `ERROR:`-level log line with no attached traceback. An `ERROR:` line immediately followed by
    /// its own `Traceback (most recent call last):` block (the common case for
    /// `logger.exception(...)`) counts as *one* incident, not two — <see cref="ScanLine"/> tracks
    /// that pairing explicitly instead of naively summing "lines starting with ERROR:" plus "lines
    /// starting with Traceback".
    ///
    /// #28 N13: this used to re-scan the whole <c>_lines</c> dump buffer from scratch on every
    /// call. That buffer is a bounded ring (<see cref="MaxLines"/>) that silently evicts its
    /// oldest lines once a long-running suite process crosses the cap — so a re-scan would forget
    /// about incidents whose lines had already scrolled out, making the return value able to go
    /// *down* between calls. <see cref="ConformanceFixture.RunAsync(Func{Task}, int)"/> depends on
    /// this being monotonically non-decreasing (it asserts <c>actual &lt;= baseline + allowed</c>
    /// across the scenario body) — a wrap partway through a suite run could otherwise make that
    /// delta go negative and silently hide a real new backend error. The count is now accumulated
    /// incrementally as each stderr line arrives (in <see cref="ScanLine"/>), independently of
    /// whether its line has since been evicted from the dump buffer.
    /// </summary>
    public int CountUnhandledErrors()
    {
        lock (_scanGate)
        {
            return _unhandledErrorCount;
        }
    }

    /// <summary>Feeds one freshly-captured line into the incident-counting state machine (moved
    /// here, off the dump buffer, per <see cref="CountUnhandledErrors"/>'s doc comment). Locked
    /// because <see cref="Attach"/> wires stdout and stderr to independent process callbacks that
    /// can run on different threads concurrently.</summary>
    private void ScanLine(string stream, string content)
    {
        lock (_scanGate)
        {
            if (stream != "ERR")
            {
                _scanState = ErrorScanState.Idle;
                return;
            }

            var isIndented = content.Length > 0 && char.IsWhiteSpace(content[0]);
            var isTracebackHeader = content is "Traceback (most recent call last):";
            var isErrorHeader = content.StartsWith("ERROR:", StringComparison.Ordinal);

            switch (_scanState)
            {
                case ErrorScanState.Idle:
                    if (isErrorHeader)
                    {
                        _unhandledErrorCount++;
                        _scanState = ErrorScanState.AfterErrorHeader;
                    }
                    else if (isTracebackHeader)
                    {
                        _unhandledErrorCount++;
                        _scanState = ErrorScanState.InTracebackBody;
                    }
                    break;

                case ErrorScanState.AfterErrorHeader:
                    if (isTracebackHeader || isIndented)
                    {
                        // Same incident: the ERROR: line was logger.exception(...)'s message, this
                        // is its attached traceback -- don't count it again.
                        _scanState = ErrorScanState.InTracebackBody;
                    }
                    else if (isErrorHeader)
                    {
                        _unhandledErrorCount++;
                        _scanState = ErrorScanState.AfterErrorHeader;
                    }
                    else
                    {
                        // The ERROR: line had no attached traceback -- already counted, done.
                        _scanState = ErrorScanState.Idle;
                    }
                    break;

                case ErrorScanState.InTracebackBody:
                    if (isIndented)
                    {
                        // A `File "...", line N, in ...` / source-line frame -- still this incident.
                        break;
                    }
                    if (isErrorHeader)
                    {
                        _unhandledErrorCount++;
                        _scanState = ErrorScanState.AfterErrorHeader;
                    }
                    else if (isTracebackHeader)
                    {
                        _unhandledErrorCount++;
                        _scanState = ErrorScanState.InTracebackBody;
                    }
                    else
                    {
                        // The un-indented "ExceptionType: message" summary line that always
                        // terminates a Python traceback -- still this incident, now finished.
                        _scanState = ErrorScanState.Idle;
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Same incident-counting contract and state machine as <see cref="CountUnhandledErrors()"/>
    /// (deliberately kept as an independent, self-contained implementation rather than a shared
    /// helper, so that method's own diff stays untouched -- one ERROR:/Traceback pairing is one
    /// incident -- but an incident whose full captured text (every content line from its header
    /// through its last frame/summary line, in order) satisfies <paramref name="isBenignIncident"/>
    /// is skipped entirely instead of counted. Added for issue #10/#26's Browser scenarios -- see
    /// <see cref="Conformance.Tests.Scenarios.Browser.BrowserConformanceFixture"/>'s own doc
    /// comment for the one signature it actually filters and why. This overload never changes what
    /// <see cref="CountUnhandledErrors()"/> itself returns for any existing caller, and re-scans
    /// <see cref="_lines"/> directly rather than sharing #28 N13's incremental <see cref="ScanLine"/>
    /// state -- unlike that state, this overload's result is allowed to miss incidents that have
    /// already scrolled out of the bounded ring buffer, because it's only ever used for a single
    /// end-of-run assertion, not the monotonic-delta contract <see cref="CountUnhandledErrors()"/>
    /// has to uphold.
    /// </summary>
    public int CountUnhandledErrors(Func<IReadOnlyList<string>, bool> isBenignIncident)
    {
        var count = 0;
        var state = ErrorScanState.Idle;
        var incident = new List<string>();

        void FinishIncident()
        {
            if (incident.Count > 0 && !isBenignIncident(incident))
            {
                count++;
            }
            incident.Clear();
        }

        foreach (var line in _lines)
        {
            if (!TryGetStderrContent(line, out var content))
            {
                FinishIncident();
                state = ErrorScanState.Idle;
                continue;
            }

            var isIndented = content.Length > 0 && char.IsWhiteSpace(content[0]);
            var isTracebackHeader = content is "Traceback (most recent call last):";
            var isErrorHeader = content.StartsWith("ERROR:", StringComparison.Ordinal);

            switch (state)
            {
                case ErrorScanState.Idle:
                    if (isErrorHeader)
                    {
                        incident.Add(content);
                        state = ErrorScanState.AfterErrorHeader;
                    }
                    else if (isTracebackHeader)
                    {
                        incident.Add(content);
                        state = ErrorScanState.InTracebackBody;
                    }
                    break;

                case ErrorScanState.AfterErrorHeader:
                    if (isTracebackHeader || isIndented)
                    {
                        // Same incident: the ERROR: line was logger.exception(...)'s message, this
                        // is its attached traceback -- don't count it again.
                        incident.Add(content);
                        state = ErrorScanState.InTracebackBody;
                    }
                    else if (isErrorHeader)
                    {
                        FinishIncident();
                        incident.Add(content);
                        state = ErrorScanState.AfterErrorHeader;
                    }
                    else
                    {
                        // The ERROR: line had no attached traceback -- already counted, done.
                        FinishIncident();
                        state = ErrorScanState.Idle;
                    }
                    break;

                case ErrorScanState.InTracebackBody:
                    if (isIndented)
                    {
                        // A `File "...", line N, in ...` / source-line frame -- still this incident.
                        incident.Add(content);
                        break;
                    }
                    if (isErrorHeader)
                    {
                        FinishIncident();
                        incident.Add(content);
                        state = ErrorScanState.AfterErrorHeader;
                    }
                    else if (isTracebackHeader)
                    {
                        FinishIncident();
                        incident.Add(content);
                        state = ErrorScanState.InTracebackBody;
                    }
                    else
                    {
                        // The un-indented "ExceptionType: message" summary line that always
                        // terminates a Python traceback -- still this incident, now finished.
                        incident.Add(content);
                        FinishIncident();
                        state = ErrorScanState.Idle;
                    }
                    break;
            }
        }

        // A capture that ends mid-incident (state != Idle at EOF, e.g. the process was killed
        // while a traceback was still printing) must still count that incident -- same as the
        // original algorithm, which had already incremented count at the incident's *start* and
        // never needed a closing step at all.
        FinishIncident();
        return count;
    }

    /// <summary>Extracts the original line's content when it was captured from stderr (tagged
    /// "ERR" by <see cref="Attach"/>); returns false for stdout ("OUT") lines.</summary>
    private static bool TryGetStderrContent(string capturedLine, out string content)
    {
        // Lines are "[HH:mm:ss.fff ERR] <original>" / "[HH:mm:ss.fff OUT] <original>" — the
        // timestamp format is fixed-width, but searching for the "] " delimiter rather than
        // hard-coding offsets keeps this robust to that formatting ever changing.
        var closeBracket = capturedLine.IndexOf("] ", StringComparison.Ordinal);
        if (closeBracket < 0 || !capturedLine.Contains(" ERR]", StringComparison.Ordinal))
        {
            content = "";
            return false;
        }

        content = capturedLine[(closeBracket + 2)..];
        return true;
    }

    private enum ErrorScanState
    {
        Idle,
        AfterErrorHeader,
        InTracebackBody,
    }
}
