using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.RateLimit;

/// <summary>
/// Issue #10: rate-limit ladder/retry-timing scenarios that were originally in
/// RateLimitRecoveryTests.cs on the ShortTimers profile, moved here (PR #54 review, blocker B1)
/// after CI hit a genuine load-dependent flake on ShortTimers' 1-second idle_timeout.
///
/// Neither scenario tests idle behaviour itself (that's IdleTimeoutTests' and
/// A_retry_is_not_guest_activity_the_idle_clock_still_closes_the_socket_on_schedule's job), but
/// both need several hundred milliseconds of wall-clock headroom for the ladder's own delays
/// (0.2s/0.4s retry delays, or the 0.5s FIRST_RETRY_BOUNDS floor) to play out before their final
/// assertion -- margin ShortTimers' 1s idle/grace budget can't reliably provide under this
/// suite's own heavy concurrent load. This is the same thin-margin pattern the #52 review
/// flagged for ResumeMargin: <see cref="BackendProfiles.RateLimitTimers"/> keeps the same
/// rate-limit-delay values ShortTimers uses (so nothing about what's asserted changes) but widens
/// idle/grace/nudge to 10s, removing the coincidental race with a genuine idle-close.
/// </summary>
[Collection(RateLimitTimersConformanceCollection.Name)]
public sealed class RateLimitRetryTimingTests(RateLimitTimersConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Enqueues a rate-limited failure with no parseable hint, so retry_delay() falls
    /// through to the profile-overridden default instead of clamping a hint.</summary>
    private static ResponseScript NoHintRateLimited() =>
        new([new DoneEvent(Status: "failed", ErrorCode: "rate_limit_exceeded", ErrorMessage: null)]);

    /// <summary>
    /// Takes an already-started <paramref name="connectionTask"/> rather than registering its own
    /// wait, so callers must call <c>fixture.Realtime.WaitForNextConnectionAsync(...)</c> *before*
    /// <c>RealtimeBrowserClient.ConnectAsync</c> -- matching the established, race-free ordering
    /// used everywhere else in this suite (e.g. SmokeSessionBootstrapTests). Registering the wait
    /// only after the browser is already connected is a genuine TOCTOU race:
    /// WaitForNextConnectionAsync deliberately only resolves a connection accepted *after* it's
    /// called, so if the backend's own eager upstream-connect (triggered by the browser's
    /// handshake) completes before this registers, it's missed entirely and this hangs for the
    /// full FrameTimeout -- confirmed to reproduce under a full-suite run's heavier concurrent load.
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

    [Fact]
    public Task Ladder_runs_silent_then_two_notifications_then_gives_up() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await ConnectAndGetPastGreetingAsync(connectionTask, browser, ct);

        // Three response.create frames: the guest's turn, then the first retry, then the second
        // (and final) retry. All three fail, driving the ladder all the way to exhaustion.
        connection.Script.Enqueue(NoHintRateLimited());
        connection.Script.Enqueue(NoHintRateLimited());
        connection.Script.Enqueue(NoHintRateLimited());

        var turnStart = connection.ReceivedFrames.Count;
        await browser.SendResponseCreateAsync(ct);

        // Attempt 0's failure is silent -- no extension.rate_limited at all -- so the *first*
        // thing the browser should observe is directly the {attempt:1} notification, not a
        // {attempt:0} one that would prove attempt 0 wasn't actually silent.
        var first = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.rate_limited", FrameTimeout, ct);
        Assert.True(first is not null, "Expected extension.rate_limited after the first retry also failed.");
        Assert.Equal(1, first!.Json.GetProperty("attempt").GetInt32());
        Assert.False(first.Json.TryGetProperty("final", out _), "attempt:1 must not carry final:true.");

        var second = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.rate_limited" && f.Sequence > first.Sequence, FrameTimeout, ct);
        Assert.True(second is not null, "Expected a second extension.rate_limited after the final retry failed.");
        Assert.Equal(2, second!.Json.GetProperty("attempt").GetInt32());
        Assert.True(second.Json.TryGetProperty("final", out var finalProp) && finalProp.GetBoolean(),
            "attempt:2 must carry final:true.");

        // No third notification, no fourth response.create -- the ladder must stay exhausted
        // until the guest's next turn. The second retry delay is 0.4s; 2s is generous headroom
        // without ever approaching this actually taking long.
        var extraNotification = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.rate_limited" && f.Sequence > second.Sequence,
            TimeSpan.FromSeconds(2), ct);
        Assert.True(extraNotification is null, "Ladder must not notify again after attempt:2/final:true.");

        var responseCreatesSeenByFake = connection.ReceivedFrames.Snapshot()
            .Skip(turnStart)
            .Count(f => f.Type == "response.create");
        Assert.Equal(3, responseCreatesSeenByFake);

        // Clean up: let the guest "speak again" so the session isn't left in a rate-limited
        // state when the scenario ends (RunAsync's own teardown only cares about connections,
        // not this, but it keeps the shared backend's state tidy for later tests in this
        // collection).
        await browser.SendInputAudioAppendAsync("dGVzdA==", ct);
    });

    [Fact]
    public Task A_service_retry_hint_is_parsed_and_clamped_to_the_production_bounds() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await ConnectAndGetPastGreetingAsync(connectionTask, browser, ct);

        // rate_limit.py's FIRST_RETRY_BOUNDS=(0.5, 5.0) are hardcoded and never overridable via
        // CONFORMANCE_TEST_HOOKS. This deliberately proves the *floor* clamp (a hint asking for
        // far less than 0.5s must still wait out the floor), not the ceiling -- a ~5s
        // ceiling-clamped retry would need a correspondingly large idle budget, and only the
        // floor case is in scope here. A hint of "10ms" clamps up to the 0.5s floor, while still
        // proving both that the hint was recognised (0.5s is nowhere near the raw, unclamped
        // 0.01s) *and* that it wasn't simply ignored in favour of the unhinted 0.2s default (0.5s
        // is well past 0.2s too).
        connection.Script.Enqueue(new ResponseScript([
            new DoneEvent(Status: "failed", ErrorCode: "rate_limit_exceeded",
                ErrorMessage: "Rate limit reached. Please try again in 10ms."),
        ]));

        var turnStart = connection.ReceivedFrames.Count;
        await browser.SendResponseCreateAsync(ct);

        // PR #54 review (blocker B1): the previous version anchored on "the first
        // response.create with Sequence > turnStart", but turnStart was captured as a *count*
        // (0-based next-index), so the guest's own just-sent response.create itself already
        // satisfies "Sequence > turnStart" -- under load, that frame can be recorded before this
        // wait even starts, so it (not the retry) was sometimes the one observed, 0.27ms after
        // sendTime rather than ~0.5s later. Anchor explicitly instead: `turn` is the first
        // response.create with Sequence >= turnStart (the guest's own frame, unambiguously), and
        // `retry` is the *next* one after it -- genuinely the ladder's retry, never the turn
        // itself, regardless of exactly when either gets recorded relative to this wait starting.
        var turn = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence >= turnStart && f.Type == "response.create", FrameTimeout, ct);
        Assert.True(turn is not null, "Expected the guest's own response.create to reach the fake.");

        // A dedicated "must not arrive within 0.3s" WaitForAsync (racing its own ~300ms
        // Task.Delay against the retry's arrival signal) was tried here first but proved
        // non-deterministic under full-suite load: FrameLog.WaitForAsync's timeout is itself a
        // Task.Delay on the shared thread pool, and under heavy CPU contention its callback can
        // be scheduled late enough that the genuine ~0.5s-floor retry -- which sets the same
        // wait's completion signal the moment it's recorded -- wins the race, producing a
        // spurious non-null "too early" result despite the retry having, in truth, arrived on
        // time. Asserting on the retry frame's own recorded ReceivedAt timestamp (relative to the
        // turn's, both taken from the fake's clock) proves the identical property (clamped to
        // the floor, not the raw hint or the unhinted default) without racing an independent
        // client-side clock against either frame.
        //
        // Wait with the full FrameTimeout (not a tight ~0.6s window) -- a slow runner delaying
        // when the retry arrives doesn't falsify "it was clamped to roughly the floor", only the
        // elapsed-time assertion below does that. RateLimitTimers' 10s idle/grace budget easily
        // outlasts this wait; a retry never touches the idle clock either way.
        var retry = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence > turn!.Sequence && f.Type == "response.create", FrameTimeout, ct);
        Assert.True(retry is not null,
            "Expected the retry at roughly the 0.5s FIRST_RETRY_BOUNDS floor, clamped up from the " +
            "10ms hint.");
        var elapsed = retry!.ReceivedAt - turn!.ReceivedAt;
        // Lower bound proves the hint was recognised and clamped up to the 0.5s floor: it rules
        // out both the raw, unclamped 0.01s hint and the unhinted 0.2s default (0.35s is
        // comfortably past both). Upper bound is loose (well under the 5s ceiling) -- it only
        // needs to rule out the ceiling-clamp path, not pin down exact scheduling latency.
        Assert.True(elapsed >= TimeSpan.FromSeconds(0.35) && elapsed < TimeSpan.FromSeconds(3),
            $"Expected the retry roughly 0.5s after the turn (clamped up to the floor), observed {elapsed}.");
    });
}
