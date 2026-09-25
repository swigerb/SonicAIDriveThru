using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.RateLimit;

/// <summary>
/// swigerb/SonicAIDriveThru#48 (PR #58 re-review, "M1"): a rate-limited GREETING's own
/// response.done (audio_pipeline.EchoSuppressor.on_response_done, #48's own original fix)
/// correctly unmutes the mic instantly, since nothing was ever rendered -- but
/// rate_limit.py's RateLimitRecovery ladder may then retry that same greeting with a bare
/// response.create. Before this fix, nothing re-armed greeting suppression for the retry:
/// its speech_started was no longer ignored (a false barge-in -- a regression from dev,
/// where the flag stayed latched for the whole greeting) and its own on_audio_done() applied
/// only the normal 1x cooldown instead of the doubled post-greeting one.
///
/// Uses <see cref="RateLimitTimersConformanceFixture"/>, not the default collection, for the
/// same reason as <see cref="RateLimitGuestSpeechCancellationTests"/> (see its own doc
/// comment): this scenario needs a real, several-second wall-clock wait for the doubled echo
/// cooldown, which ShortTimers' 1-second idle/nudge budget cannot survive, and there is no
/// CONFORMANCE_* hook for audio.echo_cooldown_seconds itself.
/// </summary>
[Collection(RateLimitTimersConformanceCollection.Name)]
public sealed class GreetingRateLimitRetryEchoSuppressionTests(RateLimitTimersConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);
    private const string MicRightAfterVadProbe = "bWljLXJpZ2h0LWFmdGVyLXZhZC1wcm9iZQ==";
    private const string MicAt1_5xCooldown = "bWljLWF0LTEuNXgtY29vbGRvd24=";
    private const string MicAfterFullCooldown = "bWljLWFmdGVyLWZ1bGwtY29vbGRvd24=";

    /// <summary>Enqueues a rate-limited failure with no parseable hint, so retry_delay() falls
    /// through to the profile-overridden default instead of clamping a hint.</summary>
    private static ResponseScript NoHintRateLimited() =>
        new([new DoneEvent(Status: "failed", ErrorCode: "rate_limit_exceeded", ErrorMessage: null)]);

    [Fact]
    public Task Speech_started_during_the_greetings_retried_audio_is_ignored_and_gets_the_doubled_cooldown() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        // The greeting's very first attempt is rate-limited with no audio at all --
        // echo.on_response_done()'s "nothing rendered" branch fires, unmuting instantly and (M1)
        // marking the pending retry so the next audio delta re-enters greeting_in_progress. The
        // ladder's own retry (a bare response.create sent directly upstream by RateLimitRecovery,
        // never seen by the browser) gets a real, audio-bearing response this time, paced so
        // there's a comfortable window to probe it before it completes.
        connection!.Script.Enqueue(NoHintRateLimited());
        connection.Script.Enqueue(new ResponseScript([
            new AudioDeltaEvent("cmV0cmllZC1ncmVldGluZy1hdWRpbw=="),
            new DoneEvent(Pace: TimeSpan.FromSeconds(1)),
        ]));

        await browser.SendStartSessionAsync(cancellationToken: ct);

        var retriedDelta = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "response.audio.delta", FrameTimeout, ct);
        Assert.True(retriedDelta is not null, "Expected the greeting's ladder retry to produce audio.");

        // Simulate the model's own server-side VAD reporting speech during the retried greeting's
        // audio -- directly, bypassing the client->server echo-suppression drop entirely (a real
        // mic append here would never even reach upstream while ai_speaking is latched). This
        // mirrors Rick's own unit-level repro exactly: on_audio_delta() then on_speech_started(),
        // with no forwarded append in between at all.
        await connection.SendAsync(new
        {
            type = "input_audio_buffer.speech_started",
            event_id = "evt_retry_vad_probe",
            audio_start_ms = 0,
            item_id = "item_retry_vad_probe",
        }, ct);
        var speechStartedAtBrowser = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "input_audio_buffer.speech_started", FrameTimeout, ct);
        Assert.True(speechStartedAtBrowser is not null,
            "Expected the synthetic speech_started to be forwarded to the browser (a passthrough " +
            "type) -- sanity that it was actually processed by rtmt.py's echo.on_speech_started().");

        // M1: if greeting_in_progress was correctly re-armed by the retry's own audio delta, this
        // speech_started must be IGNORED (ai_speaking stays True) -- so a mic append sent right
        // after must still be suppressed and never reach the fake. Before the fix,
        // greeting_in_progress was already False by the first (failed) response.done, so
        // on_speech_started() would reset ai_speaking/cooldown_end to 0 here, and this append would
        // wrongly be forwarded immediately.
        await browser.SendInputAudioAppendAsync(MicRightAfterVadProbe, ct);
        var wronglyForwarded = await connection.ReceivedFrames.WaitForAsync(
            f => f.Type == "input_audio_buffer.append" && f.Json.GetProperty("audio").GetString() == MicRightAfterVadProbe,
            TimeSpan.FromSeconds(1), ct);
        Assert.True(wronglyForwarded is null,
            "Expected mic audio right after speech_started during the retried greeting's audio to " +
            "still be suppressed -- speech_started must be ignored as greeting echo (#48 M1), not " +
            "treated as a real barge-in.");

        // The retried response then completes normally (its own DoneEvent, paced above) --
        // echo.on_audio_done() fires. Correctly re-armed greeting_in_progress means this is a
        // genuine greeting completion and gets the doubled cooldown; the bug would give only the
        // normal 1x one.
        var greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");

        // 1.5x the *normal* (undoubled) 1.5s cooldown: past a buggy 1x cooldown, comfortably short
        // of the correct, doubled 3.0s one. No CONFORMANCE_* hook exists for
        // audio.echo_cooldown_seconds (see RateLimitGuestSpeechCancellationTests' doc comment), so
        // this is a real wall-clock wait, not a shortened one.
        await Task.Delay(TimeSpan.FromSeconds(2.25), ct);
        await browser.SendInputAudioAppendAsync(MicAt1_5xCooldown, ct);
        var tooEarly = await connection.ReceivedFrames.WaitForAsync(
            f => f.Type == "input_audio_buffer.append" && f.Json.GetProperty("audio").GetString() == MicAt1_5xCooldown,
            TimeSpan.FromSeconds(1), ct);
        Assert.True(tooEarly is null,
            "Expected mic audio at 1.5x the normal cooldown to still be suppressed -- the greeting's " +
            "retried audio must get the doubled post-greeting cooldown (#48 M1), not a normal one.");

        // Past the full doubled cooldown (3.0s from the retry's own audio.done), suppression must
        // finally lift -- confirms this is a bounded cooldown, not a permanently latched mute.
        RecordedFrame? forwarded = null;
        var deadline = DateTime.UtcNow + FrameTimeout;
        while (forwarded is null && DateTime.UtcNow < deadline)
        {
            await browser.SendInputAudioAppendAsync(MicAfterFullCooldown, ct);
            forwarded = await connection.ReceivedFrames.WaitForAsync(
                f => f.Type == "input_audio_buffer.append" && f.Json.GetProperty("audio").GetString() == MicAfterFullCooldown,
                TimeSpan.FromMilliseconds(300), ct);
        }
        Assert.True(forwarded is not null,
            "Expected mic audio to eventually be forwarded once the doubled cooldown fully elapses.");
    });
}
