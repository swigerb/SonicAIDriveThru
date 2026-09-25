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

        // At the cap: rtmt must send exactly ONE server-authored response.create with
        // response.tool_choice="none" instead of silence (PR #58 re-review "S1") -- the model
        // can still apologise out loud and ask the guest, but can't call a tool again with no
        // guest input. It must ALSO carry non-empty response.instructions (PR #58 re-review
        // round 3): a live probe against real gpt-realtime-2.1 showed tool_choice="none" alone
        // still let the model falsely claim an item was added/changed in 2 of 3 runs; explicit
        // instructions saying nothing changed fixed it in 3 of 3 runs. The exact wording is a
        // brand-config concern (prompts/sonic/error_messages.yaml), not pinned here.
        var capNotice = await connection.ReceivedFrames.WaitForAsync(
            f => f.Type == "response.create" && f.Sequence > callB!.Sequence &&
                 f.Json.TryGetProperty("response", out var respObj) &&
                 respObj.TryGetProperty("tool_choice", out var toolChoiceProp) &&
                 toolChoiceProp.GetString() == "none" &&
                 respObj.TryGetProperty("instructions", out var instructionsProp) &&
                 instructionsProp.ValueKind == JsonValueKind.String &&
                 !string.IsNullOrWhiteSpace(instructionsProp.GetString()),
            OrderScenarioHelpers.FrameTimeout, ct);
        Assert.True(capNotice is not null,
            "Expected a server-authored response.create with response.tool_choice=\"none\" AND " +
            "non-empty response.instructions at the cap -- the model can still apologise out " +
            "loud, but must not be allowed to call a tool again with no guest input, or to " +
            "falsely claim the order changed anyway (swigerb/SonicAIDriveThru#36 S1, PR #58 " +
            "re-review round 3).");

        // Nothing else was queued behind call_cap_a/call_cap_b, so the cap notice falls through
        // to ResponseScript.Default (a plain audio reply, no tool call) -- proving the apology
        // itself doesn't re-open the tool-calling loop: no THIRD function_call_output ever
        // reaches upstream.
        var thirdToolCall = connection.ReceivedFrames.Snapshot().Any(f =>
            f.Sequence > capNotice!.Sequence &&
            f.Type == "conversation.item.create" &&
            f.Json.TryGetProperty("item", out var item3) &&
            item3.TryGetProperty("type", out var item3Type) &&
            item3Type.GetString() == "function_call_output");
        Assert.False(thirdToolCall,
            "A third function_call_output reached upstream after the cap notice -- the one-time " +
            "apology must not itself re-open the auto-continue loop (swigerb/SonicAIDriveThru#36 S1).");
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

    /// <summary>
    /// PR #58 re-review "S1" repro (Rick's verbatim test, adapted): the earlier per-call,
    /// reset-on-any-success cap let a model loop <c>update_order</c> (fails) -&gt;
    /// <c>get_order</c> (succeeds -- the very call our own error text tells it to make) -&gt;
    /// <c>update_order</c> (fails) -&gt; ... forever with no guest input, because each
    /// <c>get_order</c> success reset the streak back to zero. This scripts three such pairs
    /// behind a SINGLE browser response.create and asserts the third <c>update_order</c>
    /// (call_id <c>"f3"</c>) never reaches upstream: by the second failed round the cap is
    /// hit and rtmt sends its one tool_choice="none" apology; since the fake upstream (like a
    /// real model that ignores tool_choice) will still dequeue and play whatever's next if
    /// asked, the ONLY thing that can stop "f3" from ever running is that rtmt must NOT send a
    /// second auto-continue after the apology -- proving both the round-based counting (not
    /// reset by get_order) and the one-shot-notice behaviour together, black-box.
    /// </summary>
    [Fact]
    public Task Cap_is_not_reset_by_the_prescribed_get_order() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, _) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _b = browser;
        foreach (var (name, args, id) in new[] {
            ("update_order", BadPriceArgs, "f1"), ("get_order", "{}", "g1"),
            ("update_order", BadPriceArgs, "f2"), ("get_order", "{}", "g2"),
            ("update_order", BadPriceArgs, "f3"), ("get_order", "{}", "g3") })
            connection.Script.Enqueue(new ResponseScript([new FunctionCallEvent(name, args, id), new DoneEvent()]));

        await browser.SendResponseCreateAsync(ct);   // the only guest/browser action

        var f3 = await connection.ReceivedFrames.WaitForAsync(
            f => f.Type == "conversation.item.create" &&
                 f.Json.TryGetProperty("item", out var it) &&
                 it.TryGetProperty("call_id", out var cid) && cid.GetString() == "f3",
            TimeSpan.FromSeconds(8), ct);
        Assert.True(f3 is null, "third failing update_order reached with no guest input -- cap never engaged");
        Assert.Null(browser.CloseStatus);
    }, allowedNewBackendErrors: 3);

    /// <summary>
    /// PR #58 re-review "S1" follow-up scenario: genuine guest activity (not another tool
    /// success) is the only thing that resets the failed-round streak. After the cap trips
    /// (two failed rounds, one apology sent), a guest speaking (input_audio_buffer.append,
    /// which the fake's VAD-default script answers with a synthetic
    /// speech_started -&gt; ... -&gt; transcription.completed sequence) must clear the streak so a
    /// further tool call from the guest's own next turn can fail and NOT immediately hit the
    /// cap again.
    /// </summary>
    [Fact]
    public Task Guest_speech_resets_the_failure_streak_after_the_cap() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        connection.Script.Enqueue(new ResponseScript([
            new FunctionCallEvent(Name: "update_order", ArgumentsJson: BadPriceArgs, CallId: "call_reset_a"),
            new DoneEvent(),
        ]));
        connection.Script.Enqueue(new ResponseScript([
            new FunctionCallEvent(Name: "update_order", ArgumentsJson: BadPriceArgs, CallId: "call_reset_b"),
            new DoneEvent(),
        ]));
        await browser.SendResponseCreateAsync(ct);

        var callB = await connection.ReceivedFrames.WaitForAsync(
            f => f.Type == "conversation.item.create" &&
                 f.Json.TryGetProperty("item", out var itemB) &&
                 itemB.TryGetProperty("call_id", out var idB) && idB.GetString() == "call_reset_b",
            OrderScenarioHelpers.FrameTimeout, ct);
        Assert.True(callB is not null, "call_reset_b's function_call_output never reached upstream.");
        var capNotice = await connection.ReceivedFrames.WaitForAsync(
            f => f.Type == "response.create" && f.Sequence > callB!.Sequence &&
                 f.Json.TryGetProperty("response", out var respObj) &&
                 respObj.TryGetProperty("tool_choice", out var toolChoiceProp) &&
                 toolChoiceProp.GetString() == "none",
            OrderScenarioHelpers.FrameTimeout, ct);
        Assert.True(capNotice is not null, "Expected the one-time cap apology after the second failure.");

        // Guest speaks (mic audio), same idiom as the VAD-default scenarios elsewhere -- this is
        // genuine guest activity, so it must reset the failed-round streak. The cap notice's own
        // Default reply just produced real audio, so echo suppression (config.yaml's
        // audio.echo_cooldown_seconds=1.5, NOT doubled here since this isn't the greeting) is
        // genuinely active for a moment afterward and would otherwise drop this append entirely
        // before it ever reaches upstream (see EchoSuppressionBargeInTests' M4 for the same drop
        // proven directly) -- there is no CONFORMANCE_* hook for this cooldown. A single fixed
        // Task.Delay before the append is not fully reliable: the cooldown clock starts when the
        // backend finishes the cap notice's own audio, which can lag well past 1.5s under system
        // load (observed under a fresh build's CPU contention), so a delay sized for the nominal
        // cooldown occasionally isn't enough. Poll instead: retry the append (each one a fresh,
        // otherwise-identical mic chunk) until the fake's VAD-default transcription-completed
        // reply shows up, bounded overall.
        RecordedFrame? transcriptionCompleted = null;
        for (var attempt = 0; attempt < 8 && transcriptionCompleted is null; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(0.75), ct);
            var browserWatermarkForAppend = browser.ReceivedFrames.Count;
            await browser.SendInputAudioAppendAsync("dGVzdC1hdWRpby1jaHVuaw==", ct);
            transcriptionCompleted = await browser.ReceivedFrames.WaitForAsync(
                f => f.Sequence >= browserWatermarkForAppend &&
                     f.Type == "conversation.item.input_audio_transcription.completed",
                TimeSpan.FromSeconds(1), ct);
        }
        Assert.True(transcriptionCompleted is not null,
            "Expected the VAD-default transcription-completed reply after retrying past echo cooldown.");

        // A single further failing tool call must auto-continue as normal (not suppressed) --
        // proving the streak was reset to zero, not left at the cap. Watermark on the upstream
        // connection's OWN log (transcriptionCompleted's Sequence is a browser-log sequence
        // number, not comparable to connection.ReceivedFrames' independent counter).
        var connectionWatermarkForCallC = connection.ReceivedFrames.Count;
        connection.Script.Enqueue(new ResponseScript([
            new FunctionCallEvent(Name: "update_order", ArgumentsJson: BadPriceArgs, CallId: "call_reset_c"),
            new DoneEvent(),
        ]));
        await browser.SendResponseCreateAsync(ct);
        var callC = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence >= connectionWatermarkForCallC &&
                 f.Type == "conversation.item.create" &&
                 f.Json.TryGetProperty("item", out var itemC) &&
                 itemC.TryGetProperty("call_id", out var idC) && idC.GetString() == "call_reset_c",
            OrderScenarioHelpers.FrameTimeout, ct);
        Assert.True(callC is not null,
            "call_reset_c's function_call_output never reached upstream -- a single failure right " +
            "after a guest turn must not be treated as already at the cap.");
        // Positively wait for the bare (no tool_choice override) auto-continue -- a Snapshot()
        // taken right after callC is unsafe under concurrent test load: the backend can take a
        // little longer than usual to get to response.done and send the continuation, and a
        // one-shot check can race ahead of it (observed directly: callC arrived, but the bare
        // continuation was still ~2.5s away under a busy shared backend). WaitForAsync polls
        // with FrameTimeout instead of assuming it's already there.
        var bareAutoContinue = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence > callC!.Sequence &&
                 f.Type == "response.create" &&
                 (!f.Json.TryGetProperty("response", out var respObj3) ||
                  !respObj3.TryGetProperty("tool_choice", out var toolChoiceUnused3)),
            OrderScenarioHelpers.FrameTimeout, ct);
        Assert.True(bareAutoContinue is not null,
            "Expected a bare (no tool_choice override) auto-continue after the single post-reset " +
            "failure -- the streak must be 1, not already back at the cap.");
        // Now that the bare continuation has been positively observed, it's safe to check that no
        // tool_choice=none cap notice snuck in before it (a Snapshot() bounded by two confirmed
        // frames, not a race against an unconfirmed one).
        var strayCapNotice = connection.ReceivedFrames.Snapshot().Any(f =>
            f.Sequence > callC!.Sequence && f.Sequence < bareAutoContinue!.Sequence &&
            f.Type == "response.create" &&
            f.Json.TryGetProperty("response", out var respObj2) &&
            respObj2.TryGetProperty("tool_choice", out var toolChoiceProp2) &&
            toolChoiceProp2.GetString() == "none");
        Assert.False(strayCapNotice,
            "A tool_choice=none cap notice fired after only ONE failed round post-reset.");
        Assert.Null(browser.CloseStatus);
    }, allowedNewBackendErrors: 3);
}
