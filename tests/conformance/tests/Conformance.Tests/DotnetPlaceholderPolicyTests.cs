using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Unit tests for <see cref="DotnetPlaceholderPolicy"/> (PR #22 review item 15: the
/// CONFORMANCE_BACKEND=dotnet S2 placeholder must FAIL the suite by default -- CI must never
/// silently skip real backend coverage -- and only skip when a developer explicitly opts in
/// locally). Pure and process-free: the policy is a plain function of its two inputs, so this
/// exercises every combination directly instead of relying on real (global, parallel-unsafe)
/// process environment variables.
/// </summary>
public sealed class DotnetPlaceholderPolicyTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)] // must be exactly "1" -- no truthy-string leniency here
    [InlineData(" 1 ", true)] // tolerate incidental whitespace from a shell export
    [InlineData("1", true)]
    public void ShouldSkip_requires_exactly_CONFORMANCE_ALLOW_SKIP_equal_1_when_not_CI(
        string? conformanceAllowSkip, bool expected)
    {
        Assert.Equal(expected, DotnetPlaceholderPolicy.ShouldSkip(conformanceAllowSkip, isCi: false));
    }

    [Fact]
    public void ShouldSkip_is_always_false_in_CI_even_with_the_opt_in_set()
    {
        Assert.False(DotnetPlaceholderPolicy.ShouldSkip("1", isCi: true));
    }

    [Fact]
    public void BuildException_returns_NotImplemented_type_when_skip_is_allowed()
    {
        var ex = DotnetPlaceholderPolicy.BuildException("1", isCi: false);

        Assert.IsType<ConformanceBackendNotImplementedException>(ex);
        Assert.Contains("Skipping", ex.Message);
        Assert.Contains(DotnetPlaceholderPolicy.ReasonPrefix, ex.Message);
    }

    [Fact]
    public void BuildException_returns_Unavailable_type_by_default_with_no_opt_in()
    {
        var ex = DotnetPlaceholderPolicy.BuildException(null, isCi: false);

        Assert.IsType<ConformanceBackendUnavailableException>(ex);
        Assert.Contains("FAILS the suite", ex.Message);
    }

    [Fact]
    public void BuildException_returns_Unavailable_type_in_CI_even_with_the_opt_in_set()
    {
        var ex = DotnetPlaceholderPolicy.BuildException("1", isCi: true);

        Assert.IsType<ConformanceBackendUnavailableException>(ex);
    }
}
