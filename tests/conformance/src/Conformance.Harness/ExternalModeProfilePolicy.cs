namespace Conformance.Harness;

/// <summary>
/// Pure decision logic for whether a non-Default backend profile collection
/// (<c>ShortTimersConformanceFixture</c>, <c>FixedClockConformanceFixture</c>) should skip itself
/// when running in external mode (<c>CONFORMANCE_BACKEND_URL</c>) -- PR #22 review item N6.
///
/// In normal (harness-launched) mode each profile collection starts its own dedicated Python
/// process with its own <see cref="BackendProfile.ExtraEnvironment"/> layered in -- see
/// <c>BackendProfileFixtures.cs</c> -- so Default, ShortTimers and FixedClock can all run
/// concurrently against three independent backend processes with no conflict. External mode
/// breaks both halves of that assumption at once:
///
/// 1. There is exactly ONE external backend process, started once by whoever set
///    CONFORMANCE_BACKEND_URL, with whatever CONFORMANCE_TEST_HOOKS configuration they gave it
///    (if any). The harness has no way to know it matches a specific non-Default profile's
///    requirements, and no way to make it match if it doesn't -- unlike harness-launched mode, it
///    cannot start a second process with different env vars for a different profile.
/// 2. Every profile collection's fixture resolves the SAME <c>CONFORMANCE_FAKE_REALTIME_PORT</c> /
///    <c>CONFORMANCE_FAKE_SEARCH_PORT</c> fixed ports (see <see cref="ExternalModePortPolicy"/>)
///    from the SAME environment variables, because there's still only one already-running
///    external backend to point the fakes at. If more than one profile collection actually tried
///    to bind Kestrel to those same fixed ports, they would race for the same TCP port and
///    whichever loses would fail with a raw "address already in use" socket exception instead of
///    an actionable message.
///
/// This policy therefore has each non-Default profile's fixture skip itself (never call
/// <c>FakeRealtimeUpstreamServer.StartAsync</c>/<c>FakeSearchServer.StartAsync</c> at all) with a
/// clear reason in external mode, deterministically avoiding the port race in point 2 by
/// construction -- there is nothing left to fail explicitly at, since skipping happens before any
/// socket is touched. See tests/conformance/README.md's BackendContract section for the
/// documented policy.
/// </summary>
public static class ExternalModeProfilePolicy
{
    /// <summary>
    /// Returns a clear skip reason when <paramref name="backendUrl"/> denotes external mode and
    /// <paramref name="profileName"/> isn't <paramref name="defaultProfileName"/>; otherwise null
    /// (the fixture should proceed normally).
    /// </summary>
    public static string? ShouldSkip(string? backendUrl, string profileName, string defaultProfileName)
    {
        var isExternal = !string.IsNullOrWhiteSpace(backendUrl);
        if (!isExternal || profileName == defaultProfileName)
        {
            return null;
        }

        return
            $"CONFORMANCE_BACKEND_URL is set (external mode) -- skipping the '{profileName}' " +
            $"backend profile collection. External mode has exactly one already-running backend " +
            $"process, which cannot simultaneously satisfy the '{defaultProfileName}' profile's " +
            $"requirements and this profile's CONFORMANCE_TEST_HOOKS overrides, and every profile " +
            $"collection would otherwise try to bind the same fixed fake ports " +
            $"(CONFORMANCE_FAKE_REALTIME_PORT / CONFORMANCE_FAKE_SEARCH_PORT) concurrently. Only " +
            $"the '{defaultProfileName}' profile collection runs against an external backend; " +
            $"run this profile's scenarios with CONFORMANCE_BACKEND=python (harness-launched) " +
            $"instead.";
    }
}
