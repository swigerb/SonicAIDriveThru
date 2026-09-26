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

    // #66 S1: the instant the most recently captured line was appended, guarded by the same
    // _signalGate lock as _signal so a reader always sees a consistent (signal, timestamp) pair.
    // Lets WaitForOutputQuiescenceAsync shortcut straight past its idle window when the backend
    // is *already* silent instead of always paying that window's cost even though nothing was
    // ever going to arrive. DateTimeOffset.MinValue (never appended) counts as "quiet forever".
    private DateTimeOffset _lastAppendUtc = DateTimeOffset.MinValue;

    public void Attach(Process process)
    {
        process.OutputDataReceived += (_, e) => Append("OUT", e.Data);
        process.ErrorDataReceived += (_, e) => Append("ERR", e.Data);
    }

    /// <summary>
    /// #66 M2: internal rather than private so <c>CapturedProcessOutputTests</c> (see
    /// <c>AssemblyInfo.cs</c>'s <c>InternalsVisibleTo</c>) can drive deterministic, no-process
    /// unit tests of <see cref="WaitForDiagnosticsAsync"/>/<see
    /// cref="WaitForOutputQuiescenceAsync"/> directly, instead of having to race a real child
    /// process's own ThreadPool callback timing -- exactly the nondeterminism these wait methods
    /// exist to protect callers from in the first place.
    /// </summary>
    internal void Append(string stream, string? line)
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
            _lastAppendUtc = DateTimeOffset.UtcNow;
        }
        released.TrySetResult();
    }

    public string Dump() => string.Join(Environment.NewLine, _lines);

    /// <summary>
    /// Opaque watermark = total lines ever captured so far, independent of the bounded dump
    /// buffer's eviction (<see cref="MaxLines"/>) -- pair with <see cref="DumpSince"/> or the
    /// <c>sinceWatermark</c> parameter of <see cref="WaitForDiagnosticsAsync"/> to scope a
    /// predicate to only what was captured at or after a point in time (#66 S2).
    /// </summary>
    public int Watermark => Volatile.Read(ref _count);

    /// <summary>
    /// Every currently-retained captured line appended at or after <paramref name="watermark"/>
    /// (a value previously returned by <see cref="Watermark"/>). #66 S2: an unscoped <see
    /// cref="Dump"/> holds this whole collection's history, so a caller's predicate can be
    /// satisfied vacuously by an unrelated *earlier* scenario's identically-worded diagnostic line
    /// (e.g. two ShortTimers scenarios both hitting the same fallback-timeout log message) --
    /// scoping to "since I last checked" removes that false-positive window without needing the
    /// backend to stamp anything session- or scenario-specific. If lines have since scrolled out
    /// of the bounded dump buffer, this can only return a superset of "since the watermark" (same
    /// as an unscoped <see cref="Dump"/>) -- degrading back to pre-#66 scoping for an abnormally
    /// chatty run, never a new false negative.
    /// </summary>
    public string DumpSince(int watermark)
    {
        var snapshot = _lines.ToArray();
        var totalEverAppended = Volatile.Read(ref _count);
        var evictedBeforeSnapshot = Math.Max(0, totalEverAppended - snapshot.Length);
        var skip = Math.Max(0, watermark - evictedBeforeSnapshot);
        return string.Join(Environment.NewLine, snapshot.Skip(skip));
    }

    /// <summary>
    /// Awaits the first already-captured or future output snapshot satisfying
    /// <paramref name="predicate"/> (evaluated against <see cref="DumpSince"/>, scoped to
    /// <paramref name="sinceWatermark"/> -- 0, the default, is equivalent to the whole <see
    /// cref="Dump"/> history) -- event-driven, not a fixed-interval poll or an instantaneous
    /// single read. Fixes #55: a synchronous <c>Dump()</c> read taken immediately after an
    /// unrelated signal (e.g. a frame arriving on a different channel) can race a still-in-flight
    /// stderr line under load; this reacts the instant the line actually lands instead. Returns
    /// the last-seen snapshot's match result on timeout rather than throwing, so callers can
    /// assert with a clear message; a genuine <paramref name="cancellationToken"/> cancellation
    /// propagates instead of being swallowed as a timeout (#66 S3).
    /// </summary>
    public async Task<bool> WaitForDiagnosticsAsync(
        Func<string, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        int sinceWatermark = 0)
    {
        // #66 S3: one timeout task for the entire wait -- the previous version recomputed
        // "remaining" and allocated a fresh Task.Delay on every wake, which also meant a
        // cancelled delay winning Task.WhenAny fell through to a final predicate check instead of
        // propagating the cancellation.
        var timeoutTask = Task.Delay(timeout, cancellationToken);
        while (true)
        {
            Task signalTask;
            lock (_signalGate)
            {
                if (predicate(DumpSince(sinceWatermark)))
                {
                    return true;
                }
                signalTask = _signal.Task;
            }

            var completed = await Task.WhenAny(signalTask, timeoutTask).ConfigureAwait(false);
            if (completed == timeoutTask)
            {
                // Rethrows only if cancellationToken (not the plain timeout) is what completed
                // this task -- an ordinary elapsed timeout completes normally and falls through
                // to the final check below.
                await timeoutTask.ConfigureAwait(false);
                return predicate(DumpSince(sinceWatermark));
            }
            // A new line landed (signalTask completed first) -- loop and re-check the predicate
            // against the latest capture.
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
    /// retrying the scenario itself.
    /// <para>
    /// #66 S1: when the backend has already been silent for a full <paramref name="idleWindow"/>
    /// (the common case -- most scenario boundaries have nothing in flight at all), this returns
    /// immediately instead of always paying the window's cost regardless. Neither this fast path
    /// nor the debounce loop below it can ever observe a line that genuinely hasn't been
    /// dispatched yet, so the fast path changes nothing about what this method can detect --only
    /// how long it takes when there is nothing to detect.
    /// </para>
    /// <para>
    /// Returns <c>true</c> once the backend has gone quiet for a full <paramref
    /// name="idleWindow"/>; <c>false</c> if <paramref name="maxWait"/> elapsed first (#66 S4) --
    /// still never throws for a mere cap hit, only for a genuine <paramref
    /// name="cancellationToken"/> cancellation (#66 S3), so callers can decide how loudly to
    /// surface a cap hit rather than have it silently mean "quiescent".
    /// </para>
    /// </summary>
    public async Task<bool> WaitForOutputQuiescenceAsync(
        TimeSpan idleWindow,
        TimeSpan maxWait,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + maxWait;
        var firstIteration = true;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            Task signalTask;
            TimeSpan idleNeeded;
            lock (_signalGate)
            {
                signalTask = _signal.Task;
                // #66 S1 fast path: only ever applies on the FIRST iteration -- every later
                // iteration got here because a new line just landed and re-armed the debounce, so
                // this can only ever shortcut the common "already silent" case, never a line still
                // being dispatched.
                var quietFor = firstIteration ? DateTimeOffset.UtcNow - _lastAppendUtc : TimeSpan.Zero;
                idleNeeded = quietFor >= idleWindow ? TimeSpan.Zero : idleWindow - quietFor;
            }
            firstIteration = false;

            if (idleNeeded <= TimeSpan.Zero)
            {
                // Already quiet for a full idle window -- nothing to wait for at all.
                return true;
            }

            var cappedByDeadline = remaining < idleNeeded;
            var waitFor = cappedByDeadline ? remaining : idleNeeded;
            var idleTask = Task.Delay(waitFor, cancellationToken);
            var completed = await Task.WhenAny(signalTask, idleTask).ConfigureAwait(false);
            if (completed == idleTask)
            {
                await idleTask.ConfigureAwait(false); // rethrows only for a genuine cancellation.
                // waitFor was clipped to whatever was left of maxWait (never a full idle window):
                // the cap was hit before quiescence was ever observed, not the other way around.
                return !cappedByDeadline;
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
