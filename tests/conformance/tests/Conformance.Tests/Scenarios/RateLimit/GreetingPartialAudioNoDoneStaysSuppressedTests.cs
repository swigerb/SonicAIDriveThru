using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.RateLimit;

/// <summary>
/// swigerb/SonicAIDriveThru#48 S1 (PR #58 re-review, "F1"): a greeting whose audio *started*
/// streaming (at least one delta arrived) but never got a completing `response.output_audio.done`
/// -- cancelled/errored mid-stream, with no rate-limit ladder retry involved at all -- must still
/// receive the doubled post-greeting echo cooldown
/// (<c>audio_pipeline.EchoSuppressor.on_response_done</c>'s <c>_greeting_audio_seen</c> branch),
/// not <see cref="GreetingWithoutAudioUnmutesTests"/>'s no-audio-at-all case's instant unmute.
/// Before the S1 fix, `ai_speaking` alone couldn't distinguish "nothing rendered" from "some audio
/// rendered but never completed" -- both reach `on_response_done()` with `ai_speaking` still True
/// from `start_greeting_suppression()`/`on_audio_delta()`'s pre-set -- so this partial-audio case
/// was wrongly given the instant, no-cooldown unmute too, even though real audio already reached
/// the guest and carries the same residual echo risk a normal `on_audio_done()` completion does.
///
/// The fake's <see cref="DoneEvent.SuppressAudioDone"/> (added for this scenario) lets a scripted
/// response stream <see cref="AudioDeltaEvent"/>s and then complete without ever sending
/// `response.output_audio.done` -- exactly this shape. Uses
/// <see cref="RateLimitTimersConformanceFixture"/>, not the default collection, for the same
/// reason as <see cref="GreetingRateLimitRetryEchoSuppressionTests"/> (see its own doc comment):
/// this needs a real, several-second wall-clock wait for the doubled echo cooldown -- during which
/// a mic `input_audio_buffer.append` does not count as guest activity (rtmt.py's idle clock) --
/// which ShortTimers' 1-second idle budget cannot survive, and there is no CONFORMANCE_* hook for
/// audio.echo_cooldown_seconds itself.
/// </summary>
[Collection(RateLimitTimersConformanceCollection.Name)]
public sealed class GreetingPartialAudioNoDoneStaysSuppressedTests(RateLimitTimersConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);
    private const string GreetingDelta = "cGFydGlhbC1ncmVldGluZy1hdWRpbw==";
    private const string MicAt1_5xCooldown = "bWljLWF0LTEuNXgtY29vbGRvd24=";
    private const string MicAfterFullCooldown = "bWljLWFmdGVyLWZ1bGwtY29vbGRvd24=";

    [Fact]
    public Task Mic_audio_at_1_5x_cooldown_stays_suppressed_after_partial_greeting_audio_with_no_audio_done() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        // The greeting streams one real audio delta, then completes ("cancelled", mid-stream)
        // with SuppressAudioDone: true -- audio started but response.output_audio.done never
        // arrives. No rate-limit error at all here (a plain "cancelled" status), so
        // RateLimitRecovery.on_response_done() never engages -- this isolates the S1 case from
        // GreetingRateLimitRetryEchoSuppressionTests' M1 (a rate-limited retry) entirely.
        connection!.Script.Enqueue(new ResponseScript([
            new AudioDeltaEvent(GreetingDelta),
            new DoneEvent(Status: "cancelled", SuppressAudioDone: true),
        ]));

        await browser.SendStartSessionAsync(cancellationToken: ct);

        // Confirm the delta actually reached the browser (echo.on_audio_delta() ran, setting
        // _greeting_audio_seen) before relying on the doubled cooldown it enables.
        var deltaForwarded = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "response.audio.delta", FrameTimeout, ct);
        Assert.True(deltaForwarded is not null, "Greeting audio delta was never forwarded to the browser.");

        // extension.round_trip_token is only emitted from response.done's fully-parsed handling
        // in rtmt.py's from_server_to_client, which runs strictly after echo.on_response_done()
        // for the same server message -- seeing it guarantees on_response_done() has already
        // applied the doubled greeting cooldown (cooldown_end = now + 3.0s), even though no
        // audio.done ever arrived for this response.
        var greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > deltaForwarded!.Sequence && f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");

        // No response.cancel anywhere in this test -- unlike EchoSuppressionBargeInTests, this
        // proves the doubled *cooldown* applying to partial-audio-no-done, not a browser-triggered
        // barge-in.
        //
        // 1.5x the *normal* (undoubled) 1.5s cooldown: past a buggy instant-unmute (which would
        // have let this through immediately), comfortably short of the correct, doubled 3.0s one.
        // No CONFORMANCE_* hook exists for audio.echo_cooldown_seconds (see
        // RateLimitGuestSpeechCancellationTests' doc comment), so this is a real wall-clock wait.
        await Task.Delay(TimeSpan.FromSeconds(2.25), ct);
        await browser.SendInputAudioAppendAsync(MicAt1_5xCooldown, ct);
        var tooEarly = await connection.ReceivedFrames.WaitForAsync(
            f => f.Type == "input_audio_buffer.append" && f.Json.GetProperty("audio").GetString() == MicAt1_5xCooldown,
            TimeSpan.FromSeconds(1), ct);
        Assert.True(tooEarly is null,
            "Expected mic audio at 1.5x the normal cooldown to still be suppressed -- a greeting " +
            "that streamed partial audio with no audio.done must get the doubled post-greeting " +
            "cooldown (#48 S1), not the no-audio case's instant unmute.");

        // Past the full doubled cooldown (3.0s from on_response_done(), not from a never-sent
        // audio.done), suppression must finally lift -- confirms this is a bounded cooldown, not
        // a permanently latched mute. Bounded retry/poll instead of one fixed-delay send: PR #58
        // re-review S1 found a single `.Snapshot()`/fixed-delay check can race a busy backend
        // under the full suite's load, so a positive "eventually true" assertion always resends
        // and waits again rather than trusting one timed attempt.
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
