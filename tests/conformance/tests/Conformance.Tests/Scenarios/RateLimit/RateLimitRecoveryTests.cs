using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.RateLimit;

/// <summary>
/// Issue #10: rate-limit recovery conformance. app/backend/rate_limit.py's RateLimitRecovery
/// retries a rate-limited response.create up to `max_retries` (2) times, silently on the first
/// failure and with a client-visible `extension.rate_limited {attempt}` notification on every
/// failure after that (the final one carrying `final:true`), then gives up until the guest's next
/// turn. Retries are sent directly to the upstream socket (bypassing the browser-frame path
/// entirely), so they never touch the idle clock and never re-execute a tool whose
/// function_call_output is already a permanent part of the upstream conversation. Scripted
/// failures below always set ErrorMessage to null (no service hint text) so
/// rate_limit.py.parse_retry_hint() finds nothing to clamp and ShortTimers'
/// CONFORMANCE_RATE_LIMIT_RETRY_DELAY_SECONDS=0.2 / ..._SECOND_...=0.4 overrides apply directly —
/// see FIRST_RETRY_BOUNDS/SECOND_RETRY_BOUNDS clamping's own dedicated test below for the case
/// where a hint *is* supplied.
/// </summary>
[Collection(ShortTimersConformanceCollection.Name)]
public sealed class RateLimitRecoveryTests(ShortTimersConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Enqueues a rate-limited failure with no parseable hint, so retry_delay() falls
    /// through to the ShortTimers-overridden default instead of clamping a hint.</summary>
    private static ResponseScript NoHintRateLimited() =>
        new([new DoneEvent(Status: "failed", ErrorCode: "rate_limit_exceeded", ErrorMessage: null)]);

    /// <summary>
    /// Takes an already-started <paramref name="connectionTask"/> rather than registering its own
    /// wait, so callers must call <c>fixture.Realtime.WaitForNextConnectionAsync(...)</c> *before*
    /// <c>RealtimeBrowserClient.ConnectAsync</c> -- matching the established, race-free ordering
    /// used everywhere else in this suite (e.g. SmokeSessionBootstrapTests). Registering the wait
    /// only after the browser is already connected (this file's own pattern until fixed here) is
    /// a genuine TOCTOU race: WaitForNextConnectionAsync deliberately only resolves a connection
    /// accepted *after* it's called, so if the backend's own eager upstream-connect (triggered by
    /// the browser's handshake) completes before this registers, it's missed entirely and this
    /// hangs for the full FrameTimeout. Rare under light load, confirmed to reproduce under the
    /// heavier concurrent load of a full-suite run.
    /// </summary>
    private static async Task<FakeRealtimeConnection> ConnectAndGetPastGreetingAsync(
        Task<FakeRealtimeConnection?> connectionTask, RealtimeBrowserClient browser, CancellationToken ct)
    {
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await browser.SendStartSessionAsync(cancellationToken: ct);

        // Same marker UpdateOrderToolCallTests uses: the greeting's round trip must fully finish
        // (tools_pending cleared, ladder idle) before scripting the scenario's own turn, or the
        // automatic greeting response.create would consume our script instead.
        var greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");
        return connection!;
    }

    // Ladder_runs_silent_then_two_notifications_then_gives_up and
    // A_service_retry_hint_is_parsed_and_clamped_to_the_production_bounds both moved to
    // RateLimitRetryTimingTests.cs on the RateLimitTimers profile (PR #54 review, blocker B1):
    // ShortTimers' 1-second idle_timeout left both of them with only a few hundred milliseconds
    // of margin against a genuine idle-close racing the behaviour actually under test -- the
    // same thin-margin pattern the #52 review flagged for ResumeMargin. Neither test is
    // exercising idle behaviour itself, so RateLimitTimers' 10s idle/grace budget (same
    // rate-limit-delay values, otherwise) removes the race without changing what's asserted.

    // A_pending_retry_is_cancelled_by_guest_speech lives in
    // RateLimitGuestSpeechCancellationTests.cs on the RateLimitTimers profile, not ShortTimers --
    // see that file's doc comment for why (echo-suppression's post-greeting cooldown needs
    // several real seconds of headroom that ShortTimers' 1s idle/nudge budget can't provide).

    [Fact]
    public Task A_pending_retry_is_cancelled_by_detach() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await ConnectAndGetPastGreetingAsync(connectionTask, browser, ct);

        // Same two-failure structure as A_pending_retry_is_cancelled_by_guest_speech above, and
        // for the same reason: attempt 0's own failure is silent and FakeRealtimeUpstreamServer
        // dispatches each received frame as its own fire-and-forget task, so detaching
        // immediately after the guest's own response.create would only race two independent
        // fake-side tasks against each other, not exercise RateLimitRecovery's cancellation.
        // Waiting for {attempt:1} is the first client-visible, race-free proof that attempt 2 is
        // genuinely "pending" before the detach happens.
        connection.Script.Enqueue(NoHintRateLimited());
        connection.Script.Enqueue(NoHintRateLimited());
        await browser.SendResponseCreateAsync(ct);

        var attempt1Notification = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.rate_limited" && f.Json.GetProperty("attempt").GetInt32() == 1,
            FrameTimeout, ct);
        Assert.True(attempt1Notification is not null,
            "Expected extension.rate_limited{attempt:1} after the first retry also failed.");
        var boundary = connection.ReceivedFrames.Snapshot().Last(f => f.Type == "response.create").Sequence;

        // Drop while attempt 2 is still pending. rtmt.py's connection handler calls
        // recovery.cancel("socket closed") in its outer `finally`, unconditionally, for any
        // teardown path -- so this proves a detach cancels a pending retry exactly like guest
        // speech does, via a completely different code path.
        await browser.CloseAsync(cancellationToken: ct);
        await browser.WaitForCloseAsync(FrameTimeout, ct);
        await browser.DisposeAsync();

        var thirdResponseCreate = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence > boundary && f.Type == "response.create", TimeSpan.FromSeconds(1.5), ct);
        Assert.True(thirdResponseCreate is null,
            "Expected no retried response.create -- detaching should have cancelled the pending retry.");
    });

    [Fact]
    public Task A_retry_is_not_guest_activity_the_idle_clock_still_closes_the_socket_on_schedule() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await ConnectAndGetPastGreetingAsync(connectionTask, browser, ct);

        // First response.create fails and is silently retried once; the retry itself succeeds
        // (nothing queued for it, so RealtimeScript's AutoRespond default answers it), ending the
        // ladder quietly. Only one scripted failure is needed here -- the point of this test is
        // the idle clock, not the ladder depth.
        connection.Script.Enqueue(NoHintRateLimited());

        // This response.create is the *last real client activity* the idle clock will ever see --
        // sending it touches last_activity (rtmt.py: response.create is not an audio-append
        // marker, so from_client_to_server's touch_activity(session_id) call fires for it just
        // like any other non-audio client frame).
        await browser.SendResponseCreateAsync(ct);

        // If the ladder's own retry (sent directly to the upstream socket, entirely bypassing
        // from_client_to_server) counted as guest activity, the idle timer would restart around
        // t=0.2s and this close would arrive correspondingly late. ShortTimers' idle_timeout=1s
        // plus up to one 0.2s sweep pass, from the response.create above -- not from the 0.2s
        // retry -- is the bound actually being asserted here.
        await browser.WaitForCloseAsync(TimeSpan.FromSeconds(6), ct);
        Assert.Equal((System.Net.WebSockets.WebSocketCloseStatus)4000, browser.CloseStatus);
        Assert.Equal("idle_timeout", browser.CloseStatusDescription);
    });

    [Fact]
    public Task A_retried_tool_follow_up_never_re_runs_the_tool() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await ConnectAndGetPastGreetingAsync(connectionTask, browser, ct);

        const string callId = "call_rate_limited_follow_up_1";
        connection.Script.Enqueue(new ResponseScript([
            new FunctionCallEvent(
                Name: "update_order",
                ArgumentsJson: """{"action":"add","item_name":"Small Fries","size":"Small","quantity":1,"price":2.49}""",
                CallId: callId),
            new DoneEvent(),
        ]));
        // rtmt.py auto-sends a bare follow-up response.create as soon as the tool's
        // function_call_output round-trips (response.done handling: "if tools_pending:
        // tools_pending.clear(); await server_ws.send_str(_RESPONSE_CREATE_MSG)") -- this second
        // queued script answers *that* follow-up, not a new browser-initiated turn.
        connection.Script.Enqueue(NoHintRateLimited());
        // The ladder's own retry of the follow-up (a third response.create, sent directly
        // upstream by RateLimitRecovery, not by the tool-follow-up path) gets a normal reply so
        // the ladder ends quietly.

        var turnStart = connection.ReceivedFrames.Count;
        await browser.SendResponseCreateAsync(ct);

        var functionCallOutput = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence >= turnStart &&
                 f.Type == "conversation.item.create" &&
                 f.Json.TryGetProperty("item", out var item) &&
                 item.TryGetProperty("type", out var itemType) &&
                 itemType.GetString() == "function_call_output" &&
                 item.TryGetProperty("call_id", out var respondedCallId) &&
                 respondedCallId.GetString() == callId,
            FrameTimeout, ct);
        Assert.True(functionCallOutput is not null,
            $"Expected exactly one conversation.item.create(function_call_output) for call_id={callId}.");

        // The follow-up's own rate-limited failure notifies the browser (attempt:1's bounds
        // don't apply here -- this is attempt 0, so it must be silent) -- then its retry (a bare
        // response.create with no new tool call) must complete without ever producing a *second*
        // function_call_output for the same call_id.
        var laterFunctionCallOutput = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence > functionCallOutput!.Sequence &&
                 f.Type == "conversation.item.create" &&
                 f.Json.TryGetProperty("item", out var item) &&
                 item.TryGetProperty("type", out var itemType) &&
                 itemType.GetString() == "function_call_output" &&
                 item.TryGetProperty("call_id", out var respondedCallId) &&
                 respondedCallId.GetString() == callId,
            TimeSpan.FromSeconds(1.5), ct);
        Assert.True(laterFunctionCallOutput is null,
            "The tool must not be re-run -- no second function_call_output for the same call_id.");

        var responseCreatesAfterTheTool = connection.ReceivedFrames.Snapshot()
            .Skip(functionCallOutput!.Sequence + 1)
            .Count(f => f.Type == "response.create");
        // Exactly two: rtmt.py's own auto-sent tool follow-up, then the ladder's one retry of it.
        Assert.Equal(2, responseCreatesAfterTheTool);
    });
}
