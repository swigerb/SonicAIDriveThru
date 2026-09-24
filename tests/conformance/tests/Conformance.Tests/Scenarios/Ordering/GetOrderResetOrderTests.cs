using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>Issue #9: `get_order` and `reset_order` — both always TO_BOTH (see app/backend/tools.py),
/// so every call here can assert the browser's `tool_result` JSON order summary directly.</summary>
[Collection(ConformanceCollection.Name)]
public sealed class GetOrderResetOrderTests(ConformanceFixture fixture)
{
    [Fact]
    public Task Get_order_on_a_fresh_session_returns_an_empty_order() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "get_order", "{}", "call_get_order_empty", roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        Assert.Equal(0, order.GetProperty("items").GetArrayLength());
        OrderScenarioHelpers.AssertMoneyEqual(0m, order.GetProperty("total").GetDecimal());
        OrderScenarioHelpers.AssertMoneyEqual(0m, order.GetProperty("tax").GetDecimal());
        OrderScenarioHelpers.AssertMoneyEqual(0m, order.GetProperty("finalTotal").GetDecimal());
    });

    [Fact]
    public Task Get_order_after_adding_items_reflects_the_current_order_without_mutating_it() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var added = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", "Tots", "medium", 2, 2.79m)],
            roundTripIndex, ct);

        var firstGet = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "get_order", "{}", "call_get_order_1", added.RoundTripIndex, ct);
        var secondGet = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "get_order", "{}", "call_get_order_2", firstGet.RoundTripIndex, ct);

        // get_order is read-only: calling it twice in a row must return the identical summary.
        // Compared structurally (JsonElement.DeepEquals) rather than as raw strings (PR #38 review
        // nit) so this stays correct even if a future backend reorders JSON object keys or changes
        // insignificant whitespace between the two calls.
        using var firstGetDoc = JsonDocument.Parse(firstGet.ToolResultJson!);
        using var secondGetDoc = JsonDocument.Parse(secondGet.ToolResultJson!);
        Assert.True(JsonElement.DeepEquals(firstGetDoc.RootElement, secondGetDoc.RootElement),
            $"Expected two consecutive get_order calls to return identical order summaries, but they differed:\n" +
            $"first:  {firstGet.ToolResultJson}\nsecond: {secondGet.ToolResultJson}");

        var order = secondGetDoc.RootElement;
        Assert.Equal(1, order.GetProperty("items").GetArrayLength());
        Assert.Equal(2, order.GetProperty("items")[0].GetProperty("quantity").GetInt32());
        OrderScenarioHelpers.AssertMoneyEqual(2 * 2.79m, order.GetProperty("total").GetDecimal());
    });

    [Fact]
    public Task Reset_order_clears_all_items_and_zeroes_the_totals() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var added = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [
                ("add", "Tots", "medium", 1, 2.79m),
                ("add", "Cherry Limeade", "medium", 1, 2.99m),
            ],
            roundTripIndex, ct);
        var beforeReset = JsonDocument.Parse(added.ToolResultJson!).RootElement;
        Assert.Equal(2, beforeReset.GetProperty("items").GetArrayLength());

        var reset = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "reset_order", "{}", "call_reset_order", added.RoundTripIndex, ct);

        var order = JsonDocument.Parse(reset.ToolResultJson!).RootElement;
        Assert.Equal(0, order.GetProperty("items").GetArrayLength());
        OrderScenarioHelpers.AssertMoneyEqual(0m, order.GetProperty("total").GetDecimal());
        OrderScenarioHelpers.AssertMoneyEqual(0m, order.GetProperty("finalTotal").GetDecimal());
    });

    [Fact]
    public Task Reset_order_on_an_already_empty_order_is_a_safe_no_op() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var reset = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "reset_order", "{}", "call_reset_order_on_empty", roundTripIndex, ct);

        var order = JsonDocument.Parse(reset.ToolResultJson!).RootElement;
        Assert.Equal(0, order.GetProperty("items").GetArrayLength());

        // Prove the session is still fully usable afterwards.
        var afterReset = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", "Tots", "medium", 1, 2.79m)],
            reset.RoundTripIndex, ct);
        var orderAfter = JsonDocument.Parse(afterReset.ToolResultJson!).RootElement;
        Assert.Equal(1, orderAfter.GetProperty("items").GetArrayLength());
    });
}
