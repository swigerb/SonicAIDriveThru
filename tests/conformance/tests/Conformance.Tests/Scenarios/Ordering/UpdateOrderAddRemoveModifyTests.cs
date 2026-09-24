using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Issue #9: `update_order` add/remove/modify scenarios — quantities, sizes (including Route 44),
/// zero/negative price rejection, and both the per-item and whole-order quantity limits. Scripts
/// real scripted-function-call turns against the real Python backend (see
/// OrderScenarioHelpers.cs) and asserts on the `tool_result` JSON order summary
/// (extension.middle_tier_tool_response) that reaches the browser, exactly as
/// app/backend/tests/test_order_state.py and test_tool_calling.py assert against the in-process
/// order state directly.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class UpdateOrderAddRemoveModifyTests(ConformanceFixture fixture)
{
    [Fact]
    public Task Add_single_item_creates_one_line_with_correct_total() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", "Tots", "medium", 1, 2.79)],
            roundTripIndex, ct);

        Assert.NotNull(result.ToolResultJson);
        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        Assert.Equal(1, order.GetProperty("items").GetArrayLength());
        Assert.Equal(2.79, order.GetProperty("total").GetDouble(), precision: 2);
    });

    [Fact]
    public Task Adding_the_same_item_and_size_twice_merges_into_one_line_with_summed_quantity() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        // Non-drink item deliberately: this class runs under the ambient ConformanceCollection
        // (real wall-clock time, see BackendProfiles.Default), so a drink item's price would be
        // non-deterministic depending on whether the suite happens to run during the real
        // 14:00-16:00 happy-hour window. Matches the same precedent as UpdateOrderToolCallTests.cs.
        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [
                ("add", "Tots", "medium", 1, 2.79),
                ("add", "Tots", "medium", 2, 2.79),
            ],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        var items = order.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(3, items[0].GetProperty("quantity").GetInt32());
        Assert.Equal(3 * 2.79, order.GetProperty("total").GetDouble(), precision: 2);
    });

    [Fact]
    public Task Removing_part_of_a_quantity_decrements_the_line_instead_of_deleting_it() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [
                ("add", "Tots", "medium", 3, 2.79),
                ("remove", "Tots", "medium", 1, 2.79),
            ],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        var items = order.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(2, items[0].GetProperty("quantity").GetInt32());
    });

    [Fact]
    public Task Removing_the_full_quantity_clears_the_line_entirely() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [
                ("add", "Tots", "medium", 1, 2.79),
                ("remove", "Tots", "medium", 1, 2.79),
            ],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        Assert.Equal(0, order.GetProperty("items").GetArrayLength());
        Assert.Equal(0.0, order.GetProperty("total").GetDouble());
    });

    [Fact]
    public Task Removing_an_item_that_was_never_added_is_a_safe_no_op() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("remove", "Onion Rings", "medium", 1, 3.89)],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        Assert.Equal(0, order.GetProperty("items").GetArrayLength());
    });

    [Fact]
    public Task Adding_an_item_at_zero_or_negative_price_is_rejected_and_nothing_is_added() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        // tools.py::update_order rejects action=="add" && price<=0.0 with a TO_SERVER-only apology
        // (never reaches the browser), so this step must be scripted with toClient:false or the
        // wait for extension.middle_tier_tool_response would time out.
        var rejected = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            """{"action":"add","item_name":"Tots","size":"medium","quantity":1,"price":0.0}""",
            "call_zero_price", roundTripIndex, ct, toClient: false);
        Assert.Null(rejected.ToolResultJson);

        var result = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "get_order", "{}", "call_get_after_zero_price", rejected.RoundTripIndex, ct);
        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        Assert.Equal(0, order.GetProperty("items").GetArrayLength());
    });

    [Theory]
    [InlineData("rt44")]
    [InlineData("rt 44")]
    [InlineData("route 44")]
    [InlineData("44")]
    [InlineData("44oz")]
    public Task Route_44_size_aliases_all_display_as_Route_44(string sizeAlias) => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var repoRoot = RepoPaths.FindRepoRoot();
        var golden = GoldenOrderPricingData.Load(repoRoot);
        Assert.Contains(sizeAlias, golden.Route44.Aliases);

        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", "Cherry Limeade", sizeAlias, 1, 3.79)],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        var items = order.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal($"{golden.Route44.ExpectedDisplayPrefix} Cherry Limeade", items[0].GetProperty("display").GetString());
    });

    [Fact]
    public Task Adding_up_to_the_per_item_quantity_limit_succeeds_but_one_more_is_rejected() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var golden = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot());
        var max = golden.QuantityLimits.MaxItemQuantity;

        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var atLimit = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", "Tots", "medium", max, 2.79)],
            roundTripIndex, ct);
        var orderAtLimit = JsonDocument.Parse(atLimit.ToolResultJson!).RootElement;
        Assert.Equal(max, orderAtLimit.GetProperty("items")[0].GetProperty("quantity").GetInt32());

        // One more of the same item+size pushes the per-item total over MAX_QUANTITY_PER_ITEM —
        // tools.py rejects the whole call (TO_SERVER-only apology), so quantity must stay unchanged.
        var overLimit = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            """{"action":"add","item_name":"Tots","size":"medium","quantity":1,"price":2.79}""",
            "call_over_item_limit", atLimit.RoundTripIndex, ct, toClient: false);
        Assert.Null(overLimit.ToolResultJson);

        var result = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "get_order", "{}", "call_get_after_item_limit", overLimit.RoundTripIndex, ct);
        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        Assert.Equal(1, order.GetProperty("items").GetArrayLength());
        Assert.Equal(max, order.GetProperty("items")[0].GetProperty("quantity").GetInt32());
    });

    [Fact]
    public Task Exceeding_the_whole_order_item_limit_is_rejected_while_staying_at_the_cap() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var golden = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot());
        var maxTotal = golden.QuantityLimits.MaxOrderItems;
        var maxPerItem = golden.QuantityLimits.MaxItemQuantity;
        Assert.True(maxTotal > maxPerItem, "This scenario assumes the whole-order cap exceeds the per-item cap.");

        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        // Fill the order to exactly MAX_TOTAL_ITEMS using distinct item+size lines (so no single
        // line ever exceeds MAX_QUANTITY_PER_ITEM): maxPerItem + maxPerItem + remainder.
        var remainder = maxTotal - (2 * maxPerItem);
        Assert.True(remainder > 0 && remainder < maxPerItem, "Golden quantity limits changed shape; adjust this fill plan.");

        var filled = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [
                ("add", "Tots", "medium", maxPerItem, 2.79),
                ("add", "Onion Rings", "medium", maxPerItem, 3.89),
                ("add", "Groovy Fries", "medium", remainder, 2.79),
            ],
            roundTripIndex, ct);
        var orderFilled = JsonDocument.Parse(filled.ToolResultJson!).RootElement;
        var totalQty = 0;
        foreach (var item in orderFilled.GetProperty("items").EnumerateArray())
        {
            totalQty += item.GetProperty("quantity").GetInt32();
        }
        Assert.Equal(maxTotal, totalQty);

        var overLimit = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            """{"action":"add","item_name":"Mozzarella Sticks","size":"medium","quantity":1,"price":4.29}""",
            "call_over_total_limit", filled.RoundTripIndex, ct, toClient: false);
        Assert.Null(overLimit.ToolResultJson);

        var result = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "get_order", "{}", "call_get_after_total_limit", overLimit.RoundTripIndex, ct);
        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        var finalQty = 0;
        foreach (var item in order.GetProperty("items").EnumerateArray())
        {
            finalQty += item.GetProperty("quantity").GetInt32();
        }
        Assert.Equal(maxTotal, finalQty);
        Assert.Equal(3, order.GetProperty("items").GetArrayLength());
    });
}
