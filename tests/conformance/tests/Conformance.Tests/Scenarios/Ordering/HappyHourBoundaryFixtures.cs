using System.Globalization;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Issue #9: dedicated FixedClock fixture/collection pairs for the happy-hour boundary instants
/// the issue calls out — 13:59:59, 14:00:00, 15:59:59, and 16:00:00 store-local time
/// (America/Chicago), plus (PR #38 review item 5) a winter (CST/-06:00) pair at the opening
/// boundary specifically to catch a hardcoded -05:00/UTC-offset bug the summer-only cases can't:
/// America/Chicago is -06:00 in January, not -05:00. Each needs its own Python process (see
/// BackendProfileFixtures.cs's FixedClockConformanceFixture doc: CONFORMANCE_FIXED_NOW is read
/// once at Python-module-import time and can't change for an already-running backend), so testing
/// every boundary costs a separate backend launch each. Kept in this stream's own subfolder —
/// rather than editing the shared BackendProfileFixtures.cs — since these are new types, not a
/// change to existing ones.
///
/// PR #38 review item 6: the instants themselves are no longer hardcoded literals here — they're
/// parsed from golden-order-pricing.json's `happyHourBoundaryInstants.cases` (by index, in the
/// same order as that array) so the golden file remains the single source of truth for exactly
/// which instants this suite proves are on/off either side of the boundary.
/// </summary>
internal static class HappyHourBoundaryInstantsLoader
{
    public static DateTimeOffset At(int caseIndex)
    {
        var golden = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot());
        return DateTimeOffset.Parse(
            golden.HappyHourBoundaryInstants.Cases[caseIndex].Instant, CultureInfo.InvariantCulture);
    }
}

public sealed class HappyHourJustBeforeOpenFixture : ConformanceFixture
{
    public static readonly DateTimeOffset Instant = HappyHourBoundaryInstantsLoader.At(0);
    protected override BackendProfile Profile => BackendProfiles.FixedClock(Instant);
}

[CollectionDefinition(Name)]
public sealed class HappyHourJustBeforeOpenCollection : ICollectionFixture<HappyHourJustBeforeOpenFixture>
{
    public const string Name = "ConformanceFixedClockHappyHourJustBeforeOpen";
}

public sealed class HappyHourAtOpenFixture : ConformanceFixture
{
    public static readonly DateTimeOffset Instant = HappyHourBoundaryInstantsLoader.At(1);
    protected override BackendProfile Profile => BackendProfiles.FixedClock(Instant);
}

[CollectionDefinition(Name)]
public sealed class HappyHourAtOpenCollection : ICollectionFixture<HappyHourAtOpenFixture>
{
    public const string Name = "ConformanceFixedClockHappyHourAtOpen";
}

public sealed class HappyHourJustBeforeCloseFixture : ConformanceFixture
{
    public static readonly DateTimeOffset Instant = HappyHourBoundaryInstantsLoader.At(2);
    protected override BackendProfile Profile => BackendProfiles.FixedClock(Instant);
}

[CollectionDefinition(Name)]
public sealed class HappyHourJustBeforeCloseCollection : ICollectionFixture<HappyHourJustBeforeCloseFixture>
{
    public const string Name = "ConformanceFixedClockHappyHourJustBeforeClose";
}

public sealed class HappyHourAtCloseFixture : ConformanceFixture
{
    public static readonly DateTimeOffset Instant = HappyHourBoundaryInstantsLoader.At(3);
    protected override BackendProfile Profile => BackendProfiles.FixedClock(Instant);
}

[CollectionDefinition(Name)]
public sealed class HappyHourAtCloseCollection : ICollectionFixture<HappyHourAtCloseFixture>
{
    public const string Name = "ConformanceFixedClockHappyHourAtClose";
}

/// <summary>Winter (CST/-06:00) counterpart of <see cref="HappyHourJustBeforeOpenFixture"/> — PR
/// #38 review item 5.</summary>
public sealed class HappyHourJustBeforeOpenWinterFixture : ConformanceFixture
{
    public static readonly DateTimeOffset Instant = HappyHourBoundaryInstantsLoader.At(4);
    protected override BackendProfile Profile => BackendProfiles.FixedClock(Instant);
}

[CollectionDefinition(Name)]
public sealed class HappyHourJustBeforeOpenWinterCollection : ICollectionFixture<HappyHourJustBeforeOpenWinterFixture>
{
    public const string Name = "ConformanceFixedClockHappyHourJustBeforeOpenWinter";
}

/// <summary>Winter (CST/-06:00) counterpart of <see cref="HappyHourAtOpenFixture"/> — PR #38
/// review item 5.</summary>
public sealed class HappyHourAtOpenWinterFixture : ConformanceFixture
{
    public static readonly DateTimeOffset Instant = HappyHourBoundaryInstantsLoader.At(5);
    protected override BackendProfile Profile => BackendProfiles.FixedClock(Instant);
}

[CollectionDefinition(Name)]
public sealed class HappyHourAtOpenWinterCollection : ICollectionFixture<HappyHourAtOpenWinterFixture>
{
    public const string Name = "ConformanceFixedClockHappyHourAtOpenWinter";
}
