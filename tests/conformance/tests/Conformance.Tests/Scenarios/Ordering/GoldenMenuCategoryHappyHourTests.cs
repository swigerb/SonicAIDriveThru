using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Issue #39: a representative subset (not the full 60-item table -- that's already exhaustively
/// covered by app/backend/tests/test_menu_utils.py at the unit level) of
/// tests/conformance/testdata/golden-menu-categories.json exercised end-to-end against the live
/// backend during happy hour, proving the golden bucket for each item actually drives observable
/// pricing behaviour (not just the Python-internal classification): bucket "drinks" gets the 50%
/// happy-hour discount, "sides" and "none" (including both kinds of same-JSON-category exception --
/// sundaes under Shakes &amp; Ice Cream, hot-dog entrees under Hot Dogs &amp; Tots) do not.
/// Complements HappyHourBoundaryTests.cs's Ched 'R' Peppers regression case with broader coverage
/// across categories and both exception buckets.
/// </summary>
[Collection(HappyHourAtOpenCollection.Name)]
public sealed class GoldenMenuCategoryHappyHourTests(HappyHourAtOpenFixture fixture)
{
    // (item, size, unit price -- app/frontend/src/data/menuItems.json)
    public static TheoryData<string, string, decimal> RepresentativeItems() => new()
    {
        { "SuperSONIC® Double Cheeseburger", "Standard", 6.59m }, // bucket "none": a burger, never a combo slot filler
        { "Onion Rings", "Small", 3.19m }, // bucket "sides": Extras & Sides
        { "Banana Classic Shake", "Mini", 3.39m }, // bucket "drinks": Shakes & Ice Cream
        { "Hot Fudge Sundae", "Standard", 3.19m }, // bucket "none" exception: Shakes & Ice Cream category, but sundaes are full price (Brian's decision)
        { "Corn Dog", "Standard", 1.99m }, // bucket "none" exception: Hot Dogs & Tots category, but a real entree, not a fillable side
    };

    [Theory]
    [MemberData(nameof(RepresentativeItems))]
    public Task Golden_bucket_determines_the_happy_hour_discount(string item, string size, decimal unitPrice) =>
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
            var expectedTotal = categoryCase.Bucket == "drinks"
                ? unitPrice * rules.HappyHourDiscount * (1 + rules.TaxRate)
                : unitPrice * (1 + rules.TaxRate);

            OrderScenarioHelpers.AssertMoneyEqual(expectedTotal, finalTotal);
        });
}
