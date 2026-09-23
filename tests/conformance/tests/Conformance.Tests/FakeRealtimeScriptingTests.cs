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

        // Setting a voice before any audio has been sent must be accepted.
        await WebSocketJson.SendAsync(socket, new JsonObject
        {
            ["type"] = "session.update",
            ["event_id"] = "evt_voice_1",
            ["session"] = new JsonObject { ["audio"] = new JsonObject { ["output"] = new JsonObject { ["voice"] = "alloy" } } },
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
            ["session"] = new JsonObject { ["audio"] = new JsonObject { ["output"] = new JsonObject { ["voice"] = "verse" } } },
        }, TestContext.Current.CancellationToken);
        var rejected = await WebSocketJson.ReceiveJsonAsync(socket, TestContext.Current.CancellationToken);
        Assert.NotNull(rejected);
        Assert.Equal("error", rejected!.Value.GetProperty("type").GetString());
        Assert.Equal("cannot_update_voice", rejected.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("evt_voice_2", rejected.Value.GetProperty("error").GetProperty("event_id").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }
}
