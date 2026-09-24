using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Unit tests for <see cref="ExternalModeProfilePolicy"/> (PR #22 review item N6: non-Default
/// backend profile collections must skip themselves in external mode rather than race the
/// Default collection -- or each other -- to bind the same fixed fake ports). Pure and
/// process-free: the policy is a plain function of three strings, so every combination is
/// exercised directly instead of relying on real process environment variables or actually
/// starting Kestrel.
/// </summary>
public sealed class ExternalModeProfilePolicyTests
{
    [Fact]
    public void ShouldSkip_returns_null_when_not_external_regardless_of_profile()
    {
        Assert.Null(ExternalModeProfilePolicy.ShouldSkip(null, "ShortTimers", "Default"));
        Assert.Null(ExternalModeProfilePolicy.ShouldSkip("", "FixedClock(2026-07-04T15:00:00-05:00)", "Default"));
        Assert.Null(ExternalModeProfilePolicy.ShouldSkip("   ", "Default", "Default"));
    }

    [Fact]
    public void ShouldSkip_returns_null_when_external_and_profile_is_default()
    {
        Assert.Null(ExternalModeProfilePolicy.ShouldSkip("http://127.0.0.1:9000", "Default", "Default"));
    }

    [Theory]
    [InlineData("ShortTimers")]
    [InlineData("FixedClock(2026-07-04T15:00:00-05:00)")]
    public void ShouldSkip_returns_a_clear_reason_when_external_and_profile_is_not_default(string profileName)
    {
        var reason = ExternalModeProfilePolicy.ShouldSkip("http://127.0.0.1:9000", profileName, "Default");

        Assert.NotNull(reason);
        Assert.Contains(profileName, reason);
        Assert.Contains("external mode", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ExternalModePortPolicy.RealtimePortEnvVar, reason);
        Assert.Contains(ExternalModePortPolicy.SearchPortEnvVar, reason);
    }
}
