using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Issue #9: "a tool that errors returns an error result to the model and the session survives
/// (next tool call works)".
///
/// Two distinct kinds of "tool errors" are tested here, because app/backend/tools.py and
/// app/backend/rtmt.py handle them very differently:
///
///  1. An application-level rejection tools.py itself catches and turns into a graceful
///     TO_SERVER/TO_BOTH ToolResult (e.g., update_order's zero/negative-price guard) — the model
///     gets an apology string back as the function_call_output, and the very next tool call on
///     the same connection succeeds normally.
///
///  2. A genuine unhandled Python exception inside a tool handler (e.g., a scripted call missing
///     a required argument the handler accesses via `args["..."]` with no `.get()` fallback,
///     raising KeyError). Originally (#36), app/backend/rtmt.py had no try/except around
///     `await tool.target(...)` in its response.output_item.done handler — only a connection-wide
///     catch-all much further up the stack (see _forward_messages's
///     `except Exception: logger.exception(...)` around the asyncio.gather of both relay
///     directions) that logged and tore the whole socket down instead of returning a model-visible
///     error. Empirically confirmed against the live backend before the fix (not just by reading
///     rtmt.py): running this scenario unskipped produced exactly the predicted traceback --
///
///       File "app/backend/rtmt.py", line 1357, in _forward_messages
///         await asyncio.gather(from_client_to_server(), from_server_to_client())
///       File "app/backend/rtmt.py", line 1344, in from_server_to_client
///         new_msg = await self._process_message_to_client(...)
///       File "app/backend/rtmt.py", line 817, in _process_message_to_client
///         result = await tool.target(args, session_id)
///       File "app/backend/tools.py", line 325, in update_order
///         item_name = args["item_name"]
///       KeyError: 'item_name'
///
///     -- followed by "Session ... detached (client close code=None)" and no
///     function_call_output ever reaching the upstream socket.
///
///     Fixed by (a) wrapping the tool-execution + result-marshaling block in
///     response.output_item.done in a try/except that logs server-side (tool name + session id
///     only, never the raw args) and returns a neutral function_call_output ("tool_execution_failed"
///     in error_messages.yaml) instead of letting the exception propagate, and (b) tools.py's
///     update_order now validates its required arguments up front and returns the same graceful
///     ToolResult instead of raising a bare KeyError in the first place.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class ToolErrorSessionSurvivesTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan ShortFrameTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public Task Session_survives_a_graceful_application_level_tool_error() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        // tools.py::update_order explicitly rejects action=="add" && price<=0.0 with an apology
        // ToolResult(..., ToolResultDirection.TO_SERVER) rather than raising -- this is the "error
        // result to the model" the issue describes.
        var errored = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            """{"action":"add","item_name":"Tots","size":"medium","quantity":1,"price":-1.0}""",
            "call_graceful_error", roundTripIndex, ct, toClient: false);
        Assert.False(string.IsNullOrWhiteSpace(errored.FunctionCallOutputText));
        Assert.Null(errored.ToolResultJson); // TO_SERVER-only: never reaches the browser

        // The session survives: the very next tool call, on the same connection, still works.
        var next = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", "Tots", "medium", 1, 2.79m)],
            errored.RoundTripIndex, ct);
        var order = JsonDocument.Parse(next.ToolResultJson!).RootElement;
        Assert.Equal(1, order.GetProperty("items").GetArrayLength());
    });

    [Fact]
    public Task Session_survives_an_unhandled_tool_exception() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        // Deliberately omits the required "item_name" argument. tools.py::update_order does
        // `item_name = args["item_name"]` with no .get()/default, so this raises an unhandled
        // KeyError inside the tool handler.
        const string callId = "call_unhandled_exception";

        // Watermark taken before scripting, same as OrderScenarioHelpers.CallToolAsync's own
        // rejection-path pattern (PR #38 review item 3 / re-review follow-up 8: this test
        // hand-rolls its scripting instead of going through CallToolAsync, and so was missing
        // the same rejection-path checks that toClient:false callers get for free).
        var browserWatermark = browser.ReceivedFrames.Count;

        connection.Script.Enqueue(new ResponseScript([
            new FunctionCallEvent(
                Name: "update_order",
                ArgumentsJson: """{"action":"add","size":"medium","quantity":1,"price":2.79}""",
                CallId: callId),
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
            ShortFrameTimeout, ct);
        Assert.True(functionCallOutput is not null,
            "Expected a graceful function_call_output for the malformed call -- if this is null, the " +
            "tool exception killed the connection instead of producing a model-visible error.");
        var outputText = functionCallOutput!.Json.GetProperty("item").GetProperty("output").GetString() ?? "";

        // Session survives: prove the connection is still usable for a further tool call.
        var nextRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token" &&
                 f.Json.GetProperty("roundTripIndex").GetInt32() > roundTripIndex,
            ShortFrameTimeout, ct);
        Assert.True(nextRoundTrip is not null, "Round trip after the malformed call never completed.");
        var nextIndex = nextRoundTrip!.Json.GetProperty("roundTripIndex").GetInt32();

        // Rejection-path checks (PR #38 review item 3 / re-review must-fix 1 and follow-up 8),
        // run AFTER the round trip wait above -- not immediately after functionCallOutput -- for
        // the same reason OrderScenarioHelpers.CallToolAsync's own check is placed there: both
        // frames travel over the single, ordered browser WebSocket, so by the time this call's
        // round trip token has been received, anything the backend was ever going to send the
        // browser for this call has necessarily already arrived.
        var strayToolResponse = browser.ReceivedFrames.Snapshot().Any(f =>
            f.Sequence >= browserWatermark &&
            f.Type == "extension.middle_tier_tool_response" &&
            f.Json.TryGetProperty("tool_name", out var strayToolNameProp) &&
            strayToolNameProp.GetString() == "update_order");
        Assert.False(strayToolResponse,
            $"Expected no extension.middle_tier_tool_response for update_order (call_id={callId}) " +
            "to have reached the browser since the watermark for this deliberately malformed call.");
        Assert.False(OrderScenarioHelpers.LooksLikeOrderSummary(outputText),
            $"function_call_output for update_order (call_id={callId}) parses as a JSON " +
            "order-summary object, but this call was scripted to raise inside the tool handler -- " +
            "a genuine graceful error is a plain apology string, never an order summary.");
        Assert.Null(browser.CloseStatus);

        var next = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", "Tots", "medium", 1, 2.79m)],
            nextIndex, ct);
        var order = JsonDocument.Parse(next.ToolResultJson!).RootElement;
        Assert.Equal(1, order.GetProperty("items").GetArrayLength());
    }, allowedNewBackendErrors: 0);
    // #66 (non-blocking, verified): tools.py::update_order's #36 fix (see the required-argument
    // validation block a few lines above item_name = args["item_name"]) now catches a missing
    // "item_name" before ever reaching the bare-KeyError path this test's own docstring describes
    // -- it logs at logger.warning (not logger.exception/ERROR) and returns the same graceful
    // ToolResult as the "application-level" scenario above. CountUnhandledErrors only counts
    // ERROR:-level lines and Python tracebacks (see its own doc comment), so this WARNING no
    // longer counts as one; confirmed by running this scenario with allowedNewBackendErrors: 0
    // and observing it still pass. The stale allowedNewBackendErrors: 1 this line replaced dated
    // from before the #36 fix landed and was never revisited.
}
