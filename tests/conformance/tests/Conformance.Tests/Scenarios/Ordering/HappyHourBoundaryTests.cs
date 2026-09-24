using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Issue #9: happy hour (50% off drinks, 14:00-16:00 store time) at all six requested boundary
/// instants — 13:59:59, 14:00:00, 15:59:59, 16:00:00 (summer/CDT), plus a winter (CST/-06:00)
/// 13:59:59/14:00:00 pair (PR #38 review item 5) — using a dedicated FixedClock backend per
/// instant (see HappyHourBoundaryFixtures.cs), plus proof a non-drink item is unaffected while the
/// discount is active. app/backend/order_state.py's is_happy_hour() only compares now().hour, so
/// 13:59:59 and 16:00:00 are both "off" and 14:00:00/15:59:59 are both "on" — see the golden
/// dataset's happyHourBoundaryInstants block for the source of these expectations. Tax rate and
/// happy-hour discount are read from the golden file's businessRules block (PR #38 review item 6)
/// rather than hardcoded here, so both this suite and a golden-data update stay in lockstep.
/// </summary>
file static class HappyHourBoundaryTestSupport
{
    public const string DrinkItemName = "Cherry Limeade";
    public const decimal DrinkPrice = 4.00m;

    public static async Task<decimal> AddOneDrinkAndReadFinalTotalAsync(ConformanceFixture fixture, CancellationToken ct)
    {
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", DrinkItemName, "Regular", 1, DrinkPrice)],
            roundTripIndex, ct);

        return OrderScenarioHelpers.GetOrderFinalTotal(result.ToolResultJson!);
    }

    public static decimal FullPriceFinalTotal(decimal unitPrice) =>
        unitPrice * (1 + GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot()).BusinessRules.TaxRate);

    public static decimal HappyHourFinalTotal(decimal unitPrice)
    {
        var rules = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot()).BusinessRules;
        return unitPrice * rules.HappyHourDiscount * (1 + rules.TaxRate);
    }
}

[Collection(HappyHourJustBeforeOpenCollection.Name)]
public sealed class HappyHourJustBeforeOpenTests(HappyHourJustBeforeOpenFixture fixture)
{
    [Fact]
    public Task At_13_59_59_happy_hour_is_not_yet_active() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var finalTotal = await HappyHourBoundaryTestSupport.AddOneDrinkAndReadFinalTotalAsync(fixture, ct);
        OrderScenarioHelpers.AssertMoneyEqual(
            HappyHourBoundaryTestSupport.FullPriceFinalTotal(HappyHourBoundaryTestSupport.DrinkPrice), finalTotal);
    });
}

[Collection(HappyHourAtOpenCollection.Name)]
public sealed class HappyHourAtOpenTests(HappyHourAtOpenFixture fixture)
{
    [Fact]
    public Task At_14_00_00_happy_hour_is_active() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var finalTotal = await HappyHourBoundaryTestSupport.AddOneDrinkAndReadFinalTotalAsync(fixture, ct);
        OrderScenarioHelpers.AssertMoneyEqual(
            HappyHourBoundaryTestSupport.HappyHourFinalTotal(HappyHourBoundaryTestSupport.DrinkPrice), finalTotal);
    });
}

[Collection(HappyHourJustBeforeCloseCollection.Name)]
public sealed class HappyHourJustBeforeCloseTests(HappyHourJustBeforeCloseFixture fixture)
{
    [Fact]
    public Task At_15_59_59_happy_hour_is_still_active() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var finalTotal = await HappyHourBoundaryTestSupport.AddOneDrinkAndReadFinalTotalAsync(fixture, ct);
        OrderScenarioHelpers.AssertMoneyEqual(
            HappyHourBoundaryTestSupport.HappyHourFinalTotal(HappyHourBoundaryTestSupport.DrinkPrice), finalTotal);
    });
}

[Collection(HappyHourAtCloseCollection.Name)]
public sealed class HappyHourAtCloseTests(HappyHourAtCloseFixture fixture)
{
    [Fact]
    public Task At_16_00_00_happy_hour_has_ended() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var finalTotal = await HappyHourBoundaryTestSupport.AddOneDrinkAndReadFinalTotalAsync(fixture, ct);
        OrderScenarioHelpers.AssertMoneyEqual(
            HappyHourBoundaryTestSupport.FullPriceFinalTotal(HappyHourBoundaryTestSupport.DrinkPrice), finalTotal);
    });

    [Fact]
    public Task A_non_drink_item_is_unaffected_by_happy_hour_pricing_logic() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", "Tots", "medium", 1, 2.79m)],
            roundTripIndex, ct);

        // Paired with HappyHourAtOpen/JustBeforeClose's drink-item assertions (both taken during
        // an active window) this demonstrates order_state.py::_update_summary's
        // `_infer_combo_component(...) == "drinks"` guard is item-category-scoped, not merely
        // clock-scoped: a non-drink item's price is never discounted regardless of the clock.
        OrderScenarioHelpers.AssertMoneyEqual(
            HappyHourBoundaryTestSupport.FullPriceFinalTotal(2.79m),
            OrderScenarioHelpers.GetOrderFinalTotal(result.ToolResultJson!));
    });
}

/// <summary>Winter (CST/-06:00) counterpart of <see cref="HappyHourJustBeforeOpenTests"/> — PR #38
/// review item 5: proves the boundary hour comparison isn't hiding a hardcoded -05:00/summer-only
/// UTC offset, since America/Chicago is -06:00 in January.</summary>
[Collection(HappyHourJustBeforeOpenWinterCollection.Name)]
public sealed class HappyHourJustBeforeOpenWinterTests(HappyHourJustBeforeOpenWinterFixture fixture)
{
    [Fact]
    public Task At_13_59_59_CST_happy_hour_is_not_yet_active() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var finalTotal = await HappyHourBoundaryTestSupport.AddOneDrinkAndReadFinalTotalAsync(fixture, ct);
        OrderScenarioHelpers.AssertMoneyEqual(
            HappyHourBoundaryTestSupport.FullPriceFinalTotal(HappyHourBoundaryTestSupport.DrinkPrice), finalTotal);
    });
}

/// <summary>Winter (CST/-06:00) counterpart of <see cref="HappyHourAtOpenTests"/> — PR #38 review
/// item 5.</summary>
[Collection(HappyHourAtOpenWinterCollection.Name)]
public sealed class HappyHourAtOpenWinterTests(HappyHourAtOpenWinterFixture fixture)
{
    [Fact]
    public Task At_14_00_00_CST_happy_hour_is_active() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var finalTotal = await HappyHourBoundaryTestSupport.AddOneDrinkAndReadFinalTotalAsync(fixture, ct);
        OrderScenarioHelpers.AssertMoneyEqual(
            HappyHourBoundaryTestSupport.HappyHourFinalTotal(HappyHourBoundaryTestSupport.DrinkPrice), finalTotal);
    });
}

// Runs under a FixedClock pinned outside 14:00-16:00 rather than the ambient ConformanceCollection
// (real wall-clock time): several of these golden cases add drink items, so if the suite happened
// to run during the real happy-hour window their expected totals would be wrong non-deterministically.
[Collection(HappyHourJustBeforeOpenCollection.Name)]
public sealed class TaxToTheCentOffHappyHourTests(HappyHourJustBeforeOpenFixture fixture)
{
    public static TheoryData<int> OffHappyHourTaxCaseIndexes()
    {
        var golden = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot());
        var data = new TheoryData<int>();
        for (var i = 0; i < golden.TaxCases.Count; i++)
        {
            if (!golden.TaxCases[i].HappyHour) data.Add(i);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(OffHappyHourTaxCaseIndexes))]
    public Task Tax_and_totals_match_to_the_cent(int caseIndex) => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var golden = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot());
        var taxCase = golden.TaxCases[caseIndex];

        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var steps = taxCase.Items.Select(i => ("add", i.Item, i.Size, i.Quantity, i.UnitPrice));
        var result = await OrderScenarioHelpers.RunOrderStepsAsync(connection, browser, steps, roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        OrderScenarioHelpers.AssertMoneyEqual(taxCase.ExpectedSubtotal, order.GetProperty("total").GetDecimal());
        OrderScenarioHelpers.AssertMoneyEqual(taxCase.ExpectedTax, order.GetProperty("tax").GetDecimal());
        OrderScenarioHelpers.AssertMoneyEqual(taxCase.ExpectedFinalTotal, order.GetProperty("finalTotal").GetDecimal());
    });
}

[Collection(HappyHourAtOpenCollection.Name)]
public sealed class TaxToTheCentDuringHappyHourTests(HappyHourAtOpenFixture fixture)
{
    public static TheoryData<int> OnHappyHourTaxCaseIndexes()
    {
        var golden = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot());
        var data = new TheoryData<int>();
        for (var i = 0; i < golden.TaxCases.Count; i++)
        {
            if (golden.TaxCases[i].HappyHour) data.Add(i);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(OnHappyHourTaxCaseIndexes))]
    public Task Tax_and_totals_match_to_the_cent_during_happy_hour(int caseIndex) => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var golden = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot());
        var taxCase = golden.TaxCases[caseIndex];

        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var steps = taxCase.Items.Select(i => ("add", i.Item, i.Size, i.Quantity, i.UnitPrice));
        var result = await OrderScenarioHelpers.RunOrderStepsAsync(connection, browser, steps, roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        OrderScenarioHelpers.AssertMoneyEqual(taxCase.ExpectedSubtotal, order.GetProperty("total").GetDecimal());
        OrderScenarioHelpers.AssertMoneyEqual(taxCase.ExpectedTax, order.GetProperty("tax").GetDecimal());
        OrderScenarioHelpers.AssertMoneyEqual(taxCase.ExpectedFinalTotal, order.GetProperty("finalTotal").GetDecimal());
    });
}
