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

        if (!toClient)
        {
            // PR #38 re-review must-fix 1 (Rick): this positive "nothing arrived" check must run
            // AFTER the nextRoundTrip wait above, not immediately after functionCallOutput. rtmt.py
            // sends the upstream function_call_output *before* it sends (or in the rejected case,
            // withholds) the browser-bound extension.middle_tier_tool_response -- checking a
            // snapshot right after the upstream frame races the backend's own still-in-flight
            // browser send and can pass vacuously (frame simply hadn't arrived yet), exactly the
            // failure mode that let Rick's M4 mutation survive intermittently. Both frames travel
            // over the single, ordered browser WebSocket, so by the time the round trip token for
            // *this* call has been received, any middle_tier_tool_response the backend was ever
            // going to send for it has necessarily already arrived -- there is no later point at
            // which it could still show up. See ToolErrorSessionSurvivesTests's N1 mutation-testing
            // note for why this ordering guarantee is what makes the check deterministic instead of
            // racy (previously observed to catch Rick's mutation on only ~2 of 3 runs).
            var strayToolResponse = browser.ReceivedFrames.Snapshot().Any(f =>
                f.Sequence >= browserWatermark &&
                f.Type == "extension.middle_tier_tool_response" &&
                f.Json.TryGetProperty("tool_name", out var strayToolNameProp) &&
                strayToolNameProp.GetString() == toolName);
            Assert.False(strayToolResponse,
                $"Expected no extension.middle_tier_tool_response for {toolName} (call_id={callId}) " +
                "to have reached the browser since the watermark, but one arrived -- this is " +
                "exactly the shape of PR #38 review M4 (a rejected/dropped call silently answered " +
                "as if it had succeeded).");

            Assert.False(LooksLikeOrderSummary(outputText),
                $"function_call_output for {toolName} (call_id={callId}) parses as a JSON " +
                "order-summary object (has \"items\" and \"finalTotal\" keys), but this call was " +
                "scripted as rejected/dropped -- a genuine rejection is a plain apology string, " +
                "never an order summary.");
        }

        return new ToolCallResult(
            outputText,
            toolResultJson,
            nextRoundTrip!.Json.GetProperty("roundTripIndex").GetInt32());
    }

    /// <summary>Runs a sequence of `update_order` "add"/"remove" steps (as scripted by
    /// <see cref="ComboStep"/>/ad-hoc callers) against one connection, returning the final
    /// <c>tool_result</c> (order summary JSON) from the last step. A step whose <c>Action</c> is
    /// <c>"reset"</c> calls `reset_order` instead of `update_order` (item/size/quantity/price are
    /// unused for that step) — added for the M1 kill-scenario (PR #38 review item 4) that needs
    /// to reset mid-sequence and keep scripting further steps on the same connection.</summary>
    public static async Task<ToolCallResult> RunOrderStepsAsync(
        FakeRealtimeConnection connection,
        RealtimeBrowserClient browser,
        IEnumerable<(string Action, string Item, string Size, int Quantity, decimal Price)> steps,
        int roundTripIndex,
        CancellationToken ct,
        string callIdPrefix = "call_step")
    {
        ToolCallResult? last = null;
        var i = 0;
        foreach (var step in steps)
        {
            if (step.Action == "reset")
            {
                last = await CallToolAsync(
                    connection, browser, "reset_order", "{}", $"{callIdPrefix}_{i}", roundTripIndex, ct);
            }
            else
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
            }
            roundTripIndex = last.RoundTripIndex;
            i++;
        }

        Assert.True(last is not null, "RunOrderStepsAsync requires at least one step.");
        return last!;
    }

    /// <summary>
    /// Money-contract tolerance (PR #38 review item 1): the live Python backend computes in
    /// IEEE-754 double and echoes back tiny float noise on the wire (e.g. 0.8151999999999999
    /// instead of the exact decimal 0.8152); this absolute tolerance absorbs exactly that noise
    /// while still failing a genuinely wrong implementation (e.g. one that rounds tax to cents
    /// per line before summing, which differs by far more than a double-rounding error's width).
    /// </summary>
    public const decimal MoneyTolerance = 0.000001m;

    /// <summary>
    /// Asserts two money values are equal within <see cref="MoneyTolerance"/>. Never use xUnit's
    /// `Assert.Equal(double, double, precision: N)` for money in this stream — that rounds via
    /// `Math.Round(double, N)` semantics, which both wrongly fails some correct decimal-exact
    /// values (e.g. 10.185m rounds to 10.18, not 10.19) and wrongly passes some incorrect ones
    /// (e.g. a per-line-rounded tax that happens to land within half a cent of the correct total).
    /// </summary>
    public static void AssertMoneyEqual(decimal expected, decimal actual, string? because = null)
    {
        var diff = Math.Abs(expected - actual);
        Assert.True(diff <= MoneyTolerance,
            because ?? $"Expected {expected} but got {actual} (difference {diff} exceeds tolerance {MoneyTolerance}).");
    }

    public static decimal GetOrderTotal(string orderSummaryJson) =>
        JsonDocument.Parse(orderSummaryJson).RootElement.GetProperty("total").GetDecimal();

    public static decimal GetOrderTax(string orderSummaryJson) =>
        JsonDocument.Parse(orderSummaryJson).RootElement.GetProperty("tax").GetDecimal();

    public static decimal GetOrderFinalTotal(string orderSummaryJson) =>
        JsonDocument.Parse(orderSummaryJson).RootElement.GetProperty("finalTotal").GetDecimal();

    public static int GetOrderItemCount(string orderSummaryJson) =>
        JsonDocument.Parse(orderSummaryJson).RootElement.GetProperty("items").GetArrayLength();

    /// <summary>
    /// True if <paramref name="text"/> parses as a JSON object carrying both an "items" and a
    /// "finalTotal" key — i.e., looks like the order-summary payload `update_order`/`get_order`/
    /// `reset_order` embed as `client_text` (see app/backend/tools.py), rather than a plain
    /// natural-language string. Used to positively prove a rejected/dropped tool call's
    /// function_call_output is a genuine apology, not an order summary in disguise (PR #38 review
    /// item 3, Rick's M4). Internal (not private) so ToolErrorSessionSurvivesTests's hand-rolled
    /// unhandled-exception scenario -- which bypasses CallToolAsync entirely and so needs the same
    /// check inline -- can reuse it (re-review follow-up 8).
    /// </summary>
    internal static bool LooksLikeOrderSummary(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("items", out _) &&
                   doc.RootElement.TryGetProperty("finalTotal", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }
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
