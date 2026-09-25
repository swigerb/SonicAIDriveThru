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
    }

    public string Dump() => string.Join(Environment.NewLine, _lines);

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

    private enum ErrorScanState
    {
        Idle,
        AfterErrorHeader,
        InTracebackBody,
    }
}
