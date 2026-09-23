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

    // --- PR #22 review item 9: validation fidelity, re-derived from the official GA reference
    // doc (not rtmt.py) and cross-checked with a live probe against the real service. See
    // GaSessionValidator's class doc and tests/conformance/README.md for citations and the
    // exact recorded live-probe JSON.

    [Fact]
    public async Task Session_update_without_session_type_is_rejected_as_missing_required_parameter()
    {
        await using var fake = new FakeRealtimeUpstreamServer();
        await fake.StartAsync(TestContext.Current.CancellationToken);

        using var socket = new ClientWebSocket();
        var wsUri = new Uri($"ws://{fake.BaseUri.Host}:{fake.BaseUri.Port}/openai/v1/realtime?model=gpt-realtime-test");
        await socket.ConnectAsync(wsUri, TestContext.Current.CancellationToken);
        Assert.NotNull(await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken)); // session.created

        // Otherwise-clean payload -- no unknown key, no reasoning/voice interplay -- so the only
        // possible rejection is the missing `session.type` discriminator.
        await WebSocketJson.SendAsync(socket, new JsonObject
        {
            ["type"] = "session.update",
            ["event_id"] = "evt_missing_type",
            ["session"] = new JsonObject { ["instructions"] = "hello" },
        }, TestContext.Current.CancellationToken);

        var error = await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(error);
        Assert.Equal("error", error!.Value.GetProperty("type").GetString());
        var errorBody = error.Value.GetProperty("error");
        Assert.Equal("missing_required_parameter", errorBody.GetProperty("code").GetString());
        Assert.Equal("session.type", errorBody.GetProperty("param").GetString());
        Assert.Equal("evt_missing_type", errorBody.GetProperty("event_id").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Session_update_with_unknown_top_level_key_is_rejected_as_unknown_parameter()
    {
        await using var fake = new FakeRealtimeUpstreamServer();
        await fake.StartAsync(TestContext.Current.CancellationToken);

        using var socket = new ClientWebSocket();
        var wsUri = new Uri($"ws://{fake.BaseUri.Host}:{fake.BaseUri.Port}/openai/v1/realtime?model=gpt-realtime-test");
        await socket.ConnectAsync(wsUri, TestContext.Current.CancellationToken);
        Assert.NotNull(await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken)); // session.created

        // Live-confirmed (2026-09-24): an unrecognised top-level session key returns
        // code "unknown_parameter" (NOT the coarse "invalid_request_error" outer error.type),
        // param "session.<key>", with event_id echoed.
        await WebSocketJson.SendAsync(socket, new JsonObject
        {
            ["type"] = "session.update",
            ["event_id"] = "evt_unknown_top_level",
            ["session"] = new JsonObject { ["type"] = "realtime", ["totally_bogus_key"] = "x" },
        }, TestContext.Current.CancellationToken);

        var error = await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(error);
        Assert.Equal("error", error!.Value.GetProperty("type").GetString());
        var errorBody = error.Value.GetProperty("error");
        Assert.Equal("unknown_parameter", errorBody.GetProperty("code").GetString());
        Assert.Equal("session.totally_bogus_key", errorBody.GetProperty("param").GetString());
        Assert.Equal("evt_unknown_top_level", errorBody.GetProperty("event_id").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Theory]
    [InlineData("input", "bogus_input_key")]
    [InlineData("output", "bogus_output_key")]
    public async Task Session_update_with_unknown_nested_audio_key_is_rejected(string side, string badKey)
    {
        await using var fake = new FakeRealtimeUpstreamServer();
        await fake.StartAsync(TestContext.Current.CancellationToken);

        using var socket = new ClientWebSocket();
        var wsUri = new Uri($"ws://{fake.BaseUri.Host}:{fake.BaseUri.Port}/openai/v1/realtime?model=gpt-realtime-test");
        await socket.ConnectAsync(wsUri, TestContext.Current.CancellationToken);
        Assert.NotNull(await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken)); // session.created

        await WebSocketJson.SendAsync(socket, new JsonObject
        {
            ["type"] = "session.update",
            ["event_id"] = "evt_bad_nested_audio",
            ["session"] = new JsonObject
            {
                ["type"] = "realtime",
                ["audio"] = new JsonObject { [side] = new JsonObject { [badKey] = true } },
            },
        }, TestContext.Current.CancellationToken);

        var error = await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(error);
        var errorBody = error!.Value.GetProperty("error");
        Assert.Equal("unknown_parameter", errorBody.GetProperty("code").GetString());
        Assert.Equal($"session.audio.{side}.{badKey}", errorBody.GetProperty("param").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Unknown_top_level_client_event_type_is_rejected_before_dispatch()
    {
        await using var fake = new FakeRealtimeUpstreamServer();
        await fake.StartAsync(TestContext.Current.CancellationToken);

        using var socket = new ClientWebSocket();
        var wsUri = new Uri($"ws://{fake.BaseUri.Host}:{fake.BaseUri.Port}/openai/v1/realtime?model=gpt-realtime-test");
        await socket.ConnectAsync(wsUri, TestContext.Current.CancellationToken);
        Assert.NotNull(await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken)); // session.created

        // A leaked internal frame type -- never valid to forward upstream unchanged.
        await WebSocketJson.SendAsync(socket, new JsonObject
        {
            ["type"] = "extension.middle_tier_tool_response",
            ["event_id"] = "evt_leaked_extension_frame",
        }, TestContext.Current.CancellationToken);

        var error = await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(error);
        var errorBody = error!.Value.GetProperty("error");
        Assert.Equal("invalid_value", errorBody.GetProperty("code").GetString());
        Assert.Equal("type", errorBody.GetProperty("param").GetString());
        Assert.Equal("evt_leaked_extension_frame", errorBody.GetProperty("event_id").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Handshake_with_missing_api_key_is_rejected_with_401_when_required()
    {
        await using var fake = new FakeRealtimeUpstreamServer { RequireApiKey = true, ExpectedApiKey = "expected-key" };
        await fake.StartAsync(TestContext.Current.CancellationToken);

        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        var wsUri = new Uri($"ws://{fake.BaseUri.Host}:{fake.BaseUri.Port}/openai/v1/realtime?model=gpt-realtime-test");
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(wsUri, TestContext.Current.CancellationToken));

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, socket.HttpStatusCode);
        Assert.Equal(0, fake.OpenConnectionCount);
    }

    [Fact]
    public async Task Handshake_with_wrong_api_key_is_rejected_with_401_when_expected_key_set()
    {
        await using var fake = new FakeRealtimeUpstreamServer { RequireApiKey = true, ExpectedApiKey = "expected-key" };
        await fake.StartAsync(TestContext.Current.CancellationToken);

        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("api-key", "totally-wrong-key");
        var wsUri = new Uri($"ws://{fake.BaseUri.Host}:{fake.BaseUri.Port}/openai/v1/realtime?model=gpt-realtime-test");
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(wsUri, TestContext.Current.CancellationToken));

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, socket.HttpStatusCode);
        Assert.Equal(0, fake.OpenConnectionCount);
    }

    [Fact]
    public async Task Handshake_with_correct_api_key_is_accepted_when_required()
    {
        await using var fake = new FakeRealtimeUpstreamServer { RequireApiKey = true, ExpectedApiKey = "expected-key" };
        await fake.StartAsync(TestContext.Current.CancellationToken);

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("api-key", "expected-key");
        var wsUri = new Uri($"ws://{fake.BaseUri.Host}:{fake.BaseUri.Port}/openai/v1/realtime?model=gpt-realtime-test");
        await socket.ConnectAsync(wsUri, TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketState.Open, socket.State);
        Assert.NotNull(await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken)); // session.created

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    // --- PR #22 review item 10: session.updated must echo the full effective session (GA's
    // session.update is a partial patch that accumulates, it doesn't replace the whole session).

    [Fact]
    public async Task Session_updated_echoes_the_full_accumulated_effective_session_across_updates()
    {
        await using var fake = new FakeRealtimeUpstreamServer();
        await fake.StartAsync(TestContext.Current.CancellationToken);

        using var socket = new ClientWebSocket();
        var wsUri = new Uri($"ws://{fake.BaseUri.Host}:{fake.BaseUri.Port}/openai/v1/realtime?model=gpt-realtime-test");
        await socket.ConnectAsync(wsUri, TestContext.Current.CancellationToken);
        Assert.NotNull(await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken)); // session.created

        // First update sets instructions/tools/tool_choice and audio.output.voice.
        await WebSocketJson.SendAsync(socket, new JsonObject
        {
            ["type"] = "session.update",
            ["event_id"] = "evt_first",
            ["session"] = new JsonObject
            {
                ["type"] = "realtime",
                ["instructions"] = "You are a drive-thru order taker.",
                ["tool_choice"] = "auto",
                ["tools"] = new JsonArray(new JsonObject { ["type"] = "function", ["name"] = "update_order" }),
                ["audio"] = new JsonObject { ["output"] = new JsonObject { ["voice"] = "marin" } },
            },
        }, TestContext.Current.CancellationToken);
        var firstUpdated = await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(firstUpdated);
        var firstSession = firstUpdated!.Value.GetProperty("session");
        Assert.Equal("You are a drive-thru order taker.", firstSession.GetProperty("instructions").GetString());
        Assert.Equal("marin", firstSession.GetProperty("audio").GetProperty("output").GetProperty("voice").GetString());

        // Second update only touches audio.input.format -- must not drop instructions/tools/voice
        // accumulated from the first update (GA's partial-patch semantics).
        await WebSocketJson.SendAsync(socket, new JsonObject
        {
            ["type"] = "session.update",
            ["event_id"] = "evt_second",
            ["session"] = new JsonObject
            {
                ["type"] = "realtime",
                ["audio"] = new JsonObject { ["input"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "audio/pcm" } } },
            },
        }, TestContext.Current.CancellationToken);
        var secondUpdated = await ReceiveJsonWithTimeoutAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(secondUpdated);
        var secondSession = secondUpdated!.Value.GetProperty("session");

        Assert.Equal("You are a drive-thru order taker.", secondSession.GetProperty("instructions").GetString());
        Assert.Equal("auto", secondSession.GetProperty("tool_choice").GetString());
        Assert.Equal(1, secondSession.GetProperty("tools").GetArrayLength());
        var audio = secondSession.GetProperty("audio");
        Assert.Equal("marin", audio.GetProperty("output").GetProperty("voice").GetString());
        Assert.Equal("audio/pcm", audio.GetProperty("input").GetProperty("format").GetProperty("type").GetString());
        // Server-assigned fields are always present and reflect this connection.
        Assert.Equal("sess_fake", secondSession.GetProperty("id").GetString());
        Assert.Equal("gpt-realtime-test", secondSession.GetProperty("model").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }
}

