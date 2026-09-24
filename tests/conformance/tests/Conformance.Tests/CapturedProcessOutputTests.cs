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
