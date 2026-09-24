using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Issue #9: shared connect/greet/tool-call-round-trip plumbing for the ordering scenarios in
/// this folder, factored out of the single-tool-call pattern <c>UpdateOrderToolCallTests.cs</c>
/// and <c>HappyHourPricingTests.cs</c> already established, so a scenario that needs to script
/// several sequential tool calls (e.g. combo absorption: add combo, then add side, then add
/// drink) doesn't have to hand-roll the round-trip bookkeeping every time.
/// </summary>
public static class OrderScenarioHelpers
{
    public static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Connects a fresh browser client, asserts no connections leaked from a previous scenario,
    /// waits for the accepted upstream connection, starts the session, and waits for the
    /// greeting's round trip to fully complete (tools_pending cleared, backend ready for a new
    /// turn) — the same sequence every existing scenario repeats before scripting its own turn.
    /// </summary>
    public static async Task<(RealtimeBrowserClient Browser, FakeRealtimeConnection Connection, int RoundTripIndex)> ConnectAndGreetAsync(
        ConformanceFixture fixture, CancellationToken ct)
    {
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        await browser.SendStartSessionAsync(cancellationToken: ct);
        var greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");

        var roundTripIndex = greetingRoundTrip!.Json.GetProperty("roundTripIndex").GetInt32();
        return (browser, connection!, roundTripIndex);
    }

    /// <summary>One scripted `update_order`/`get_order`/`reset_order`/`search` call and everything
    /// the caller needs from its round trip.</summary>
    /// <param name="toClient">True if this tool's <c>ToolResultDirection</c> reaches the browser
    /// (TO_CLIENT/TO_BOTH) — <c>search</c> never does (TO_SERVER only), so callers scripting a
    /// search call must pass false or the wait below would time out waiting for a frame that
    /// never arrives.</param>
    public static async Task<ToolCallResult> CallToolAsync(
        FakeRealtimeConnection connection,
        RealtimeBrowserClient browser,
        string toolName,
        string argumentsJson,
        string callId,
        int previousRoundTripIndex,
        CancellationToken ct,
        bool toClient = true)
    {
        // extension.middle_tier_tool_response carries no call_id (rtmt.py only puts previous_id
        // and tool_name on it), and FrameLog.WaitForAsync always returns the *first* recorded
        // match rather than the newest one -- so a second scripted call to the same tool would
        // otherwise re-match the first call's stale frame. Recording the browser log's length
        // before scripting this call and requiring Sequence >= that watermark scopes the wait to
        // frames this call could actually have produced.
        var browserWatermark = browser.ReceivedFrames.Count;

        connection.Script.Enqueue(new ResponseScript([
            new FunctionCallEvent(Name: toolName, ArgumentsJson: argumentsJson, CallId: callId),
            new DoneEvent(),
        ]));

        // rtmt.py never handles a raw client response.create specially — it passes straight
        // through to the upstream socket unchanged, exactly like a server-VAD-triggered turn
        // would, without needing to simulate real audio timing.
        await browser.SendResponseCreateAsync(ct);

        var functionCallOutput = await connection.ReceivedFrames.WaitForAsync(
            f => f.Type == "conversation.item.create" &&
                 f.Json.TryGetProperty("item", out var item) &&
                 item.TryGetProperty("type", out var itemType) &&
                 itemType.GetString() == "function_call_output" &&
                 item.TryGetProperty("call_id", out var respondedCallId) &&
                 respondedCallId.GetString() == callId,
            FrameTimeout, ct);
        Assert.True(functionCallOutput is not null,
            $"Expected a conversation.item.create(function_call_output) for call_id={callId} upstream within {FrameTimeout}.");

        var outputText = functionCallOutput!.Json.GetProperty("item").GetProperty("output").GetString() ?? "";

        string? toolResultJson = null;
        if (toClient)
        {
            var toolResponse = await browser.ReceivedFrames.WaitForAsync(
                f => f.Sequence >= browserWatermark &&
                     f.Type == "extension.middle_tier_tool_response" &&
                     f.Json.TryGetProperty("tool_name", out var toolNameProp) &&
                     toolNameProp.GetString() == toolName,
                FrameTimeout, ct);
            Assert.True(toolResponse is not null,
                $"Expected extension.middle_tier_tool_response for {toolName} (call_id={callId}) on the browser within {FrameTimeout}.");
            toolResultJson = toolResponse!.Json.GetProperty("tool_result").GetString();
        }

        // After a tool call, rtmt.py auto-issues a bare follow-up response.create upstream to get
        // the model's spoken reply to the tool result; only once *that* turn also completes does
        // it advance/emit the round trip token. Waiting for an index strictly greater than the
        // caller's last-seen one (rather than just "the next round trip frame") means a second
        // scripted tool call in the same test can never race an already-observed-but-stale frame.
        var nextRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token" &&
                 f.Json.GetProperty("roundTripIndex").GetInt32() > previousRoundTripIndex,
            FrameTimeout, ct);
        Assert.True(nextRoundTrip is not null,
            $"Round trip after {toolName} (call_id={callId}) never completed.");

        return new ToolCallResult(
            outputText,
            toolResultJson,
            nextRoundTrip!.Json.GetProperty("roundTripIndex").GetInt32());
    }

    /// <summary>Runs a sequence of `update_order` "add"/"remove" steps (as scripted by
    /// <see cref="ComboStep"/>/ad-hoc callers) against one connection, returning the final
    /// <c>tool_result</c> (order summary JSON) from the last step.</summary>
    public static async Task<ToolCallResult> RunOrderStepsAsync(
        FakeRealtimeConnection connection,
        RealtimeBrowserClient browser,
        IEnumerable<(string Action, string Item, string Size, int Quantity, double Price)> steps,
        int roundTripIndex,
        CancellationToken ct,
        string callIdPrefix = "call_step")
    {
        ToolCallResult? last = null;
        var i = 0;
        foreach (var step in steps)
        {
            var argsJson = JsonSerializer.Serialize(new
            {
                action = step.Action,
                item_name = step.Item,
                size = step.Size,
                quantity = step.Quantity,
                price = step.Price,
            });
            last = await CallToolAsync(
                connection, browser, "update_order", argsJson, $"{callIdPrefix}_{i}", roundTripIndex, ct);
            roundTripIndex = last.RoundTripIndex;
            i++;
        }

        Assert.True(last is not null, "RunOrderStepsAsync requires at least one step.");
        return last!;
    }

    public static double GetOrderTotal(string orderSummaryJson) =>
        JsonDocument.Parse(orderSummaryJson).RootElement.GetProperty("total").GetDouble();

    public static double GetOrderFinalTotal(string orderSummaryJson) =>
        JsonDocument.Parse(orderSummaryJson).RootElement.GetProperty("finalTotal").GetDouble();

    public static int GetOrderItemCount(string orderSummaryJson) =>
        JsonDocument.Parse(orderSummaryJson).RootElement.GetProperty("items").GetArrayLength();
}

/// <summary>Result of one scripted tool-call round trip.</summary>
/// <param name="FunctionCallOutputText">The `function_call_output` item's `output` string sent
/// upstream — empty when the tool's result direction was TO_CLIENT-only (never happens today,
/// but kept honest with rtmt.py's own conditional).</param>
/// <param name="ToolResultJson">The `tool_result` string from `extension.middle_tier_tool_response`,
/// or null if the caller passed <c>toClient: false</c> (e.g. `search`, which is TO_SERVER only).</param>
/// <param name="RoundTripIndex">The `roundTripIndex` of the round trip that completed this call —
/// pass this back in as `previousRoundTripIndex` for the next sequential call.</param>
public sealed record ToolCallResult(string FunctionCallOutputText, string? ToolResultJson, int RoundTripIndex);
