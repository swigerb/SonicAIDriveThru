using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// PR #22 review item 12: proves the FixedClock backend profile actually freezes
/// app/backend/order_state.py's notion of "now" for the Python process it launches. Scripts an
/// `update_order` "add" call for a drink-keyword item while the clock is frozen inside the
/// 14:00-16:00 happy-hour window, and asserts the resulting order summary's finalTotal reflects
/// the 50% happy-hour discount plus tax -- deterministic regardless of what day/hour the suite
/// actually runs on.
/// </summary>
[Collection(FixedClockConformanceCollection.Name)]
public sealed class HappyHourPricingTests(FixedClockConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Happy_hour_discount_applies_at_the_frozen_clock_instant() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        await browser.SendStartSessionAsync(cancellationToken: ct);
        var greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");

        const string callId = "call_update_order_happy_hour";
        const double price = 4.00;
        connection!.Script.Enqueue(new ResponseScript([
            new FunctionCallEvent(
                Name: "update_order",
                // "Cherry Limeade" doesn't need to exist in menuItems.json: menu_utils'
                // infer_category keyword-fallback matches "limeade" to the "drinks" category
                // that order_state.py's happy-hour discount applies to.
                ArgumentsJson: $$"""{"action":"add","item_name":"Cherry Limeade","size":"Regular","quantity":1,"price":{{price}}}""",
                CallId: callId),
            new DoneEvent(),
        ]));
        await browser.SendResponseCreateAsync(ct);

        var functionCallOutput = await connection!.ReceivedFrames.WaitForAsync(
            f => f.Type == "conversation.item.create" &&
                 f.Json.TryGetProperty("item", out var item) &&
                 item.TryGetProperty("type", out var itemType) &&
                 itemType.GetString() == "function_call_output" &&
                 item.TryGetProperty("call_id", out var respondedCallId) &&
                 respondedCallId.GetString() == callId,
            FrameTimeout, ct);
        Assert.True(functionCallOutput is not null,
            $"Expected a conversation.item.create(function_call_output) for call_id={callId} upstream within {FrameTimeout}.");

        var toolResponse = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.middle_tier_tool_response" &&
                 f.Json.TryGetProperty("tool_name", out var toolName) &&
                 toolName.GetString() == "update_order",
            FrameTimeout, ct);
        Assert.True(toolResponse is not null,
            $"Expected extension.middle_tier_tool_response for update_order on the browser within {FrameTimeout}.");

        // tool_result is the order summary's JSON-serialised OrderSummary (see
        // app/backend/tools.py's update_order -> client_text=json_order_summary).
        var toolResultJson = toolResponse!.Json.GetProperty("tool_result").GetString();
        Assert.NotNull(toolResultJson);
        using var orderSummary = JsonDocument.Parse(toolResultJson!);
        var finalTotal = orderSummary.RootElement.GetProperty("finalTotal").GetDouble();

        // price(4.00) * qty(1) * happy_hour_discount(0.5) = 2.00; + tax_rate(0.08) = 2.16.
        // See app/backend/config.yaml's business_rules and order_state.py's _update_summary.
        const double expectedFinalTotal = price * 0.5 * 1.08;
        Assert.Equal(expectedFinalTotal, finalTotal, precision: 2);

        // The "backend logged no unhandled error during this scenario" invariant (PR #22 review
        // item N5) is now a fixture-wide, language-neutral check applied by
        // ConformanceFixture.RunAsync after every scenario -- no per-test assertion needed here.
    });
}
