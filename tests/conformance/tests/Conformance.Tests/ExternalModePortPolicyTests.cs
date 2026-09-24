using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Unit tests for <see cref="ExternalModePortPolicy"/> (PR #22 review item 16: external mode
/// must use fixed, known-in-advance fake ports, or fail explicitly). Pure and process-free: the
/// policy is a plain function of three string inputs, so every combination is exercised directly
/// instead of relying on real process environment variables or actually starting Kestrel.
/// </summary>
public sealed class ExternalModePortPolicyTests
{
    [Fact]
    public void Resolve_returns_null_ports_when_not_external_and_nothing_set()
    {
        var (realtime, search) = ExternalModePortPolicy.Resolve(null, null, null);

        Assert.Null(realtime);
        Assert.Null(search);
    }

    [Fact]
    public void Resolve_honours_explicit_ports_even_when_not_external()
    {
        var (realtime, search) = ExternalModePortPolicy.Resolve(null, "5001", "5002");

        Assert.Equal(5001, realtime);
        Assert.Equal(5002, search);
    }

    [Fact]
    public void Resolve_throws_when_external_and_realtime_port_missing()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ExternalModePortPolicy.Resolve("http://127.0.0.1:9000", null, "5002"));

        Assert.Contains(ExternalModePortPolicy.RealtimePortEnvVar, ex.Message);
    }

    [Fact]
    public void Resolve_throws_when_external_and_search_port_missing()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ExternalModePortPolicy.Resolve("http://127.0.0.1:9000", "5001", null));

        Assert.Contains(ExternalModePortPolicy.SearchPortEnvVar, ex.Message);
    }

    [Fact]
    public void Resolve_succeeds_when_external_and_both_ports_present_and_valid()
    {
        var (realtime, search) = ExternalModePortPolicy.Resolve("http://127.0.0.1:9000", "5001", "5002");

        Assert.Equal(5001, realtime);
        Assert.Equal(5002, search);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("not-a-port")]
    [InlineData("-1")]
    public void Resolve_throws_on_an_invalid_port_value_regardless_of_mode(string invalidPort)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ExternalModePortPolicy.Resolve(null, invalidPort, "5002"));

        Assert.Contains(ExternalModePortPolicy.RealtimePortEnvVar, ex.Message);
    }

    [Fact]
    public void Resolve_tolerates_incidental_whitespace_around_a_valid_port()
    {
        var (realtime, _) = ExternalModePortPolicy.Resolve(null, " 5001 ", null);

        Assert.Equal(5001, realtime);
    }
}
