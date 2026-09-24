using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Issue #9: happy hour (50% off drinks, 14:00-16:00 store time) at all four requested boundary
/// instants — 13:59:59, 14:00:00, 15:59:59, 16:00:00 — using a dedicated FixedClock backend per
/// instant (see HappyHourBoundaryFixtures.cs), plus proof a non-drink item is unaffected while the
/// discount is active. app/backend/order_state.py's is_happy_hour() only compares now().hour, so
/// 13:59:59 and 16:00:00 are both "off" and 14:00:00/15:59:59 are both "on" — see the golden
/// dataset's happyHourBoundaryInstants block for the source of these expectations.
/// </summary>
file static class HappyHourBoundaryTestSupport
{
    public const string DrinkItemName = "Cherry Limeade";
    public const double DrinkPrice = 4.00;

    public static async Task<double> AddOneDrinkAndReadFinalTotalAsync(ConformanceFixture fixture, CancellationToken ct)
    {
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", DrinkItemName, "Regular", 1, DrinkPrice)],
            roundTripIndex, ct);

        return OrderScenarioHelpers.GetOrderFinalTotal(result.ToolResultJson!);
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
        Assert.Equal(HappyHourBoundaryTestSupport.DrinkPrice * 1.08, finalTotal, precision: 2);
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
        Assert.Equal(HappyHourBoundaryTestSupport.DrinkPrice * 0.5 * 1.08, finalTotal, precision: 2);
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
        Assert.Equal(HappyHourBoundaryTestSupport.DrinkPrice * 0.5 * 1.08, finalTotal, precision: 2);
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
        Assert.Equal(HappyHourBoundaryTestSupport.DrinkPrice * 1.08, finalTotal, precision: 2);
    });

    [Fact]
    public Task A_non_drink_item_is_unaffected_by_happy_hour_pricing_logic() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", "Tots", "medium", 1, 2.79)],
            roundTripIndex, ct);

        // Paired with HappyHourAtOpen/JustBeforeClose's drink-item assertions (both taken during
        // an active window) this demonstrates order_state.py::_update_summary's
        // `_infer_combo_component(...) == "drinks"` guard is item-category-scoped, not merely
        // clock-scoped: a non-drink item's price is never discounted regardless of the clock.
        Assert.Equal(2.79 * 1.08, OrderScenarioHelpers.GetOrderFinalTotal(result.ToolResultJson!), precision: 2);
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
        Assert.Equal(taxCase.ExpectedSubtotal, order.GetProperty("total").GetDouble(), precision: 2);
        Assert.Equal(taxCase.ExpectedTax, order.GetProperty("tax").GetDouble(), precision: 2);
        Assert.Equal(taxCase.ExpectedFinalTotal, order.GetProperty("finalTotal").GetDouble(), precision: 2);
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
        Assert.Equal(taxCase.ExpectedSubtotal, order.GetProperty("total").GetDouble(), precision: 2);
        Assert.Equal(taxCase.ExpectedTax, order.GetProperty("tax").GetDouble(), precision: 2);
        Assert.Equal(taxCase.ExpectedFinalTotal, order.GetProperty("finalTotal").GetDouble(), precision: 2);
    });
}
