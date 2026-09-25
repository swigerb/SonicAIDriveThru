using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.RateLimit;

/// <summary>
/// Issue #10: <c>A_retry_is_not_guest_activity_the_idle_clock_still_closes_the_socket_on_schedule</c>,
/// moved here from RateLimitRecoveryTests.cs (PR #54 review follow-up, Rick, post-merge) onto its
/// own dedicated <see cref="BackendProfiles.RateLimitIdleInteractionTimers"/> profile/collection.
/// That test measures the *gap* between two competing wall-clock predictions (see the profile's
/// own doc comment for the exact numbers) rather than a simple "did it close in time" bound, so
/// unlike this suite's other timer-driven scenarios, the margin between "latest realistic
/// correct-code close" and "earliest possible mutant close" scales directly with how generous the
/// idle_timeout/retry-delay values are -- ShortTimers' 1s idle_timeout against 0.2s/0.4s retry
/// delays left only ~190ms of headroom, tight enough to flake under this suite's own heavy
/// concurrent load. A first widening to 2s idle_timeout / 0.3s+1.2s retry delays (~0.6s margin)
/// still flaked once in CI (run 36176347267) -- not via the retry-vs-activity race this margin
/// protects, but via the idle clock tripping during the un-timed connect-and-greet phase in
/// <see cref="ConnectAndGetPastGreetingAsync"/> under real host contention (see the profile's own
/// doc comment for the full analysis and why idle_timeout=2s wasn't enough headroom for that
/// phase). This profile's current values (6s idle_timeout, 0.9s/3.6s retry delays) leave ~2.1s of
/// margin on either side of the race *and* 6s of idle-clock headroom for connect-and-greet, up
/// from 2s.
/// </summary>
[Collection(RateLimitIdleInteractionTimersConformanceCollection.Name)]
public sealed class RateLimitIdleInteractionTests(RateLimitIdleInteractionTimersConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Enqueues a rate-limited failure with no parseable hint, so retry_delay() falls
    /// through to the profile-overridden default instead of clamping a hint.</summary>
    private static ResponseScript NoHintRateLimited() =>
        new([new DoneEvent(Status: "failed", ErrorCode: "rate_limit_exceeded", ErrorMessage: null)]);

    /// <summary>
    /// Takes an already-started <paramref name="connectionTask"/> rather than registering its own
    /// wait -- see RateLimitRecoveryTests.ConnectAndGetPastGreetingAsync's doc comment for the
    /// TOCTOU race this avoids.
    /// </summary>
    private static async Task<FakeRealtimeConnection> ConnectAndGetPastGreetingAsync(
        Task<FakeRealtimeConnection?> connectionTask, RealtimeBrowserClient browser, CancellationToken ct)
    {
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await browser.SendStartSessionAsync(cancellationToken: ct);

        var greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");
        return connection!;
    }

    [Fact]
    public Task A_retry_is_not_guest_activity_the_idle_clock_still_closes_the_socket_on_schedule() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await ConnectAndGetPastGreetingAsync(connectionTask, browser, ct);

        // Two scripted failures push both of rate_limit.py's retries out
        // (CONFORMANCE_RATE_LIMIT_RETRY_DELAY_SECONDS=0.9, then
        // CONFORMANCE_RATE_LIMIT_SECOND_RETRY_DELAY_SECONDS=3.6 after that, i.e. ~4.5s total),
        // giving a wide, robust gap between the two hypotheses: if retries are correctly NOT
        // guest activity, idle_timeout=6s from the response.create below plus at most one 0.2s
        // sweep pass closes by ~6.0-6.2s; if a retry wrongly touched the idle clock (rtmt.py's
        // RateLimitRecovery is constructed with target_ws.send_str directly as _send_upstream,
        // bypassing from_client_to_server's touch_activity entirely -- see
        // RateLimitRecoveryTests' class doc comment), last activity would reset to ~4.5s and the
        // close would instead land no earlier than ~10.5s. A stopwatch started at the same
        // response.create send and stopped when the close is observed measures which actually
        // happened.
        connection.Script.Enqueue(NoHintRateLimited());
        connection.Script.Enqueue(NoHintRateLimited());

        // This response.create is the *last real client activity* the idle clock will ever see --
        // sending it touches last_activity (rtmt.py: response.create is not an audio-append
        // marker, so from_client_to_server's touch_activity(session_id) call fires for it just
        // like any other non-audio client frame).
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await browser.SendResponseCreateAsync(ct);

        // Confirm both retries actually ran (attempt:1 notification only fires on the *second*
        // failure) before measuring the close -- otherwise a harness regression that silently
        // drops a scripted failure could make this pass for the wrong reason (fewer retries than
        // intended, less activity-touching opportunity for the mutation to expose).
        var attempt1Notification = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.rate_limited" && f.Json.GetProperty("attempt").GetInt32() == 1,
            FrameTimeout, ct);
        Assert.True(attempt1Notification is not null,
            "Expected extension.rate_limited{attempt:1} after the second scripted failure.");

        // Wide enough to observe even the mutant's ~10.5s close (see above) so a mutation run
        // fails on the ceiling assertion below for the intended reason, not a timeout here first.
        await browser.WaitForCloseAsync(TimeSpan.FromSeconds(15), ct);
        stopwatch.Stop();
        Assert.Equal((System.Net.WebSockets.WebSocketCloseStatus)4000, browser.CloseStatus);
        Assert.Equal("idle_timeout", browser.CloseStatusDescription);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8.3),
            $"Idle close took {stopwatch.Elapsed.TotalSeconds:F2}s after the response.create -- expected " +
            "~6.0-6.2s (idle_timeout plus at most one sweep pass). Anything approaching ~10.5s+ would mean " +
            "a retry reset the idle clock instead of being correctly ignored by it.");
    });
}
