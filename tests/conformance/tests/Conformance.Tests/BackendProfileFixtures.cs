using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// PR #22 review item 12: named backend profiles (Default, ShortTimers, FixedClock) each get
/// their own dedicated xUnit collection and Python process — CONFORMANCE_TEST_HOOKS and its
/// overrides are read once at Python module-import time, so an already-running backend can't
/// switch profiles mid-suite. <see cref="ConformanceFixture"/>/<see cref="ConformanceCollection"/>
/// remain the Default profile for the bulk of the suite; these are additional, narrowly-scoped
/// collections for scenarios that specifically need short timers or a frozen clock.
/// </summary>
public sealed class ShortTimersConformanceFixture : ConformanceFixture
{
    protected override BackendProfile Profile => BackendProfiles.ShortTimers;
}

[CollectionDefinition(Name)]
public sealed class ShortTimersConformanceCollection : ICollectionFixture<ShortTimersConformanceFixture>
{
    public const string Name = "ConformanceShortTimers";
}

/// <summary>
/// Same generous idle/grace/nudge budgets as <see cref="BackendProfiles.BrowserTimers"/>, but for
/// a plain-WebSocket reason: see <see cref="BackendProfiles.RateLimitTimers"/>'s doc comment for
/// why a fresh connection's echo-suppression cooldown needs this much wall-clock room.
/// </summary>
public sealed class RateLimitTimersConformanceFixture : ConformanceFixture
{
    protected override BackendProfile Profile => BackendProfiles.RateLimitTimers;
}

[CollectionDefinition(Name)]
public sealed class RateLimitTimersConformanceCollection : ICollectionFixture<RateLimitTimersConformanceFixture>
{
    public const string Name = "ConformanceRateLimitTimers";
}

/// <summary>
/// See <see cref="BackendProfiles.ResumeTimers"/>'s doc comment: an 8-second idle/grace budget
/// (instead of <see cref="ShortTimersConformanceFixture"/>'s 1-second one) for the multi-round-trip
/// resume-handshake scenarios that aren't testing idle behaviour themselves. See that profile's
/// doc comment for why this stays a separate collection/process from
/// <see cref="ResumeMarginConformanceFixture"/> rather than sharing one.
/// </summary>
public sealed class ResumeTimersConformanceFixture : ConformanceFixture
{
    protected override BackendProfile Profile => BackendProfiles.ResumeTimers;
}

[CollectionDefinition(Name)]
public sealed class ResumeTimersConformanceCollection : ICollectionFixture<ResumeTimersConformanceFixture>
{
    public const string Name = "ConformanceResumeTimers";
}

/// <summary>
/// PR #52 CI follow-up: dedicated collection/process for <see cref="BackendProfiles.ResumeMargin"/>
/// — see that profile's doc comment for why the resume scenarios need their own idle/grace timing
/// separate from <see cref="ShortTimersConformanceFixture"/>'s.
/// </summary>
public sealed class ResumeMarginConformanceFixture : ConformanceFixture
{
    protected override BackendProfile Profile => BackendProfiles.ResumeMargin;
}

[CollectionDefinition(Name)]
public sealed class ResumeMarginConformanceCollection : ICollectionFixture<ResumeMarginConformanceFixture>
{
    public const string Name = "ConformanceResumeMargin";
}

/// <summary>
/// Frozen at 2026-07-04T15:00:00-05:00 (America/Chicago, CDT) — inside the 14:00-16:00 happy-hour
/// window app/backend/config.yaml's business_rules configure, so scenarios needing deterministic
/// happy-hour pricing don't depend on what day or hour the suite happens to run.
/// </summary>
public sealed class FixedClockConformanceFixture : ConformanceFixture
{
    public static readonly DateTimeOffset Instant = new(2026, 7, 4, 15, 0, 0, TimeSpan.FromHours(-5));

    protected override BackendProfile Profile => BackendProfiles.FixedClock(Instant);
}

[CollectionDefinition(Name)]
public sealed class FixedClockConformanceCollection : ICollectionFixture<FixedClockConformanceFixture>
{
    public const string Name = "ConformanceFixedClock";
}
