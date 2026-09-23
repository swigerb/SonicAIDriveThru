using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Pure unit tests for <see cref="PortRaceDetection"/> (PR #22 review item 17) — no real process
/// spawning needed since the heuristic is a pure function of two plain inputs (same pattern as
/// <see cref="ExternalModePortPolicy"/> and <see cref="DotnetPlaceholderPolicy"/>).
/// </summary>
public sealed class PortRaceDetectionTests
{
    [Theory]
    [InlineData("OSError: [Errno 98] error while attempting to bind on address ('127.0.0.1', 51000)")]
    [InlineData("Address already in use")]
    [InlineData("[WinError 10048] Only one usage of each socket address")]
    [InlineData("[WinError 10013] An attempt was made to access a socket in a way forbidden")]
    [InlineData("Only one usage of each socket address (protocol/network address/port) is normally permitted")]
    public void ShouldRetry_true_when_exited_early_with_a_known_bind_failure_signature(string capturedOutput)
    {
        Assert.True(PortRaceDetection.ShouldRetry(TimeSpan.FromSeconds(1), capturedOutput));
    }

    [Fact]
    public void ShouldRetry_false_for_an_unrelated_early_crash()
    {
        const string output = "Traceback (most recent call last):\n  ...\nModuleNotFoundError: No module named 'foo'";

        Assert.False(PortRaceDetection.ShouldRetry(TimeSpan.FromSeconds(1), output));
    }

    [Fact]
    public void ShouldRetry_false_when_the_bind_failure_text_appears_but_the_exit_was_not_early()
    {
        // A real bug that happens to print bind-failure-shaped text long after startup (e.g. a
        // health-check timeout scenario) must not be masked as a "just retry with a new port" race.
        const string output = "OSError: [Errno 98] error while attempting to bind on address ('127.0.0.1', 51000)";

        Assert.False(PortRaceDetection.ShouldRetry(PortRaceDetection.RaceDetectionWindow + TimeSpan.FromSeconds(1), output));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void ShouldRetry_false_for_empty_or_null_output(string? capturedOutput)
    {
        Assert.False(PortRaceDetection.ShouldRetry(TimeSpan.FromSeconds(1), capturedOutput!));
    }

    [Fact]
    public void ShouldRetry_is_case_insensitive()
    {
        Assert.True(PortRaceDetection.ShouldRetry(TimeSpan.FromSeconds(1), "ADDRESS ALREADY IN USE"));
    }
}
