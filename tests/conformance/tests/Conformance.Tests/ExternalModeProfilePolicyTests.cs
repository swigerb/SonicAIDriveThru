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

    // PR #42 review item 2: a fixture that only overrides Deployment (leaving Profile at
    // Default) must ALSO skip in external mode -- it slipped through the three-argument
    // overload's profile-name check entirely (same profile name as Default, so that check alone
    // never fires), even though it has exactly the same "needs its own dedicated backend
    // process" requirement a non-Default profile does.

    [Fact]
    public void Four_arg_ShouldSkip_returns_null_when_not_external_regardless_of_deployment()
    {
        Assert.Null(ExternalModeProfilePolicy.ShouldSkip(null, "Default", "Default", "gpt-realtime-1.5-conformance"));
        Assert.Null(ExternalModeProfilePolicy.ShouldSkip("", "Default", "Default", "gpt-realtime-2.1-dz-conformance"));
    }

    [Fact]
    public void Four_arg_ShouldSkip_returns_null_when_external_and_deployment_is_null_and_profile_is_default()
    {
        Assert.Null(ExternalModeProfilePolicy.ShouldSkip("http://127.0.0.1:9000", "Default", "Default", deployment: null));
    }

    [Theory]
    [InlineData("gpt-realtime-1.5-conformance")]
    [InlineData("gpt-realtime-2.1-dz-conformance")]
    public void Four_arg_ShouldSkip_returns_a_clear_reason_when_external_and_deployment_is_overridden_even_on_the_default_profile(string deployment)
    {
        // Profile is deliberately "Default" here -- this is exactly the case the three-argument
        // overload could never catch, since profileName == defaultProfileName short-circuits it.
        var reason = ExternalModeProfilePolicy.ShouldSkip("http://127.0.0.1:9000", "Default", "Default", deployment);

        Assert.NotNull(reason);
        Assert.Contains(deployment, reason);
        Assert.Contains("external mode", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AZURE_OPENAI_REALTIME_DEPLOYMENT", reason);
        Assert.Contains(ExternalModePortPolicy.RealtimePortEnvVar, reason);
        Assert.Contains(ExternalModePortPolicy.SearchPortEnvVar, reason);
    }

    [Fact]
    public void Four_arg_ShouldSkip_prefers_the_deployment_reason_when_both_profile_and_deployment_are_non_default()
    {
        // Not a real combination any current fixture produces (Gpt15ForcedReasoning already sets
        // a distinct profile name so the 3-arg check alone would already skip it) but the policy
        // must still resolve deterministically to one clear reason, not throw or pick arbitrarily.
        var reason = ExternalModeProfilePolicy.ShouldSkip(
            "http://127.0.0.1:9000", "Gpt15ForcedReasoning", "Default", "gpt-realtime-1.5-conformance");

        Assert.NotNull(reason);
        Assert.Contains("AZURE_OPENAI_REALTIME_DEPLOYMENT", reason);
    }
}
