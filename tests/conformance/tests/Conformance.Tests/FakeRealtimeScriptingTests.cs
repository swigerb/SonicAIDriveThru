using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Conformance.Fakes;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Exercises <see cref="FakeRealtimeUpstreamServer"/> directly (no Python backend involved) to
/// prove out the scripting surface issue #7 asks for: audio deltas, a failed/rate-limited
/// `response.done` with a retry hint, and GA validation rejections. Fast and isolated — useful
/// as a template for S1.2+ scenarios that need finer control over upstream behaviour than the
/// shared <see cref="ConformanceFixture"/>'s single Python connection allows.
/// </summary>
public sealed class FakeRealtimeScriptingTests
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Scripted_response_done_can_report_a_rate_limited_failure_with_a_hint()
    {
        await using var fake = new FakeRealtimeUpstreamServer();
        await fake.StartAsync(TestContext.Current.CancellationToken);

        using var socket = new ClientWebSocket();
        var wsUri = new Uri($"ws://{fake.BaseUri.Host}:{fake.BaseUri.Port}/openai/v1/realtime?model=gpt-realtime-test");
        var connectionTask = fake.WaitForNextConnectionAsync(FrameTimeout, TestContext.Current.CancellationToken);
        await socket.ConnectAsync(wsUri, TestContext.Current.CancellationToken);
        var connection = await connectionTask;
        Assert.NotNull(connection);

        connection!.Script.Enqueue(ResponseScript.RateLimited("Rate limit reached. Please try again in 2s."));

        // session.created greeting frame.
        Assert.NotNull(await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken));

        await WebSocketJson.SendAsync(socket, new JsonObject { ["type"] = "response.create" }, TestContext.Current.CancellationToken);

        Assert.NotNull(await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken)); // response.created
        var done = await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken);

        Assert.NotNull(done);
        Assert.Equal("response.done", done!.Value.GetProperty("type").GetString());
        var response = done.Value.GetProperty("response");
        Assert.Equal("failed", response.GetProperty("status").GetString());
        var error = response.GetProperty("status_details").GetProperty("error");
        Assert.Equal("rate_limit_exceeded", error.GetProperty("code").GetString());
        Assert.Contains("try again in 2s", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        // #28 N12: status_details.error.type was missing entirely -- GA documents it as one of
        // exactly two properties on the error object (the other being `code`, asserted above).
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Unknown_top_level_session_keys_are_rejected()
    {
        await using var fake = new FakeRealtimeUpstreamServer();
        await fake.StartAsync(TestContext.Current.CancellationToken);

        using var socket = new ClientWebSocket();
        var wsUri = new Uri($"ws://{fake.BaseUri.Host}:{fake.BaseUri.Port}/openai/v1/realtime?model=gpt-realtime-test");
        await socket.ConnectAsync(wsUri, TestContext.Current.CancellationToken);
        Assert.NotNull(await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken)); // session.created

        await WebSocketJson.SendAsync(socket, new JsonObject
        {
            ["type"] = "session.update",
            ["event_id"] = "evt_1",
            ["session"] = new JsonObject { ["not_a_real_ga_key"] = true },
        }, TestContext.Current.CancellationToken);

        var error = await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(error);
        Assert.Equal("error", error!.Value.GetProperty("type").GetString());
        Assert.Equal("evt_1", error.Value.GetProperty("error").GetProperty("event_id").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Reasoning_is_rejected_on_1_5_style_deployments_without_echoing_event_id()
    {
        await using var fake = new FakeRealtimeUpstreamServer();
        await fake.StartAsync(TestContext.Current.CancellationToken);

        using var socket = new ClientWebSocket();
        var wsUri = new Uri($"ws://{fake.BaseUri.Host}:{fake.BaseUri.Port}/openai/v1/realtime?model=gpt-realtime-1.5");
        await socket.ConnectAsync(wsUri, TestContext.Current.CancellationToken);
        Assert.NotNull(await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken)); // session.created

        await WebSocketJson.SendAsync(socket, new JsonObject
        {
            ["type"] = "session.update",
            ["event_id"] = "evt_2",
            ["session"] = new JsonObject { ["reasoning"] = new JsonObject { ["effort"] = "low" } },
        }, TestContext.Current.CancellationToken);

        var error = await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(error);
        Assert.Equal("invalid_value", error!.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, error.Value.GetProperty("error").GetProperty("event_id").ValueKind);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    /// <summary>
    /// Self-test for the fake's own GA fidelity (item 4 of PR #22's review): `cannot_update_voice`
    /// is only real if something actually sets <c>RealtimeSessionState.AssistantAudioSeen</c> once
    /// audio has gone out — prove the flag flips by driving a real response through the default
    /// script (which emits an audio delta) rather than setting it directly.
    /// </summary>
    [Fact]
    public async Task Voice_cannot_be_changed_after_assistant_audio_has_been_sent()
    {
        await using var fake = new FakeRealtimeUpstreamServer();
        await fake.StartAsync(TestContext.Current.CancellationToken);

        using var socket = new ClientWebSocket();
        var wsUri = new Uri($"ws://{fake.BaseUri.Host}:{fake.BaseUri.Port}/openai/v1/realtime?model=gpt-realtime-test");
        await socket.ConnectAsync(wsUri, TestContext.Current.CancellationToken);
        Assert.NotNull(await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken)); // session.created

        // Setting a voice before any audio has been sent must be accepted. Includes the
        // required `session.type` discriminator (PR #22 review item 9) since this test's point
        // is voice-lock behaviour, not the type-required check exercised elsewhere.
        await WebSocketJson.SendAsync(socket, new JsonObject
        {
            ["type"] = "session.update",
            ["event_id"] = "evt_voice_1",
            ["session"] = new JsonObject { ["type"] = "realtime", ["audio"] = new JsonObject { ["output"] = new JsonObject { ["voice"] = "alloy" } } },
        }, TestContext.Current.CancellationToken);
        var accepted = await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(accepted);
        Assert.Equal("session.updated", accepted!.Value.GetProperty("type").GetString());

        // response.create with no queued script uses ResponseScript.Default, which emits the full
        // GA item lifecycle (output_item.added -> ... -> output_audio.delta -> ... -> response.done)
        // for one audio delta — drain all of it; only response.done flipping AssistantAudioSeen
        // (via the delta in between) is what this test actually cares about.
        await WebSocketJson.SendAsync(socket, new JsonObject { ["type"] = "response.create" }, TestContext.Current.CancellationToken);
        JsonElement? frame;
        do
        {
            frame = await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken);
            Assert.NotNull(frame);
        } while (frame!.Value.GetProperty("type").GetString() != "response.done");

        // Now changing the voice must be rejected.
        await WebSocketJson.SendAsync(socket, new JsonObject
        {
            ["type"] = "session.update",
            ["event_id"] = "evt_voice_2",
            ["session"] = new JsonObject { ["type"] = "realtime", ["audio"] = new JsonObject { ["output"] = new JsonObject { ["voice"] = "verse" } } },
        }, TestContext.Current.CancellationToken);
        var rejected = await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(rejected);
        Assert.Equal("error", rejected!.Value.GetProperty("type").GetString());
        Assert.Equal("cannot_update_voice", rejected.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("evt_voice_2", rejected.Value.GetProperty("error").GetProperty("event_id").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    /// <summary>
    /// Self-test for the fake's own GA fidelity (PR #30 review "G1"/item 2): a repeated
    /// client-created item id within one connection must be rejected with exactly the error GA
    /// returns (live-verified against gpt-realtime-2.1 / gpt-realtime-2.1-dz), not silently
    /// re-acknowledged. This is what makes the backend's item-id-uniqueness guarantee provable
    /// black-box instead of only by Python unit test.
    /// </summary>
    [Fact]
    public async Task Conversation_item_create_with_a_repeated_id_is_rejected_with_the_ga_duplicate_error()
    {
        await using var fake = new FakeRealtimeUpstreamServer();
        await fake.StartAsync(TestContext.Current.CancellationToken);

        using var socket = new ClientWebSocket();
        var wsUri = new Uri($"ws://{fake.BaseUri.Host}:{fake.BaseUri.Port}/openai/v1/realtime?model=gpt-realtime-test");
        await socket.ConnectAsync(wsUri, TestContext.Current.CancellationToken);
        Assert.NotNull(await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken)); // session.created

        static JsonObject DupItem() => new()
        {
            ["id"] = "sonic_mt_dup0001test",
            ["type"] = "message",
            ["role"] = "user",
            ["content"] = new JsonArray { new JsonObject { ["type"] = "input_text", ["text"] = "hi" } },
        };

        await WebSocketJson.SendAsync(socket, new JsonObject { ["type"] = "conversation.item.create", ["item"] = DupItem() }, TestContext.Current.CancellationToken);
        var added = await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(added);
        Assert.Equal("conversation.item.added", added!.Value.GetProperty("type").GetString());
        var done = await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(done);
        Assert.Equal("conversation.item.done", done!.Value.GetProperty("type").GetString());

        // Re-sending the exact same id must be rejected instead of acknowledged again.
        await WebSocketJson.SendAsync(socket, new JsonObject { ["type"] = "conversation.item.create", ["item"] = DupItem() }, TestContext.Current.CancellationToken);
        var rejected = await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(rejected);
        Assert.Equal("invalid_request_error", rejected!.Value.GetProperty("type").GetString());
        Assert.Equal("item_create_duplicate_item_id", rejected.Value.GetProperty("code").GetString());
        Assert.Equal(
            "Error adding item: an item with id 'sonic_mt_dup0001test' already exists.",
            rejected.Value.GetProperty("message").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, rejected.Value.GetProperty("param").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, rejected.Value.GetProperty("event_id").ValueKind);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    /// <summary>
    /// Self-test for PR #30 review "G1" item 2's other half: GA sets `previous_item_id` on the
    /// *next* item's `.added` to the id of the item that came before it, client-created ids
    /// included. Before this fix the fake read <c>LastConversationItemId</c> for a client item's
    /// own acknowledgement but never advanced it, so a second client item always saw a stale (or
    /// null) predecessor instead of the first item's real id.
    /// </summary>
    [Fact]
    public async Task Conversation_item_create_chains_previous_item_id_across_client_created_items()
    {
        await using var fake = new FakeRealtimeUpstreamServer();
        await fake.StartAsync(TestContext.Current.CancellationToken);

        using var socket = new ClientWebSocket();
        var wsUri = new Uri($"ws://{fake.BaseUri.Host}:{fake.BaseUri.Port}/openai/v1/realtime?model=gpt-realtime-test");
        await socket.ConnectAsync(wsUri, TestContext.Current.CancellationToken);
        Assert.NotNull(await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken)); // session.created

        static JsonObject Item(string id) => new()
        {
            ["id"] = id,
            ["type"] = "message",
            ["role"] = "user",
            ["content"] = new JsonArray { new JsonObject { ["type"] = "input_text", ["text"] = "hi" } },
        };

        await WebSocketJson.SendAsync(socket, new JsonObject { ["type"] = "conversation.item.create", ["item"] = Item("sonic_mt_first0001") }, TestContext.Current.CancellationToken);
        var firstAdded = await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(firstAdded);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, firstAdded!.Value.GetProperty("previous_item_id").ValueKind);
        Assert.NotNull(await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken)); // .done

        await WebSocketJson.SendAsync(socket, new JsonObject { ["type"] = "conversation.item.create", ["item"] = Item("sonic_mt_second0001") }, TestContext.Current.CancellationToken);
        var secondAdded = await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(secondAdded);
        Assert.Equal("sonic_mt_first0001", secondAdded!.Value.GetProperty("previous_item_id").GetString());
        var secondDone = await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(secondDone);
        Assert.Equal("sonic_mt_first0001", secondDone!.Value.GetProperty("previous_item_id").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    /// <summary>
    /// Self-test for #28 N18: before this fix, `conversation.item.retrieve` matched
    /// <see cref="GaSessionValidator.GaClientEventTypes"/>'s allow-list (so it never hit the
    /// "unrecognised type" rejection) but had no case in `HandleFrameAsync`'s switch, so the fake
    /// silently swallowed it -- leaving the backend's `conversation.item.retrieved` scrub
    /// (rtmt.py) provable only by Python unit test, never black-box. Creates a real item first so
    /// retrieval has real, previously-observed content to return, then asserts the retrieved item
    /// echoes that exact content back.
    /// </summary>
    [Fact]
    public async Task Conversation_item_retrieve_returns_the_previously_created_item()
    {
        await using var fake = new FakeRealtimeUpstreamServer();
        await fake.StartAsync(TestContext.Current.CancellationToken);

        using var socket = new ClientWebSocket();
        var wsUri = new Uri($"ws://{fake.BaseUri.Host}:{fake.BaseUri.Port}/openai/v1/realtime?model=gpt-realtime-test");
        await socket.ConnectAsync(wsUri, TestContext.Current.CancellationToken);
        Assert.NotNull(await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken)); // session.created

        await WebSocketJson.SendAsync(socket, new JsonObject
        {
            ["type"] = "conversation.item.create",
            ["item"] = new JsonObject
            {
                ["id"] = "sonic_mt_retrieve0001",
                ["type"] = "message",
                ["role"] = "user",
                ["content"] = new JsonArray { new JsonObject { ["type"] = "input_text", ["text"] = "large fries" } },
            },
        }, TestContext.Current.CancellationToken);
        Assert.NotNull(await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken)); // .added
        Assert.NotNull(await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken)); // .done

        await WebSocketJson.SendAsync(socket, new JsonObject
        {
            ["type"] = "conversation.item.retrieve",
            ["event_id"] = "evt_retrieve_1",
            ["item_id"] = "sonic_mt_retrieve0001",
        }, TestContext.Current.CancellationToken);

        // Bounded (not TestContext.Current.CancellationToken) so a regression that makes the fake
        // silently swallow conversation.item.retrieve again fails fast with a clear timeout
        // instead of hanging the run indefinitely.
        using var retrieveTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        retrieveTimeout.CancelAfter(FrameTimeout);
        var retrieved = await WebSocketJson.ReceiveJsonAsync(socket, retrieveTimeout.Token);
        Assert.NotNull(retrieved);
        Assert.Equal("conversation.item.retrieved", retrieved!.Value.GetProperty("type").GetString());
        Assert.Equal("sonic_mt_retrieve0001", retrieved.Value.GetProperty("item_id").GetString());
        var item = retrieved.Value.GetProperty("item");
        Assert.Equal("sonic_mt_retrieve0001", item.GetProperty("id").GetString());
        Assert.Equal("user", item.GetProperty("role").GetString());
        Assert.Equal("large fries", item.GetProperty("content")[0].GetProperty("text").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    /// <summary>
    /// Self-test for #28 N18's other half: GA responds with an error, not a silent no-op or a
    /// crash, when the requested item id was never seen on this connection.
    /// </summary>
    [Fact]
    public async Task Conversation_item_retrieve_for_an_unknown_id_returns_an_error()
    {
        await using var fake = new FakeRealtimeUpstreamServer();
        await fake.StartAsync(TestContext.Current.CancellationToken);

        using var socket = new ClientWebSocket();
        var wsUri = new Uri($"ws://{fake.BaseUri.Host}:{fake.BaseUri.Port}/openai/v1/realtime?model=gpt-realtime-test");
        await socket.ConnectAsync(wsUri, TestContext.Current.CancellationToken);
        Assert.NotNull(await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken)); // session.created

        await WebSocketJson.SendAsync(socket, new JsonObject
        {
            ["type"] = "conversation.item.retrieve",
            ["event_id"] = "evt_retrieve_2",
            ["item_id"] = "sonic_mt_never_existed",
        }, TestContext.Current.CancellationToken);

        // See the sibling test's comment: bounded so a swallowed conversation.item.retrieve
        // fails fast instead of hanging.
        using var errorTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        errorTimeout.CancelAfter(FrameTimeout);
        var error = await WebSocketJson.ReceiveJsonAsync(socket, errorTimeout.Token);
        Assert.NotNull(error);
        Assert.Equal("error", error!.Value.GetProperty("type").GetString());
        Assert.Equal("item_not_found", error.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("evt_retrieve_2", error.Value.GetProperty("error").GetProperty("event_id").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }
}
