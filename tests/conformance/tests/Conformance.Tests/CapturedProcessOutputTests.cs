using System.Diagnostics;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Pure unit tests for <see cref="CapturedProcessOutput.CountUnhandledErrors"/> (PR #22 review
/// item N5) — drives a real short-lived Python process (the venv interpreter this suite already
/// depends on, via -c) that prints known stderr content, so the test exercises the actual
/// stdout/stderr capture wiring (<see cref="CapturedProcessOutput.Attach"/>,
/// <c>BeginOutputReadLine</c>/<c>BeginErrorReadLine</c>) rather than a hand-built fixture.
/// </summary>
public sealed class CapturedProcessOutputTests
{
    [Fact]
    public async Task CountUnhandledErrors_treats_an_error_line_with_its_attached_traceback_as_one_incident()
    {
        // Deliberately writes to stderr only -- if this also wrote to stdout, the two streams are
        // read by independent async readers into one shared queue with no guaranteed cross-stream
        // interleave order, which would make the exact line order (and so the state machine's
        // count) non-deterministic. A single stream's own order is always preserved.
        const string script =
            "import sys\n" +
            "print('ERROR:sonic-drive-in:boom', file=sys.stderr)\n" +
            "print('Traceback (most recent call last):', file=sys.stderr)\n" +
            "print('  File \"rtmt.py\", line 10, in handle', file=sys.stderr)\n" +
            "print(\"KeyError: 'output'\", file=sys.stderr)\n" +
            "print('ERROR:asyncio:unrelated bare error with no traceback', file=sys.stderr)\n" +
            "print('a plain diagnostic line, not an error', file=sys.stderr)\n";

        var count = await RunAndCountAsync(script);

        // Incident 1: the ERROR: line plus its attached Traceback/frame/summary lines (4 physical
        // lines, one incident). Incident 2: the bare ERROR: line with nothing attached. The plain
        // diagnostic line is not an incident.
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CountUnhandledErrors_is_zero_for_ordinary_stderr_output()
    {
        const string script =
            "import sys\n" +
            "print('INFO:sonic-drive-in:server started', file=sys.stderr)\n" +
            "print('some ordinary diagnostic text', file=sys.stderr)\n";

        Assert.Equal(0, await RunAndCountAsync(script));
    }

    /// <summary>
    /// Deterministic regression coverage for the issue #10/#26 Windows Browser-category flake
    /// (see <see cref="Scenarios.Browser.BrowserConformanceFixture.IsBenignProactorTeardownIncident"/>):
    /// replays the *exact* two-incident stderr shape captured live from a real flaked run (a real
    /// browser abruptly severing a live `/realtime` WebSocket raced CPython's ProactorEventLoop
    /// teardown path on Windows) through the actual capture-and-count pipeline, rather than relying
    /// on that race reoccurring on demand. Proves both that the raw, unfiltered count still sees
    /// two incidents (so <see cref="CapturedProcessOutput.CountUnhandledErrors()"/> itself is
    /// completely unchanged) and that the filtered overload recognises and discards precisely
    /// those two, and only those two.
    /// </summary>
    [Fact]
    public async Task CountUnhandledErrors_filtered_overload_discards_the_known_benign_proactor_teardown_incident()
    {
        const string script =
            "import sys\n" +
            "print('ERROR:asyncio:Exception in callback _ProactorBasePipeTransport._call_connection_lost(None)', file=sys.stderr)\n" +
            "print('handle: <Handle _ProactorBasePipeTransport._call_connection_lost(None)>', file=sys.stderr)\n" +
            "print('Traceback (most recent call last):', file=sys.stderr)\n" +
            "print('  File \"C:\\\\Python312\\\\Lib\\\\asyncio\\\\events.py\", line 88, in _run', file=sys.stderr)\n" +
            "print('    self._context.run(self._callback, *self._args)', file=sys.stderr)\n" +
            "print('  File \"C:\\\\Python312\\\\Lib\\\\asyncio\\\\proactor_events.py\", line 165, in _call_connection_lost', file=sys.stderr)\n" +
            "print('    self._sock.shutdown(socket.SHUT_RDWR)', file=sys.stderr)\n" +
            "print('ConnectionResetError: [WinError 10054] An existing connection was forcibly closed by the remote host', file=sys.stderr)\n" +
            "print('INFO:sonic-drive-in:Session abc detached (client close code=1001); holding order for 10s', file=sys.stderr)\n";

        var (raw, filtered) = await RunAndCountBothAsync(
            script, Scenarios.Browser.BrowserConformanceFixture.IsBenignProactorTeardownIncident);

        Assert.Equal(2, raw);
        Assert.Equal(0, filtered);
    }

    [Fact]
    public async Task CountUnhandledErrors_filtered_overload_still_counts_an_unrelated_real_error()
    {
        const string script =
            "import sys\n" +
            "print('ERROR:sonic-drive-in:a genuine application bug', file=sys.stderr)\n" +
            "print('Traceback (most recent call last):', file=sys.stderr)\n" +
            "print('  File \"rtmt.py\", line 42, in handle', file=sys.stderr)\n" +
            "print(\"KeyError: 'oops'\", file=sys.stderr)\n";

        var (raw, filtered) = await RunAndCountBothAsync(
            script, Scenarios.Browser.BrowserConformanceFixture.IsBenignProactorTeardownIncident);

        Assert.Equal(1, raw);
        Assert.Equal(1, filtered);
    }

    private static async Task<(int Raw, int Filtered)> RunAndCountBothAsync(
        string pythonScript, Func<IReadOnlyList<string>, bool> isBenignIncident)
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var pythonExe = RepoPaths.PythonExecutable(repoRoot);
        var startInfo = new ProcessStartInfo(pythonExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(pythonScript);

        using var process = new Process { StartInfo = startInfo };
        var output = new CapturedProcessOutput();
        output.Attach(process);

        Assert.True(process.Start(), "Failed to start the Python venv interpreter for this test.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        return (output.CountUnhandledErrors(), output.CountUnhandledErrors(isBenignIncident));
    }

    /// <summary>
    /// #28 N13: <see cref="CapturedProcessOutput"/>'s dump buffer is capped at 4000 lines and
    /// silently evicts its oldest entries once a process prints past that — before this fix,
    /// <c>CountUnhandledErrors</c> re-derived its answer by re-scanning that same bounded buffer
    /// on every call, so an error logged early enough to have since scrolled out of the buffer
    /// would stop being counted, and the running total could go *down* between calls. Prints one
    /// incident, then enough harmless stderr lines to wrap the 4000-line buffer several times
    /// over, then asserts the incident is still counted -- this fails against the old
    /// whole-buffer-rescan implementation (see this fix's mutation check) and passes against the
    /// incremental, Append-time count.
    /// </summary>
    [Fact]
    public async Task CountUnhandledErrors_survives_the_dump_buffer_wrapping_around_it()
    {
        const int linesAfterTheIncident = 4500; // comfortably past the 4000-line dump buffer cap
        var script =
            "import sys\n" +
            "print('ERROR:sonic-drive-in:boom before the wrap', file=sys.stderr)\n" +
            $"for i in range({linesAfterTheIncident}):\n" +
            "    print(f'ordinary diagnostic line {i}', file=sys.stderr)\n";

        Assert.Equal(1, await RunAndCountAsync(script));
    }

    private static async Task<int> RunAndCountAsync(string pythonScript)
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var pythonExe = RepoPaths.PythonExecutable(repoRoot);
        var startInfo = new ProcessStartInfo(pythonExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(pythonScript);

        using var process = new Process { StartInfo = startInfo };
        var output = new CapturedProcessOutput();
        output.Attach(process);

        Assert.True(process.Start(), "Failed to start the Python venv interpreter for this test.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        return output.CountUnhandledErrors();
    }
}
