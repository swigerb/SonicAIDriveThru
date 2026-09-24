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
    /// `logger.exception(...)`) counts as *one* incident, not two — the state machine below tracks
    /// that pairing explicitly instead of naively summing "lines starting with ERROR:" plus "lines
    /// starting with Traceback".
    /// </summary>
    public int CountUnhandledErrors()
    {
        var count = 0;
        var state = ErrorScanState.Idle;

        foreach (var line in _lines)
        {
            if (!TryGetStderrContent(line, out var content))
            {
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
                        count++;
                        state = ErrorScanState.AfterErrorHeader;
                    }
                    else if (isTracebackHeader)
                    {
                        count++;
                        state = ErrorScanState.InTracebackBody;
                    }
                    break;

                case ErrorScanState.AfterErrorHeader:
                    if (isTracebackHeader || isIndented)
                    {
                        // Same incident: the ERROR: line was logger.exception(...)'s message, this
                        // is its attached traceback -- don't count it again.
                        state = ErrorScanState.InTracebackBody;
                    }
                    else if (isErrorHeader)
                    {
                        count++;
                        state = ErrorScanState.AfterErrorHeader;
                    }
                    else
                    {
                        // The ERROR: line had no attached traceback -- already counted, done.
                        state = ErrorScanState.Idle;
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
                        count++;
                        state = ErrorScanState.AfterErrorHeader;
                    }
                    else if (isTracebackHeader)
                    {
                        count++;
                        state = ErrorScanState.InTracebackBody;
                    }
                    else
                    {
                        // The un-indented "ExceptionType: message" summary line that always
                        // terminates a Python traceback -- still this incident, now finished.
                        state = ErrorScanState.Idle;
                    }
                    break;
            }
        }

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
