using System.Linq;
using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Issue #9: combo scenarios, including component absorption. Data-driven over the
/// `comboAbsorptionScenarios` table in golden-order-pricing.json (ported verbatim from
/// app/backend/tests/test_combo_orders.py), plus a Theory proving all 10 real combo items from
/// app/frontend/src/data/menuItems.json price correctly through the same tax pipeline used
/// elsewhere in this stream.
///
/// The absorption Theory (and the mods-carried Fact) run under a FixedClock pinned just before
/// the happy-hour window opens rather than the ambient ConformanceCollection (real wall-clock
/// time): several scenarios add standalone drinks, so their expected totals would be wrong
/// non-deterministically if the suite happened to run during the real 14:00-16:00 window. The one
/// golden scenario that specifically expects happy hour ON gets its own dedicated Fact under the
/// HappyHourAtOpen FixedClock instead (see Combo_plus_extra_standalone_drink_is_discounted_during_happy_hour).
/// </summary>
[Collection(HappyHourJustBeforeOpenCollection.Name)]
public sealed class ComboAbsorptionTests(HappyHourJustBeforeOpenFixture fixture)
{
    public static TheoryData<int> AbsorptionScenarioIndexes()
    {
        var golden = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot());
        var data = new TheoryData<int>();
        for (var i = 0; i < golden.ComboAbsorptionScenarios.Count; i++)
        {
            if (!golden.ComboAbsorptionScenarios[i].HappyHour) data.Add(i);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AbsorptionScenarioIndexes))]
    public Task Combo_absorption_scenarios_match_the_golden_line_item_count_and_total(int scenarioIndex) => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var golden = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot());
        var scenario = golden.ComboAbsorptionScenarios[scenarioIndex];

        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var steps = scenario.Steps.Select(s => (s.Action, s.Item, s.Size, s.Quantity, s.Price));
        var result = await OrderScenarioHelpers.RunOrderStepsAsync(connection, browser, steps, roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        Assert.Equal(scenario.ExpectedLineItemCount, order.GetProperty("items").GetArrayLength());
        Assert.Equal(scenario.ExpectedTotal, order.GetProperty("total").GetDouble(), precision: 2);
    });

    [Fact]
    public Task Combo_conversion_carries_parenthesized_mods_from_the_standalone_item() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [
                ("add", "SONIC® Cheeseburger (No Onions)", "standard", 1, 5.29),
                ("add", "SONIC® Cheeseburger Combo", "standard", 1, 8.49),
            ],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        var items = order.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Contains("(No Onions)", items[0].GetProperty("item").GetString());
        Assert.Equal(8.49, order.GetProperty("total").GetDouble(), precision: 2);
    });

    public static TheoryData<int> ComboItemIndexes()
    {
        var golden = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot());
        var data = new TheoryData<int>();
        for (var i = 0; i < golden.Combos.Items.Count; i++)
        {
            data.Add(i);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ComboItemIndexes))]
    public Task All_ten_real_combo_menu_items_price_correctly_to_the_cent(int comboIndex) => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var golden = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot());
        Assert.Equal(10, golden.Combos.ExpectedCount);
        var combo = golden.Combos.Items[comboIndex];

        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", combo.Name, combo.Size, 1, combo.Price)],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        Assert.Equal(combo.Price, order.GetProperty("total").GetDouble(), precision: 2);
        Assert.Equal(combo.ExpectedTax, order.GetProperty("tax").GetDouble(), precision: 2);
        Assert.Equal(combo.ExpectedFinalTotal, order.GetProperty("finalTotal").GetDouble(), precision: 2);
    });
}

/// <summary>
/// The one comboAbsorptionScenarios golden case that specifically expects happy hour ON
/// (test_combo_orders.py::TestComboHappyHour::test_combo_plus_extra_standalone_drink_discounted):
/// a fully-satisfied combo plus an extra standalone drink beyond the one absorbed slot, where only
/// the extra drink is halved -- the combo price and the already-absorbed drink are untouched.
/// Needs the HappyHourAtOpen FixedClock (14:00:00) rather than ComboAbsorptionTests's off-happy-hour
/// fixture, so it gets its own top-level class/collection.
/// </summary>
[Collection(HappyHourAtOpenCollection.Name)]
public sealed class ComboAbsorptionHappyHourTests(HappyHourAtOpenFixture fixture)
{
    [Fact]
    public Task Combo_plus_extra_standalone_drink_is_discounted_during_happy_hour() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var golden = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot());
        var scenario = golden.ComboAbsorptionScenarios.Single(s => s.HappyHour);

        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var steps = scenario.Steps.Select(s => (s.Action, s.Item, s.Size, s.Quantity, s.Price));
        var result = await OrderScenarioHelpers.RunOrderStepsAsync(connection, browser, steps, roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        Assert.Equal(scenario.ExpectedLineItemCount, order.GetProperty("items").GetArrayLength());
        Assert.Equal(scenario.ExpectedTotal, order.GetProperty("total").GetDouble(), precision: 2);
    });
}
