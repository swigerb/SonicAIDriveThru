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
}
