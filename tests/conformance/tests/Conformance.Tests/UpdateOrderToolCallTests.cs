using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// PR #22 review item 3: prove a scripted function call actually executes in the backend, not
/// just that the fake can emit the right frame shapes. Scripts an `update_order` call for the
/// second turn (after the greeting), then asserts the backend actually ran the tool: a
/// `conversation.item.create` with a `function_call_output` item comes back upstream, and the
/// browser receives `extension.middle_tier_tool_response` — both only possible if rtmt.py's
/// `response.output_item.done` handler recognised the scripted item as a real, pending tool call.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class UpdateOrderToolCallTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Scripted_update_order_call_executes_and_notifies_the_browser() => fixture.RunAsync(async () =>
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

        // Wait for the greeting's round trip to fully complete (tools_pending cleared, backend
        // ready for a new turn) before scripting the next response — enqueuing any earlier would
        // let the automatic greeting response.create consume our script instead.
        var greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");

        const string callId = "call_update_order_1";
        connection!.Script.Enqueue(new ResponseScript([
            new FunctionCallEvent(
                Name: "update_order",
                ArgumentsJson: """{"action":"add","item_name":"Small Fries","size":"Small","quantity":1,"price":2.49}""",
                CallId: callId),
            new DoneEvent(),
        ]));

        // rtmt.py never handles a raw client response.create specially — it passes straight
        // through to the upstream socket unchanged, exactly like a server-VAD-triggered turn
        // would, without needing to simulate real audio timing.
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

        // The "backend logged no unhandled error during this scenario" invariant (PR #22 review
        // item N5) is now a fixture-wide, language-neutral check applied by
        // ConformanceFixture.RunAsync after every scenario -- no per-test assertion needed here.
    });
}
