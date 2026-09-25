using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// PR #58 re-review "S2" (issue #36): after a genuinely unhandled tool exception --
/// <c>rtmt.py</c>'s layer-1 <c>except Exception</c> safety net, not <c>tools.py</c>'s own
/// upfront argument validation (layer 2), which returns a graceful <c>ToolResult</c> without
/// ever raising -- rtmt.py now (a) best-effort pushes a ticket refresh so the guest's on-screen
/// order doesn't go stale, and (b) caps consecutive failures (<c>_TOOL_FAILURE_CAP</c> = 2) so it
/// stops silently auto-continuing after that many failures in a row with no success between them.
///
/// Both scenarios here deliberately use <c>update_order</c> with a string <c>price</c>
/// (<c>"cheap"</c>) rather than a missing required argument: <c>tools.py</c>'s own layer-2
/// validation only checks <c>action</c>/<c>item_name</c>/<c>size</c>/<c>quantity</c> presence, so
/// a call with all four present but a non-numeric price sails past it and hits the unguarded
/// `price &lt;= 0.0` comparison at <c>tools.py:~391</c>, raising a genuine <c>TypeError</c> that
/// only rtmt.py's layer-1 <c>except</c> block catches -- exactly the failure mode Rick's S3 "S3"
/// probe describes, and the only way this black-box harness can reach layer 1 through
/// <c>update_order</c>'s own front door (see <see cref="ToolErrorSessionSurvivesTests"/>'s
/// missing-<c>item_name</c> script, which is caught by layer 2 instead and never reaches here).
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class ToolFailureCapAndTicketRefreshTests(ConformanceFixture fixture)
{
    private const string BadPriceArgs =
        """{"action":"add","item_name":"Tots","size":"medium","quantity":1,"price":"cheap"}""";

    [Fact]
    public Task A_genuine_tool_exception_refreshes_the_guests_ticket() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        const string callId = "call_bad_price_ticket";
        var browserWatermark = browser.ReceivedFrames.Count;

        connection.Script.Enqueue(new ResponseScript([
            new FunctionCallEvent(Name: "update_order", ArgumentsJson: BadPriceArgs, CallId: callId),
            new DoneEvent(),
        ]));
        await browser.SendResponseCreateAsync(ct);

        var functionCallOutput = await connection.ReceivedFrames.WaitForAsync(
            f => f.Type == "conversation.item.create" &&
                 f.Json.TryGetProperty("item", out var item) &&
                 item.TryGetProperty("type", out var itemType) &&
                 itemType.GetString() == "function_call_output" &&
                 item.TryGetProperty("call_id", out var respondedCallId) &&
                 respondedCallId.GetString() == callId,
            OrderScenarioHelpers.FrameTimeout, ct);
        Assert.True(functionCallOutput is not null,
            "Expected a graceful function_call_output for the bad-price call -- if this is null, " +
            "the tool exception killed the connection instead of producing a model-visible error.");
        var outputText = functionCallOutput!.Json.GetProperty("item").GetProperty("output").GetString() ?? "";
        Assert.False(OrderScenarioHelpers.LooksLikeOrderSummary(outputText),
            "function_call_output for the bad-price call parses as a JSON order-summary object, " +
            "but this call was scripted to raise inside the tool handler -- a genuine graceful " +
            "error is a plain apology string, never an order summary.");

        // Session survives: prove the connection is still usable for a further tool call, same
        // pattern as ToolErrorSessionSurvivesTests.
        var nextRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token" &&
                 f.Json.GetProperty("roundTripIndex").GetInt32() > roundTripIndex,
            OrderScenarioHelpers.FrameTimeout, ct);
        Assert.True(nextRoundTrip is not null, "Round trip after the bad-price call never completed.");
        var nextIndex = nextRoundTrip!.Json.GetProperty("roundTripIndex").GetInt32();

        // No stray extension.middle_tier_tool_response tagged "update_order" -- the failed call's
        // own result never reaches the browser (checked after the round trip wait for the same
        // ordering reason as ToolErrorSessionSurvivesTests / OrderScenarioHelpers.CallToolAsync).
        var strayToolResponse = browser.ReceivedFrames.Snapshot().Any(f =>
            f.Sequence >= browserWatermark &&
            f.Type == "extension.middle_tier_tool_response" &&
            f.Json.TryGetProperty("tool_name", out var strayToolNameProp) &&
            strayToolNameProp.GetString() == "update_order");
        Assert.False(strayToolResponse,
            $"Expected no extension.middle_tier_tool_response for update_order (call_id={callId}) " +
            "to have reached the browser for this deliberately failing call.");

        // #36 S2: the ticket-refresh IS expected -- tagged tool_name="get_order" (a read, never
        // implying the failed mutation actually happened) rather than "update_order" (ruled out
        // immediately above).
        var ticketRefresh = browser.ReceivedFrames.Snapshot().FirstOrDefault(f =>
            f.Sequence >= browserWatermark &&
            f.Type == "extension.middle_tier_tool_response" &&
            f.Json.TryGetProperty("tool_name", out var refreshToolNameProp) &&
            refreshToolNameProp.GetString() == "get_order");
        Assert.True(ticketRefresh is not null,
            "Expected a get_order-tagged extension.middle_tier_tool_response (ticket refresh) to " +
            "reach the browser after the tool exception, so the on-screen order doesn't go stale " +
            "(swigerb/SonicAIDriveThru#36 S2).");
        Assert.True(OrderScenarioHelpers.LooksLikeOrderSummary(
            ticketRefresh!.Json.GetProperty("tool_result").GetString() ?? ""),
            "The ticket-refresh tool_result should be a genuine order-summary JSON object.");
        Assert.Null(browser.CloseStatus);

        var next = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", "Tots", "medium", 1, 2.79m)],
            nextIndex, ct);
        var order = JsonDocument.Parse(next.ToolResultJson!).RootElement;
        Assert.Equal(1, order.GetProperty("items").GetArrayLength());
    }, allowedNewBackendErrors: 1);

    [Fact]
    public Task Consecutive_tool_exceptions_suppress_the_auto_continue_at_the_cap() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        // Two consecutive genuine tool exceptions (both a string "price", same shape as
        // A_genuine_tool_exception_refreshes_the_guests_ticket above), with no success in
        // between -- exactly rtmt.py's _TOOL_FAILURE_CAP (2) in a row. The first failure's
        // auto-continue must still fire as always; the SECOND failure reaches the cap and must
        // suppress it.
        connection.Script.Enqueue(new ResponseScript([
            new FunctionCallEvent(Name: "update_order", ArgumentsJson: BadPriceArgs, CallId: "call_cap_a"),
            new DoneEvent(),
        ]));
        connection.Script.Enqueue(new ResponseScript([
            new FunctionCallEvent(Name: "update_order", ArgumentsJson: BadPriceArgs, CallId: "call_cap_b"),
            new DoneEvent(),
        ]));
        // Nothing else is queued -- if the cap is broken, a THIRD auto-continue falls through to
        // ResponseScript.Default (a plain audio reply, no tool call), which -- unlike the two
        // tool-call responses above -- completes with no pending tool call, so it WOULD emit an
        // extension.round_trip_token automatically, with no browser action at all.

        await browser.SendResponseCreateAsync(ct); // manual trigger for round 1 (call_cap_a)

        var callA = await connection.ReceivedFrames.WaitForAsync(
            f => f.Type == "conversation.item.create" &&
                 f.Json.TryGetProperty("item", out var itemA) &&
                 itemA.TryGetProperty("call_id", out var idA) && idA.GetString() == "call_cap_a",
            OrderScenarioHelpers.FrameTimeout, ct);
        Assert.True(callA is not null, "call_cap_a's function_call_output never reached upstream.");

        // Round 2 (call_cap_b) must be triggered automatically by rtmt's own auto-continue after
        // round 1's failure -- the browser never sends a second response.create for it. This is
        // the "not yet at the cap" case: a single failure must still auto-continue as before.
        var callB = await connection.ReceivedFrames.WaitForAsync(
            f => f.Type == "conversation.item.create" &&
                 f.Json.TryGetProperty("item", out var itemB) &&
                 itemB.TryGetProperty("call_id", out var idB) && idB.GetString() == "call_cap_b",
            OrderScenarioHelpers.FrameTimeout, ct);
        Assert.True(callB is not null,
            "call_cap_b's function_call_output never reached upstream -- the auto-continue after " +
            "the FIRST failure should still fire; only the cap-th consecutive failure suppresses it.");

        // At the cap: rtmt must NOT auto-continue a third time. Rather than racing an uncertain
        // amount of async processing time after callB, send a KNOWN, always-forwarded, unrelated
        // frame (input_audio_buffer.clear, same idiom as ResponseCreateHooksGateTests) and wait
        // for IT to arrive -- since every frame on this connection is strictly ordered, by the
        // time `clear` has been recorded, anything an errant auto-continue was ever going to send
        // in response to callB's response.done has necessarily already arrived too. This makes
        // the snapshot check below a hard ordering guarantee, not a timing-dependent race.
        await browser.SendInputAudioClearAsync(ct);
        var clear = await connection.ReceivedFrames.WaitForAsync(
            f => f.Type == "input_audio_buffer.clear" && f.Sequence > callB!.Sequence,
            OrderScenarioHelpers.FrameTimeout, ct);
        Assert.True(clear is not null, "Expected input_audio_buffer.clear to still reach the fake upstream.");

        var prematureRoundTrip = browser.ReceivedFrames.Snapshot().Any(f =>
            f.Type == "extension.round_trip_token" &&
            f.Json.GetProperty("roundTripIndex").GetInt32() > roundTripIndex);
        Assert.False(prematureRoundTrip,
            "An extension.round_trip_token arrived with no browser action after the second " +
            "consecutive tool failure -- the auto-continue should have been suppressed at the cap " +
            "(swigerb/SonicAIDriveThru#36 S2).");
        var prematureResponseCreate = connection.ReceivedFrames.Snapshot().Any(f =>
            f.Sequence > callB!.Sequence && f.Sequence < clear!.Sequence && f.Type == "response.create");
        Assert.False(prematureResponseCreate,
            "An unexpected response.create reached upstream between the second tool failure and " +
            "the probe frame -- the auto-continue should have been suppressed at the cap " +
            "(swigerb/SonicAIDriveThru#36 S2).");
        Assert.Null(browser.CloseStatus);

        // The connection is still usable: the browser's own next action (not an auto-continue)
        // still produces a normal, successful tool call and round trip.
        var next = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", "Tots", "medium", 1, 2.79m)],
            roundTripIndex, ct);
        var order = JsonDocument.Parse(next.ToolResultJson!).RootElement;
        Assert.Equal(1, order.GetProperty("items").GetArrayLength());
    }, allowedNewBackendErrors: 2);
}
