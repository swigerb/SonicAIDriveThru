using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Conformance.Fakes;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// PR #22 review item N7: exercises <see cref="FakeRealtimeUpstreamServer"/>'s built-in
/// `response.cancel` handling directly (no Python backend involved) against the GA semantics
/// documented at <see href="https://developers.openai.com/api/reference/resources/realtime"/>
/// (fetched 2026-09-24, "Response Cancel Event" / "Response Create Event"): cancelling an active
/// response stops it early with `response.done` `status: "cancelled"`; cancelling with nothing
/// active is an error and leaves the session otherwise unaffected; and only one response may be
/// active on the default conversation at a time. The exact error codes/params/messages for the two
/// error paths are NOT independently live-verified — see
/// <c>tests/conformance/README.md</c>, "Response cancel — GA semantics and unverified error codes".
/// </summary>
public sealed class ResponseCancelTests
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Bounds a single frame read so a regression that silently drops an expected frame
    /// (or fails to interrupt a paced stream) fails the test cleanly instead of hanging the host.</summary>
    private static async Task<JsonElement?> ReceiveJsonWithTimeoutAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(FrameTimeout);
        try
        {
            return await WebSocketJson.ReceiveJsonAsync(socket, cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No frame received within {FrameTimeout}.");
        }
    }

    [Fact]
    public async Task Cancel_with_nothing_active_is_rejected_and_the_session_remains_unaffected()
    {
        await using var fake = new FakeRealtimeUpstreamServer();
        await fake.StartAsync(TestContext.Current.CancellationToken);

        using var socket = new ClientWebSocket();
        var wsUri = new Uri($"ws://{fake.BaseUri.Host}:{fake.BaseUri.Port}/openai/v1/realtime?model=gpt-realtime-test");
        await socket.ConnectAsync(wsUri, TestContext.Current.CancellationToken);
        Assert.NotNull(await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken)); // session.created

        // Never sent a response.create -- nothing is active on this connection.
        await WebSocketJson.SendAsync(socket, new JsonObject
        {
            ["type"] = "response.cancel",
            ["event_id"] = "evt_cancel_nothing_active",
        }, TestContext.Current.CancellationToken);

        var error = await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(error);
        Assert.Equal("error", error!.Value.GetProperty("type").GetString());
        var errorBody = error.Value.GetProperty("error");
        Assert.Equal("invalid_request_error", errorBody.GetProperty("type").GetString());
        Assert.Equal("response_cancel_not_active", errorBody.GetProperty("code").GetString());
        Assert.Equal("evt_cancel_nothing_active", errorBody.GetProperty("event_id").GetString());

        // GA: "an error will be returned the session will remain unaffected" -- prove it by
        // driving a completely normal response afterward on the same connection.
        await WebSocketJson.SendAsync(socket, new JsonObject { ["type"] = "response.create" }, TestContext.Current.CancellationToken);
        JsonElement? frame;
        do
        {
            frame = await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken);
            Assert.NotNull(frame);
        } while (frame!.Value.GetProperty("type").GetString() != "response.done");
        Assert.Equal("completed", frame!.Value.GetProperty("response").GetProperty("status").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Cancel_while_streaming_stops_the_response_early_and_reports_cancelled_status()
    {
        await using var fake = new FakeRealtimeUpstreamServer();
        await fake.StartAsync(TestContext.Current.CancellationToken);

        using var socket = new ClientWebSocket();
        var wsUri = new Uri($"ws://{fake.BaseUri.Host}:{fake.BaseUri.Port}/openai/v1/realtime?model=gpt-realtime-test");
        var connectionTask = fake.WaitForNextConnectionAsync(FrameTimeout, TestContext.Current.CancellationToken);
        await socket.ConnectAsync(wsUri, TestContext.Current.CancellationToken);
        var connection = await connectionTask;
        Assert.NotNull(connection);
        Assert.NotNull(await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken)); // session.created

        // First delta arrives immediately (unpaced); the second is paced 2s out so there's a wide
        // window to send response.cancel while the response is genuinely still streaming, without
        // the test itself needing a real 2s wait in the success path -- cancellation is expected
        // to interrupt the paced delay well before it elapses.
        connection!.Script.Enqueue(new ResponseScript(
        [
            new AudioDeltaEvent("Zmlyc3QtZGVsdGE="),
            new AudioDeltaEvent("c2Vjb25kLWRlbHRh", Pace: TimeSpan.FromSeconds(2)),
            new DoneEvent(),
        ]));

        await WebSocketJson.SendAsync(socket, new JsonObject { ["type"] = "response.create" }, TestContext.Current.CancellationToken);

        var created = await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        Assert.Equal("response.created", created!.Value.GetProperty("type").GetString());
        var responseId = created.Value.GetProperty("response").GetProperty("id").GetString();

        Assert.Equal("response.output_item.added", (await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken))!.Value.GetProperty("type").GetString());
        Assert.Equal("conversation.item.added", (await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken))!.Value.GetProperty("type").GetString());
        Assert.Equal("response.content_part.added", (await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken))!.Value.GetProperty("type").GetString());
        Assert.Equal("response.output_audio.delta", (await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken))!.Value.GetProperty("type").GetString());

        // The response is genuinely mid-stream now (first delta out, second one 2s away).
        await WebSocketJson.SendAsync(socket, new JsonObject
        {
            ["type"] = "response.cancel",
            ["event_id"] = "evt_cancel_active",
            ["response_id"] = responseId,
        }, TestContext.Current.CancellationToken);

        Assert.Equal("response.output_audio.done", (await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken))!.Value.GetProperty("type").GetString());
        Assert.Equal("response.content_part.done", (await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken))!.Value.GetProperty("type").GetString());

        var itemDone = await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(itemDone);
        Assert.Equal("response.output_item.done", itemDone!.Value.GetProperty("type").GetString());
        // Stopped mid-stream, not finished -- "incomplete", not "completed" (item 4's audio item
        // status distinction extended to the cancel path).
        Assert.Equal("incomplete", itemDone.Value.GetProperty("item").GetProperty("status").GetString());

        // GA also emits conversation.item.done "when the item is finalized" -- a second, separate
        // event on the conversation.item.* family carrying the same (now-incomplete) item again,
        // right after response.output_item.done (PR #30 review "M1"/"M2").
        var conversationItemDone = await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(conversationItemDone);
        Assert.Equal("conversation.item.done", conversationItemDone!.Value.GetProperty("type").GetString());
        Assert.Equal("incomplete", conversationItemDone.Value.GetProperty("item").GetProperty("status").GetString());

        var done = await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(done);
        Assert.Equal("response.done", done!.Value.GetProperty("type").GetString());
        var response = done.Value.GetProperty("response");
        Assert.Equal(responseId, response.GetProperty("id").GetString());
        Assert.Equal("cancelled", response.GetProperty("status").GetString());
        // Exactly one output item (the incomplete audio item) -- the second, paced delta and the
        // scripted DoneEvent's own (would-be "completed") response.done must never have run.
        Assert.Equal(1, response.GetProperty("output").GetArrayLength());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Response_create_while_a_response_is_already_active_is_rejected()
    {
        await using var fake = new FakeRealtimeUpstreamServer();
        await fake.StartAsync(TestContext.Current.CancellationToken);

        using var socket = new ClientWebSocket();
        var wsUri = new Uri($"ws://{fake.BaseUri.Host}:{fake.BaseUri.Port}/openai/v1/realtime?model=gpt-realtime-test");
        var connectionTask = fake.WaitForNextConnectionAsync(FrameTimeout, TestContext.Current.CancellationToken);
        await socket.ConnectAsync(wsUri, TestContext.Current.CancellationToken);
        var connection = await connectionTask;
        Assert.NotNull(connection);
        Assert.NotNull(await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken)); // session.created

        // A short (but real, non-racy) pace on the first response's only delta gives a
        // comfortably wide window in which ActiveResponseId is guaranteed still set -- with
        // ResponseScript.Default's unpaced delta, RespondAsync can race all the way to completion
        // (clearing ActiveResponseId) before this test's second response.create round-trips back
        // to the server, which was observed to flake without pacing here.
        connection!.Script.Enqueue(new ResponseScript(
        [
            new AudioDeltaEvent("b25seS1kZWx0YQ==", Pace: TimeSpan.FromMilliseconds(300)),
            new DoneEvent(),
        ]));

        await WebSocketJson.SendAsync(socket, new JsonObject { ["type"] = "response.create" }, TestContext.Current.CancellationToken);
        var created = await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        Assert.Equal("response.created", created!.Value.GetProperty("type").GetString());
        var firstResponseId = created.Value.GetProperty("response").GetProperty("id").GetString();

        // Well inside the 300ms pace window -- the first response is still active.
        await WebSocketJson.SendAsync(socket, new JsonObject
        {
            ["type"] = "response.create",
            ["event_id"] = "evt_second_create",
        }, TestContext.Current.CancellationToken);

        var rejected = await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(rejected);
        Assert.Equal("error", rejected!.Value.GetProperty("type").GetString());
        var errorBody = rejected.Value.GetProperty("error");
        Assert.Equal("invalid_request_error", errorBody.GetProperty("type").GetString());
        Assert.Equal("conversation_already_has_active_response", errorBody.GetProperty("code").GetString());
        Assert.Equal("evt_second_create", errorBody.GetProperty("event_id").GetString());

        // Cancel the first (only legitimate) response rather than waiting out its pace -- proves
        // the rejected second create left it perfectly usable, without slowing the test down.
        await WebSocketJson.SendAsync(socket, new JsonObject
        {
            ["type"] = "response.cancel",
            ["response_id"] = firstResponseId,
        }, TestContext.Current.CancellationToken);

        JsonElement? frame;
        do
        {
            frame = await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken);
            Assert.NotNull(frame);
        } while (frame!.Value.GetProperty("type").GetString() != "response.done");
        Assert.Equal("cancelled", frame!.Value.GetProperty("response").GetProperty("status").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }
}
