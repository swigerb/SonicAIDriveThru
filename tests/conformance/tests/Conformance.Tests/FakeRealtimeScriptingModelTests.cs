using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Conformance.Fakes;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// PR #22 review item 8: exercises the per-connection scripting model directly against
/// <see cref="FakeRealtimeUpstreamServer"/> (no Python backend involved) — handshake rejection,
/// the VAD-like default rule triggers, and that a fresh connection never inherits another
/// connection's script state.
/// </summary>
public sealed class FakeRealtimeScriptingModelTests
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Bounds a single frame read to <see cref="FrameTimeout"/> so a scripting regression that
    /// silently drops an expected frame fails the test cleanly instead of hanging the test host.
    /// </summary>
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

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    public async Task RejectNextConnectionWith_fails_the_handshake_with_the_given_status(int statusCode)
    {
        await using var fake = new FakeRealtimeUpstreamServer();
        await fake.StartAsync(TestContext.Current.CancellationToken);
        fake.RejectNextConnectionWith(statusCode);

        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        var wsUri = new Uri($"ws://{fake.BaseUri.Host}:{fake.BaseUri.Port}/openai/v1/realtime?model=gpt-realtime-test");
        var ex = await Assert.ThrowsAsync<WebSocketException>(
            () => socket.ConnectAsync(wsUri, TestContext.Current.CancellationToken));

        Assert.Equal((System.Net.HttpStatusCode)statusCode, socket.HttpStatusCode);
        Assert.NotEqual(WebSocketState.Open, socket.State);
        // A dropped/rejected exception must not have registered a connection at all -- a test that
        // scripts a rejection and then a real connect must still see the real one as "the next".
        Assert.Equal(0, fake.OpenConnectionCount);
        Assert.NotNull(ex);
    }

    [Fact]
    public async Task RejectNextConnectionWith_only_affects_one_attempt()
    {
        await using var fake = new FakeRealtimeUpstreamServer();
        await fake.StartAsync(TestContext.Current.CancellationToken);
        fake.RejectNextConnectionWith(401);

        var wsUri = new Uri($"ws://{fake.BaseUri.Host}:{fake.BaseUri.Port}/openai/v1/realtime?model=gpt-realtime-test");

        using (var rejected = new ClientWebSocket())
        {
            await Assert.ThrowsAsync<WebSocketException>(
                () => rejected.ConnectAsync(wsUri, TestContext.Current.CancellationToken));
        }

        // The second attempt must succeed -- the rejection queue is one-shot, not sticky.
        using var accepted = new ClientWebSocket();
        var connectionTask = fake.WaitForNextConnectionAsync(FrameTimeout, TestContext.Current.CancellationToken);
        await accepted.ConnectAsync(wsUri, TestContext.Current.CancellationToken);
        var connection = await connectionTask;
        Assert.NotNull(connection);
        Assert.Equal(WebSocketState.Open, accepted.State);

        await accepted.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Input_audio_buffer_append_triggers_the_default_vad_like_acknowledgement_sequence()
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

        await WebSocketJson.SendAsync(socket, new JsonObject
        {
            ["type"] = "input_audio_buffer.append",
            ["audio"] = "dGVzdA==",
        }, TestContext.Current.CancellationToken);

        var expectedOrder = new[]
        {
            "input_audio_buffer.speech_started",
            "input_audio_buffer.speech_stopped",
            "input_audio_buffer.committed",
            "conversation.item.input_audio_transcription.completed",
        };
        foreach (var expectedType in expectedOrder)
        {
            var frame = await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken);
            Assert.NotNull(frame);
            Assert.Equal(expectedType, frame!.Value.GetProperty("type").GetString());
        }

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Conversation_item_create_triggers_a_conversation_item_added_acknowledgement()
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

        await WebSocketJson.SendAsync(socket, new JsonObject
        {
            ["type"] = "conversation.item.create",
            ["item"] = new JsonObject { ["id"] = "item_test_1", ["type"] = "message", ["role"] = "user" },
        }, TestContext.Current.CancellationToken);

        var added = await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(added);
        Assert.Equal("conversation.item.added", added!.Value.GetProperty("type").GetString());
        Assert.Equal("item_test_1", added.Value.GetProperty("item").GetProperty("id").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task A_fresh_connection_never_inherits_a_previous_connections_queued_script_or_rules()
    {
        await using var fake = new FakeRealtimeUpstreamServer();
        await fake.StartAsync(TestContext.Current.CancellationToken);
        var wsUri = new Uri($"ws://{fake.BaseUri.Host}:{fake.BaseUri.Port}/openai/v1/realtime?model=gpt-realtime-test");

        // Connection A: queue a failed response and clear its VAD-default rules.
        using (var socketA = new ClientWebSocket())
        {
            var connectionTaskA = fake.WaitForNextConnectionAsync(FrameTimeout, TestContext.Current.CancellationToken);
            await socketA.ConnectAsync(wsUri, TestContext.Current.CancellationToken);
            var connectionA = await connectionTaskA;
            Assert.NotNull(connectionA);
            connectionA!.Script.Enqueue(ResponseScript.Failed("connection A only"));
            connectionA.Script.Rules.Clear();
            await ReceiveJsonWithTimeoutAsync(socketA, TestContext.Current.CancellationToken); // session.created
            await socketA.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }

        // Connection B: a brand-new connection must see the VAD defaults (not the cleared rules
        // from A) and ResponseScript.Default (not A's queued failure) since nothing was ever
        // queued on B's own Script.
        using var socketB = new ClientWebSocket();
        var connectionTaskB = fake.WaitForNextConnectionAsync(FrameTimeout, TestContext.Current.CancellationToken);
        await socketB.ConnectAsync(wsUri, TestContext.Current.CancellationToken);
        var connectionB = await connectionTaskB;
        Assert.NotNull(connectionB);
        Assert.NotEmpty(connectionB!.Script.Rules);
        Assert.Empty(connectionB.Script.QueuedResponses);

        await ReceiveJsonWithTimeoutAsync(socketB, TestContext.Current.CancellationToken); // session.created
        await WebSocketJson.SendAsync(socketB, new JsonObject { ["type"] = "response.create" }, TestContext.Current.CancellationToken);
        Assert.NotNull(await ReceiveJsonWithTimeoutAsync(socketB, TestContext.Current.CancellationToken)); // response.created

        JsonElement? done = null;
        while (true)
        {
            var frame = await ReceiveJsonWithTimeoutAsync(socketB, TestContext.Current.CancellationToken);
            Assert.NotNull(frame);
            if (frame!.Value.GetProperty("type").GetString() == "response.done")
            {
                done = frame;
                break;
            }
        }

        Assert.NotNull(done);
        // ResponseScript.Default completes successfully; connection A's queued "Failed" script
        // must not have leaked onto connection B.
        Assert.Equal("completed", done!.Value.GetProperty("response").GetProperty("status").GetString());

        await socketB.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }
}

