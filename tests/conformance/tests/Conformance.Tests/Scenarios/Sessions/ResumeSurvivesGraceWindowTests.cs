using System.Net.WebSockets;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Rick's PR #52 review, S3: none of the resume-scenario tests in this suite actually proved the
/// property <c>session_manager.py</c>'s <c>resume()</c> is supposed to guarantee -- that a
/// successfully resumed session is genuinely re-attached, not merely re-attached *for now* while
/// still secretly ticking down the *original* detach's grace clock in the background. Rick's own
/// mutation (deleting <c>self._detached.pop(session_id, None)</c> from <c>resume()</c>) leaves the
/// full existing suite green, because every existing resume test only checks behaviour *within* a
/// few seconds of resuming, never past the original detach's grace window.
///
/// <c>detached_expires_at(session_id)</c> is <c>min(detached_at + grace_seconds, last_activity +
/// idle_timeout_seconds)</c>, and <c>detached_at</c> is fixed at the moment of the *original*
/// detach -- it is never refreshed by activity. If <c>resume()</c> does not remove
/// <c>session_id</c> from <c>_detached</c>, the periodic sweep (<c>sweep_detached</c>,
/// <see cref="BackendProfiles.ResumeMargin"/>'s 0.2s interval) will still see the session as
/// detached and will forcibly <c>end_session</c> it once real wall-clock time reaches
/// <c>detached_at + grace_seconds</c> -- <see cref="BackendProfiles.ResumeMargin"/>'s 5s -- no
/// matter how long ago the resume happened or how much genuine activity has occurred since. This
/// test waits past that exact window (~6s from the original detach, matching Rick's "~5.5s"
/// framing plus headroom for scheduling jitter under full-suite load) with real keepalive traffic
/// on the resumed socket throughout (so a correct implementation's <c>idle_timeout_seconds</c>
/// arm of the same min() can't independently explain a pass), then asserts the socket is still
/// open and a normal tool round trip still completes.
///
/// Mutation-check: temporarily deleting <c>self._detached.pop(session_id, None)</c> from
/// <c>resume()</c> in app/backend/session_manager.py (scratch edit only, restored after -- this
/// stream does not commit changes under app/backend/) turns this test red: the resumed session is
/// forcibly ended by the sweep partway through the wait, so the final tool round trip's
/// <c>response.create</c> either never produces <c>extension.middle_tier_tool_response</c> before
/// <see cref="FrameTimeout"/>, or the socket itself has already transitioned out of
/// <see cref="WebSocketState.Open"/> by the time the post-wait assertion runs. Restoring the
/// <c>pop</c> call makes it pass reliably again. See the PR #52 CI follow-up report for the full
/// revert/restore run log.
/// </summary>
[Collection(ResumeMarginConformanceCollection.Name)]
public sealed class ResumeSurvivesGraceWindowTests(ResumeMarginConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <see cref="BackendProfiles.ResumeMargin"/>'s grace_seconds (5s) plus a comfortable margin
    /// above it, so the wait reliably spans the original detach's grace deadline even accounting
    /// for the small amount of wall-clock time the greeting and the resume handshake themselves
    /// take (both start their own clock no earlier than the detach), and for scheduling jitter
    /// when this runs alongside the rest of the suite in parallel.
    /// </summary>
    private static readonly TimeSpan PastGraceMargin = TimeSpan.FromMilliseconds(6000);

    [Fact]
    public Task Resumed_session_survives_past_the_original_detachs_grace_window_with_a_working_tool_round_trip() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        // ── First connection: start a conversation and let the greeting complete. ──
        var firstConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        var first = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var firstConnection = await firstConnectionTask;
        Assert.True(firstConnection is not null, "No upstream connection was accepted for the first browser socket.");

        await first.SendStartSessionAsync(cancellationToken: ct);

        var metadata = await first.ReceivedFrames.WaitForAsync(f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(metadata is not null, "Expected extension.session_metadata on the first connection.");
        var resumeId = metadata!.Json.GetProperty("resumeId").GetString();
        Assert.False(string.IsNullOrEmpty(resumeId), "extension.session_metadata must carry a non-empty resumeId.");

        var greetingDone = await first.ReceivedFrames.WaitForAsync(f => f.Type == "response.done", FrameTimeout, ct);
        Assert.True(greetingDone is not null, "Expected the greeting's response.done on the first connection.");

        // ── Detach: a plain transport close, exactly like a dropped connection. This is what
        // starts session_manager.py's grace clock (detached_at = now). ──
        var detachStartedAt = DateTimeOffset.UtcNow;
        await first.CloseAsync(cancellationToken: ct);
        await first.WaitForCloseAsync(FrameTimeout, ct);
        await first.DisposeAsync();

        // ── Resume immediately, well within the grace window. ──
        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var second = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var secondConnection = await secondConnectionTask;
        Assert.True(secondConnection is not null, "No upstream connection was accepted for the resumed browser socket.");

        await second.SendExtensionResumeAsync(resumeId!, ct);

        var resumed = await second.ReceivedFrames.WaitForAsync(f => f.Type == "extension.session_resumed", FrameTimeout, ct);
        Assert.True(resumed is not null, "Expected extension.session_resumed on the resumed connection.");

        // ── The actual regression check: wait past the *original* detach's grace deadline with
        // real activity on the resumed socket throughout (so a correct implementation's idle
        // timeout, not just its grace window, has real headroom the whole time too -- ruling out
        // "it merely wasn't idle long enough" as an alternate explanation for a pass). A buggy
        // resume() that leaves session_id in _detached will have the sweep end this session out
        // from under the resumed socket once real time reaches detachStartedAt + grace_seconds,
        // regardless of this activity, since detached_at is never refreshed. ──
        var keepAlive = KeepAlive.RunAsync(second, TimeSpan.FromMilliseconds(300), ct);
        var remaining = PastGraceMargin - (DateTimeOffset.UtcNow - detachStartedAt);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, ct);
        }
        await keepAlive.StopAsync();

        Assert.NotEqual(WebSocketState.Closed, second.SocketState);
        Assert.NotEqual(WebSocketState.Aborted, second.SocketState);
        Assert.NotEqual(WebSocketState.CloseReceived, second.SocketState);

        // ── The resumed session must still be functionally alive, not merely not-yet-torn-down:
        // a normal tool round trip, exactly like UpdateOrderToolCallTests / WholeSessionLeakTests'
        // own tool round trip. ──
        const string callId = "call_resume_survives_grace_1";
        secondConnection!.Script.Enqueue(new ResponseScript([
            new FunctionCallEvent(
                Name: "update_order",
                ArgumentsJson: """{"action":"add","item_name":"Small Fries","size":"Small","quantity":1,"price":2.49}""",
                CallId: callId),
            new DoneEvent(),
        ]));
        await second.SendResponseCreateAsync(ct);

        var functionCallOutput = await secondConnection.ReceivedFrames.WaitForAsync(
            f => f.Type == "conversation.item.create" &&
                 f.Json.TryGetProperty("item", out var item) &&
                 item.TryGetProperty("type", out var itemType) &&
                 itemType.GetString() == "function_call_output" &&
                 item.TryGetProperty("call_id", out var respondedCallId) &&
                 respondedCallId.GetString() == callId,
            FrameTimeout, ct);
        Assert.True(functionCallOutput is not null,
            $"Expected a conversation.item.create(function_call_output) for call_id={callId} upstream -- " +
            "the resumed session must still be able to round-trip a tool call after surviving the original detach's grace window.");

        var toolResponse = await second.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.middle_tier_tool_response" &&
                 f.Json.TryGetProperty("tool_name", out var toolName) &&
                 toolName.GetString() == "update_order",
            FrameTimeout, ct);
        Assert.True(toolResponse is not null,
            "Expected extension.middle_tier_tool_response for update_order on the resumed browser socket.");
    });
}
