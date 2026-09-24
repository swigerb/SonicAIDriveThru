using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Issue #9: dedicated FixedClock fixture/collection pairs for the four happy-hour boundary
/// instants the issue calls out — 13:59:59, 14:00:00, 15:59:59, and 16:00:00 store-local time
/// (America/Chicago). Each needs its own Python process (see BackendProfileFixtures.cs's
/// FixedClockConformanceFixture doc: CONFORMANCE_FIXED_NOW is read once at Python-module-import
/// time and can't change for an already-running backend), so testing all four boundaries costs
/// four separate backend launches. Kept in this stream's own subfolder — rather than editing the
/// shared BackendProfileFixtures.cs — since these are new types, not a change to existing ones.
/// All four instants use the same date (2026-07-04, CDT/-05:00) as the pre-existing
/// FixedClockConformanceFixture purely for consistency.
/// </summary>
public sealed class HappyHourJustBeforeOpenFixture : ConformanceFixture
{
    public static readonly DateTimeOffset Instant = new(2026, 7, 4, 13, 59, 59, TimeSpan.FromHours(-5));
    protected override BackendProfile Profile => BackendProfiles.FixedClock(Instant);
}

[CollectionDefinition(Name)]
public sealed class HappyHourJustBeforeOpenCollection : ICollectionFixture<HappyHourJustBeforeOpenFixture>
{
    public const string Name = "ConformanceFixedClockHappyHourJustBeforeOpen";
}

public sealed class HappyHourAtOpenFixture : ConformanceFixture
{
    public static readonly DateTimeOffset Instant = new(2026, 7, 4, 14, 0, 0, TimeSpan.FromHours(-5));
    protected override BackendProfile Profile => BackendProfiles.FixedClock(Instant);
}

[CollectionDefinition(Name)]
public sealed class HappyHourAtOpenCollection : ICollectionFixture<HappyHourAtOpenFixture>
{
    public const string Name = "ConformanceFixedClockHappyHourAtOpen";
}

public sealed class HappyHourJustBeforeCloseFixture : ConformanceFixture
{
    public static readonly DateTimeOffset Instant = new(2026, 7, 4, 15, 59, 59, TimeSpan.FromHours(-5));
    protected override BackendProfile Profile => BackendProfiles.FixedClock(Instant);
}

[CollectionDefinition(Name)]
public sealed class HappyHourJustBeforeCloseCollection : ICollectionFixture<HappyHourJustBeforeCloseFixture>
{
    public const string Name = "ConformanceFixedClockHappyHourJustBeforeClose";
}

public sealed class HappyHourAtCloseFixture : ConformanceFixture
{
    public static readonly DateTimeOffset Instant = new(2026, 7, 4, 16, 0, 0, TimeSpan.FromHours(-5));
    protected override BackendProfile Profile => BackendProfiles.FixedClock(Instant);
}

[CollectionDefinition(Name)]
public sealed class HappyHourAtCloseCollection : ICollectionFixture<HappyHourAtCloseFixture>
{
    public const string Name = "ConformanceFixedClockHappyHourAtClose";
}
