using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// PR #58 re-review "S3" (issue #36): Rick's second black-box probe pinning rtmt.py's layer-1
/// <c>except Exception</c> safety net -- <c>update_order</c> called with malformed (non-JSON)
/// <c>arguments</c>, distinct from <see cref="ToolFailureCapAndTicketRefreshTests"/>'s
/// non-numeric-<c>price</c> probe (a genuine exception <i>inside</i> the tool handler). This one
/// never reaches the tool handler at all: <c>rtmt.py</c>'s
/// <c>args = json.loads(item["arguments"])</c> (the very first line inside the try block, before
/// <c>tool.target(...)</c> is ever called) raises <c>json.JSONDecodeError</c> for a malformed
/// string, and only the same layer-1 <c>except Exception</c> catches it -- there is no layer-2
/// equivalent for malformed JSON (<c>tools.py</c>'s own validation only runs once <c>args</c> is
/// already a dict).
///
/// Both this and the non-numeric-price probe assert the same three things Rick's S3 asked for:
/// the session survives, a function_call_output reaches the server, and a subsequent tool call
/// still succeeds.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class ToolMalformedArgumentsTests(ConformanceFixture fixture)
{
    // Deliberately not valid JSON -- rtmt.py must never reach tool.target(...) for this call.
    private const string MalformedArgsJson = "{not json";

    [Fact]
    public Task Malformed_tool_arguments_produce_a_graceful_error_and_the_session_survives() =>
        fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        const string callId = "call_malformed_args";
        var browserWatermark = browser.ReceivedFrames.Count;

        connection.Script.Enqueue(new ResponseScript([
            new FunctionCallEvent(Name: "update_order", ArgumentsJson: MalformedArgsJson, CallId: callId),
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
            "Expected a graceful function_call_output for the malformed-arguments call -- if this " +
            "is null, the json.loads(...) failure killed the connection instead of producing a " +
            "model-visible error (swigerb/SonicAIDriveThru#36 S3).");
        var outputText = functionCallOutput!.Json.GetProperty("item").GetProperty("output").GetString() ?? "";
        Assert.False(OrderScenarioHelpers.LooksLikeOrderSummary(outputText),
            "function_call_output for the malformed-arguments call parses as a JSON order-summary " +
            "object, but this call could never have reached the tool handler at all -- a genuine " +
            "graceful error is a plain apology string, never an order summary.");

        // Session survives: the connection is still usable for a further round trip.
        var nextRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token" &&
                 f.Json.GetProperty("roundTripIndex").GetInt32() > roundTripIndex,
            OrderScenarioHelpers.FrameTimeout, ct);
        Assert.True(nextRoundTrip is not null,
            "Round trip after the malformed-arguments call never completed.");
        var nextIndex = nextRoundTrip!.Json.GetProperty("roundTripIndex").GetInt32();

        // No stray extension.middle_tier_tool_response for this call -- it never ran, so there is
        // no result (successful or otherwise) to relay to the browser.
        var strayToolResponse = browser.ReceivedFrames.Snapshot().Any(f =>
            f.Sequence >= browserWatermark &&
            f.Type == "extension.middle_tier_tool_response" &&
            f.Json.TryGetProperty("tool_name", out var strayToolNameProp) &&
            strayToolNameProp.GetString() == "update_order");
        Assert.False(strayToolResponse,
            $"Expected no extension.middle_tier_tool_response for update_order (call_id={callId}) " +
            "to have reached the browser for this malformed call.");
        Assert.Null(browser.CloseStatus);

        // A subsequent, well-formed tool call still succeeds -- the connection is fully usable.
        var next = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", "Tots", "medium", 1, 2.79m)],
            nextIndex, ct);
        var order = JsonDocument.Parse(next.ToolResultJson!).RootElement;
        Assert.Equal(1, order.GetProperty("items").GetArrayLength());
    }, allowedNewBackendErrors: 1);
}
