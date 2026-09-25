using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.RateLimit;

/// <summary>
/// Issue #10: a pending rate-limit retry is cancelled the instant genuine guest speech reaches
/// the backend (app/backend/rate_limit.py's RateLimitRecovery.on_guest_speech(), invoked from
/// rtmt.py's from_server_to_client handling of the fake's own speech_started reply).
///
/// This scenario needs its own dedicated <see cref="RateLimitTimersConformanceFixture"/> instead
/// of sharing <see cref="RateLimitRecoveryTests"/>'s ShortTimers collection, for a reason that has
/// nothing to do with rate-limit timing at all: app/backend/audio_pipeline.py's EchoSuppressor.
/// Every fresh (non-resumed) connection's greeting is answered by RealtimeBrowserClient's
/// AutoRespond default script with a real audio delta, so echo.on_audio_done() always fires and
/// starts a `greeting_in_progress`-doubled cooldown (config.yaml's audio.echo_cooldown_seconds is
/// 1.5, doubled to 3.0s post-greeting) during which rtmt.py's from_client_to_server loop silently
/// `continue`s past *every* input_audio_buffer.append -- it never reaches the fake upstream, so
/// the fake's WithVadDefaults() rule never replies with speech_started, so on_guest_speech() is
/// never invoked at all. This is a genuine, deliberate anti-echo production safety feature (real
/// callers' own mic audio picking up the assistant's TTS would otherwise falsely look like guest
/// speech), not a test bug or a rate-limit-specific race -- but it means this scenario's synthetic
/// guest-speech append must be sent *after* that fixed 3.0s cooldown has elapsed, which ShortTimers'
/// 1-second idle timeout and 1-second nudge budget cannot survive (the idle sweep would close the
/// socket, or the nudge would inject an unrelated response.create, before the wait finished).
/// There is no CONFORMANCE_* hook for echo_cooldown_seconds itself (unlike the timers this profile
/// does override), so the wait below is real wall-clock time, not a shortened one.
/// </summary>
[Collection(RateLimitTimersConformanceCollection.Name)]
public sealed class RateLimitGuestSpeechCancellationTests(RateLimitTimersConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Comfortably longer than the fixed 3.0s post-greeting echo-suppression cooldown
    /// (audio.echo_cooldown_seconds=1.5, doubled because the greeting itself is in progress) --
    /// see this class's doc comment. Sending guest speech any earlier is silently dropped by
    /// rtmt.py's echo-suppression `continue` before it ever reaches the fake upstream.</summary>
    private static readonly TimeSpan EchoCooldownWait = TimeSpan.FromSeconds(3.3);

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
        // automatic greeting response.create would consume our script instead. It also means the
        // greeting's own audio.done has already fired by the time this returns, so the echo
        // cooldown wait below starts from a stable, already-elapsed baseline.
        var greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");
        return connection!;
    }

    [Fact]
    public Task A_pending_retry_is_cancelled_by_guest_speech() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await ConnectAndGetPastGreetingAsync(connectionTask, browser, ct);

        // Wait out the greeting's own echo-suppression cooldown (see class doc comment) *before*
        // starting the rate-limit ladder at all, so the guest-speech append below -- sent shortly
        // after the ladder's own {attempt:1} notification -- lands comfortably past the cooldown
        // window rather than racing it.
        await Task.Delay(EchoCooldownWait, ct);

        // Two scripted failures: attempt 0 (silent) and retry 1 (notifies {attempt:1}) -- the
        // ladder's own second retry (attempt 2) is by then scheduled ("pending"). That
        // notification is the first client-visible, race-free signal that a retry is genuinely
        // pending: attempt 0's own failure is silent by design (no client-visible signal at
        // all), and FakeRealtimeUpstreamServer dispatches each received frame as an independent
        // fire-and-forget task (see FakeRealtimeUpstreamServer.HandleFrameAsync's caller,
        // `TrackHandler(...)`, not an inline await) -- racing guest speech against attempt 0's
        // own still-in-flight response would only prove which of two concurrent fake-side tasks
        // happened to finish first, not anything about RateLimitRecovery's own cancellation.
        connection.Script.Enqueue(NoHintRateLimited());
        connection.Script.Enqueue(NoHintRateLimited());
        await browser.SendResponseCreateAsync(ct);

        var attempt1Notification = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.rate_limited" && f.Json.GetProperty("attempt").GetInt32() == 1,
            FrameTimeout, ct);
        Assert.True(attempt1Notification is not null,
            "Expected extension.rate_limited{attempt:1} after the first retry also failed.");

        // The boundary for "no further retry reaches the fake": retry 1's own response.create,
        // the last one the fake has seen so far. Attempt 2 is pending now -- guest speech must
        // cancel it before it ever reaches the fake.
        var boundary = connection.ReceivedFrames.Snapshot().Last(f => f.Type == "response.create").Sequence;

        // rate_limit.py's _on_failure sends the {attempt:1} notification (awaited) and only
        // *then*, with no further await in between, assigns self._pending for attempt 2 -- so
        // self.pending is False for the notification's own send duration. Reacting to the
        // notification the instant it's observed (0 delay) reliably lands inside that window,
        // where on_guest_speech() sees nothing pending to cancel and attempt 2 gets scheduled
        // anyway right after. A short buffer here gives the notification's own send time to fully
        // unwind before the guest "speaks", well short of the 0.4s second retry delay this is
        // racing against, and long past the echo cooldown already waited out above.
        await Task.Delay(TimeSpan.FromMilliseconds(200), ct);

        // input_audio_buffer.append is forwarded upstream (echo suppression's cooldown has long
        // since elapsed by now), where WithVadDefaults' rule replies with speech_started --
        // rtmt.py's handling of that reply is what actually calls recovery.on_guest_speech() (see
        // rtmt.py: "elif _MARKER_SPEECH_STARTED in data: ... recovery.on_guest_speech()").
        await browser.SendInputAudioAppendAsync("dGVzdA==", ct);

        // Bounded comfortably longer than the 0.4s second-retry delay that would have applied
        // had attempt 2 not been cancelled -- if this ever finds a third response.create, the
        // cancellation didn't actually happen.
        var thirdResponseCreate = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence > boundary && f.Type == "response.create", TimeSpan.FromSeconds(1.5), ct);
        Assert.True(thirdResponseCreate is null,
            "Expected no retried response.create -- guest speech should have cancelled the pending retry.");

        var furtherNotification = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.rate_limited" && f.Sequence > attempt1Notification!.Sequence,
            TimeSpan.FromSeconds(1), ct);
        Assert.True(furtherNotification is null, "A cancelled retry must never notify the browser.");
    });
}
