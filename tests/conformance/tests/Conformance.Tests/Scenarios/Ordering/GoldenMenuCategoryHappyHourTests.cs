using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Issue #39 / PR #50 review: a representative subset (not the full 60-item table -- that's
/// already exhaustively covered by app/backend/tests/test_menu_utils.py at the unit level, and by
/// GoldenMenuComboSlotTheoryTests.cs's combo-absorption Theory at the end-to-end level) of
/// tests/conformance/testdata/golden-menu-categories.json exercised end-to-end against the live
/// backend during happy hour, proving each item's golden HappyHourDiscounted flag actually drives
/// observable pricing behaviour (not just the Python-internal classification). Asserts directly
/// against HappyHourDiscounted -- NOT re-derived from ComboSlot=="drinks" -- because the two are
/// independent questions (PR #50 review): items can in principle be happy-hour-discounted without
/// filling a combo drink slot (or vice versa), even though they agree on every item today.
/// Complements HappyHourBoundaryTests.cs's Ched 'R' Peppers regression case with broader coverage
/// across categories and both exception buckets.
/// </summary>
[Collection(HappyHourAtOpenCollection.Name)]
public sealed class GoldenMenuCategoryHappyHourTests(HappyHourAtOpenFixture fixture)
{
    // (item, size, unit price -- app/frontend/src/data/menuItems.json)
    public static TheoryData<string, string, decimal> RepresentativeItems() => new()
    {
        { "SuperSONIC® Double Cheeseburger", "Standard", 6.59m }, // never happy-hour-discounted: a burger, never a combo slot filler
        { "Onion Rings", "Small", 3.19m }, // never happy-hour-discounted: not a combo side either post-PR #50 (was wrongly "sides")
        { "Banana Classic Shake", "Mini", 3.39m }, // never happy-hour-discounted: Shakes & Ice Cream, but Brian's decision (2026-09-25) is full price
        { "Hot Fudge Sundae", "Standard", 3.19m }, // never happy-hour-discounted: Shakes & Ice Cream category, but sundaes are full price (Brian's decision)
        { "Corn Dog", "Standard", 1.99m }, // never happy-hour-discounted: Hot Dogs & Tots category, but a real entree, not a fillable side
    };

    [Theory]
    [MemberData(nameof(RepresentativeItems))]
    public Task Golden_happy_hour_discounted_flag_determines_the_happy_hour_discount(string item, string size, decimal unitPrice) =>
        fixture.RunAsync(async () =>
        {
            var ct = TestContext.Current.CancellationToken;
            var golden = GoldenMenuCategoryData.Load(RepoPaths.FindRepoRoot());
            var categoryCase = golden.Items.Single(c => c.Item == item);

            var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
            await using var _ = browser;

            var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                connection, browser,
                [("add", item, size, 1, unitPrice)],
                roundTripIndex, ct);

            var finalTotal = OrderScenarioHelpers.GetOrderFinalTotal(result.ToolResultJson!);
            var rules = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot()).BusinessRules;
            var expectedTotal = categoryCase.HappyHourDiscounted
                ? unitPrice * rules.HappyHourDiscount * (1 + rules.TaxRate)
                : unitPrice * (1 + rules.TaxRate);

            OrderScenarioHelpers.AssertMoneyEqual(expectedTotal, finalTotal);
        });
}
