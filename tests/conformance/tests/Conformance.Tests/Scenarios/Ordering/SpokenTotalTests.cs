using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// PR #38 second re-review should-fix 2: the spoken `$X.XX` text embedded in the
/// `function_call_output` (tools.py's `delta_text`, e.g. "Added 1 Tots — your total is now
/// $9.47") is a *display* concern, separate from the exact-decimal wire contract asserted
/// elsewhere in this suite. This still needs its own assertion because Rick's N2 mutation
/// (building that spoken text from the pre-tax subtotal instead of finalTotal) doesn't touch any
/// JSON money field and so is invisible to every other scenario in this file.
///
/// The display-rounding rule (README "Rendering money for display": round half away from zero on
/// the exact decimal) is distinct from the wire contract's "no rounding, ever" rule. Golden cases
/// whose finalTotal lands exactly on a half cent are mathematically impossible to assert against
/// the live Python backend today -- its `float:.2f` formatting reproduces no single consistent
/// rounding convention for such totals (Rick's 200k-order simulation found hundreds of
/// disagreements) -- so that specific case stays `Skip`'d referencing #46. The non-half-cent case
/// stays active and alone is sufficient to catch N2 today.
/// </summary>
[Collection(HappyHourJustBeforeOpenCollection.Name)]
public sealed class SpokenTotalTests(HappyHourJustBeforeOpenFixture fixture)
{
    [Fact]
    public Task Spoken_total_text_matches_the_exact_final_total_non_half_cent() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var golden = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot());
        var spokenCase = golden.SpokenTotalCases.Single(c => !c.LandsOnHalfCent);
        Assert.False(spokenCase.HappyHour, "This fixture pins the clock outside the happy-hour window.");

        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var steps = spokenCase.Steps.Select(s => (s.Action, s.Item, s.Size, s.Quantity, s.Price));
        var result = await OrderScenarioHelpers.RunOrderStepsAsync(connection, browser, steps, roundTripIndex, ct);

        // Confirms the wire finalTotal is what the golden case expects before checking the spoken
        // text derives from it -- otherwise a failure here could just as easily mean the golden
        // case itself drifted from menu prices, not that N2 regressed.
        OrderScenarioHelpers.AssertMoneyEqual(
            spokenCase.ExpectedFinalTotal, OrderScenarioHelpers.GetOrderFinalTotal(result.ToolResultJson!));

        Assert.Contains(spokenCase.ExpectedSpokenTotalText, result.FunctionCallOutputText, StringComparison.Ordinal);
    });

}

// Separate class/collection because this case needs happy hour ACTIVE (the half-cent finalTotal
// only arises from a happy-hour-halved odd-cent drink price -- see the golden entry's
// description), unlike the active non-half-cent case above.
[Collection(HappyHourAtOpenCollection.Name)]
public sealed class SpokenTotalHalfCentTests(HappyHourAtOpenFixture fixture)
{
    [Fact(Skip = "Lands exactly on a half cent; Python's float `:.2f` formatting does not " +
                 "reproduce any single consistent rounding convention for such totals -- #46.")]
    public Task Spoken_total_text_matches_the_exact_final_total_half_cent() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var golden = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot());
        var spokenCase = golden.SpokenTotalCases.Single(c => c.LandsOnHalfCent);
        Assert.True(spokenCase.HappyHour, "This fixture pins the clock inside the happy-hour window.");

        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var steps = spokenCase.Steps.Select(s => (s.Action, s.Item, s.Size, s.Quantity, s.Price));
        var result = await OrderScenarioHelpers.RunOrderStepsAsync(connection, browser, steps, roundTripIndex, ct);

        OrderScenarioHelpers.AssertMoneyEqual(
            spokenCase.ExpectedFinalTotal, OrderScenarioHelpers.GetOrderFinalTotal(result.ToolResultJson!));

        Assert.Contains(spokenCase.ExpectedSpokenTotalText, result.FunctionCallOutputText, StringComparison.Ordinal);
    });
}
