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
        OrderScenarioHelpers.AssertMoneyEqual(scenario.ExpectedTotal, order.GetProperty("total").GetDecimal());

        // PR #38 review item 6: wires up the previously-unenforced expectedComboComplete golden
        // field via the black-box "[SYSTEM HINT: ...]" text tools.py::update_order appends to the
        // model-facing function_call_output whenever get_combo_requirements(...) reports the
        // combo still incomplete (needs a side/drink). This also kills Rick's M1 (PR #38 review
        // item 4): if reset_order stopped zeroing the absorbed-slot counters, the fresh combo
        // added after reset in this scenario's steps would wrongly look complete and this
        // assertion would fail to find the hint.
        if (scenario.ExpectedComboComplete)
        {
            Assert.DoesNotContain("[SYSTEM HINT:", result.FunctionCallOutputText);
        }
        else
        {
            Assert.Contains("[SYSTEM HINT:", result.FunctionCallOutputText);
        }

        // PR #38 review item 4 (Rick's M2/M6): when a conversion/absorption step only consumes
        // part of a standalone line's quantity, the remainder must survive as its own priced
        // line — neither the whole line removed (M2: converting one of two standalone burgers to
        // a combo) nor the whole quantity silently absorbed (M6: a combo needing one side slot
        // sent two units of that side in one call).
        if (scenario.ExpectedStandaloneQuantityAfterConversion is int expectedStandaloneQty)
        {
            var standaloneItem = order.GetProperty("items").EnumerateArray()
                .Single(i => !i.GetProperty("item").GetString()!.Contains("Combo"));
            Assert.Equal(expectedStandaloneQty, standaloneItem.GetProperty("quantity").GetInt32());
        }
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
                ("add", "SONIC® Cheeseburger (No Onions)", "standard", 1, 5.29m),
                ("add", "SONIC® Cheeseburger Combo", "standard", 1, 8.49m),
            ],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        var items = order.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Contains("(No Onions)", items[0].GetProperty("item").GetString());
        OrderScenarioHelpers.AssertMoneyEqual(8.49m, order.GetProperty("total").GetDecimal());
    });

    [Fact(Skip = "app/backend/order_state.py::reset_order clears order_state/absorbed_sides/" +
                 "absorbed_drinks but not the absorbed_side_display/absorbed_drink_display " +
                 "session strings, so the next combo's display carries over the previous order's " +
                 "absorbed component names -- #41. Not fixing Python; tracked for the C# backend.")]
    public Task Reset_order_clears_the_previous_orders_absorbed_component_display() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        // Establish stale absorbed_side_display/absorbed_drink_display state (order_state.py:98-99,
        // 179-191) with a combo whose side (Tots) and drink (Cherry Limeade) both get absorbed and
        // recorded onto the *session*, not just the order line.
        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [
                ("add", "SONIC® Cheeseburger Combo", "standard", 1, 8.49m),
                ("add", "Tots", "medium", 1, 2.79m),
                ("add", "Cherry Limeade", "medium", 1, 2.89m),
                ("reset", "", "", 0, 0m),
                // A fresh combo of the same kind, then only ONE new side (Onion Rings) absorbed.
                // The reported #41 example is literally this shape ("...Combo w/ Medium Tots &
                // Medium Cherry Limeade & Medium Onion Rings" after reset+fresh-combo+one side).
                ("add", "SONIC® Cheeseburger Combo", "standard", 1, 8.49m),
                ("add", "Onion Rings", "medium", 1, 3.89m),
            ],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        var comboDisplay = order.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("item").GetString()!.Contains("Combo"))
            .GetProperty("display").GetString();

        Assert.Contains("Onion Rings", comboDisplay);
        Assert.DoesNotContain("Tots", comboDisplay);
        Assert.DoesNotContain("Cherry Limeade", comboDisplay);
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
        OrderScenarioHelpers.AssertMoneyEqual(combo.Price, order.GetProperty("total").GetDecimal());
        OrderScenarioHelpers.AssertMoneyEqual(combo.ExpectedTax, order.GetProperty("tax").GetDecimal());
        OrderScenarioHelpers.AssertMoneyEqual(combo.ExpectedFinalTotal, order.GetProperty("finalTotal").GetDecimal());
    });
}

/// <summary>
/// The one comboAbsorptionScenarios golden case that specifically expects happy hour ON
/// (test_combo_orders.py::TestComboHappyHour::test_combo_plus_extra_standalone_drink_discounted):
/// a fully-satisfied combo plus an extra standalone drink beyond the one absorbed slot, where only
/// the extra drink is halved -- the combo price and the already-absorbed drink are untouched.
/// Needs the HappyHourAtOpen FixedClock (14:00:00) rather than ComboAbsorptionTests's off-happy-hour
/// fixture, so it gets its own top-level class/collection. Also runs the 10-real-combo-items
/// Theory again under this same fixture (PR #38 review item 7): combos are never themselves
/// classified as "drinks" (see test_combo_orders.py::test_combo_price_not_discounted_during_happy_hour),
/// so their price/tax/finalTotal must come out byte-for-byte identical to the off-happy-hour run.
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
        OrderScenarioHelpers.AssertMoneyEqual(scenario.ExpectedTotal, order.GetProperty("total").GetDecimal());
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
    public Task All_ten_real_combo_menu_items_price_correctly_to_the_cent_during_happy_hour(int comboIndex) => fixture.RunAsync(async () =>
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
        // A combo's bundle price is never itself classified as a "drink" line, so happy hour
        // being active must make no difference at all -- identical to the off-happy-hour Theory.
        OrderScenarioHelpers.AssertMoneyEqual(combo.Price, order.GetProperty("total").GetDecimal());
        OrderScenarioHelpers.AssertMoneyEqual(combo.ExpectedTax, order.GetProperty("tax").GetDecimal());
        OrderScenarioHelpers.AssertMoneyEqual(combo.ExpectedFinalTotal, order.GetProperty("finalTotal").GetDecimal());
    });
}
