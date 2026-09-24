using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// #8 barge-in (PR #42 review item 5): <c>audio_pipeline.EchoSuppressor.on_barge_in</c> was
/// previously only exercised indirectly -- <see cref="ResponseCancelRelayTests"/> proves the
/// browser's `response.cancel` itself is relayed and that `response.done status:"cancelled"` is
/// handled, but never that mic audio is actually dropped while the AI is speaking, nor that
/// dropping it is lifted again once the browser barges in. Mutations M3 ("no echo suppression at
/// all"), M4 ("mic audio stays suppressed after response.cancel") and M7 ("backend sends
/// response.cancel upstream twice") all survived Rick's mutation run because nothing asserted on
/// `input_audio_buffer.append` frames actually reaching (or not reaching) the upstream socket.
///
/// This scenario drives the exact sequence rtmt.py's `from_client_to_server` implements
/// (`_forward_messages` in rtmt.py, ~line 1263-1272): while the AI is mid-response,
/// `input_audio_buffer.append` is dropped (never forwarded); `response.cancel` is always
/// forwarded and calls `echo.on_barge_in()`, which resets `ai_speaking`/`cooldown_end`; a
/// following `input_audio_buffer.append` is then forwarded normally.
///
/// M4 specifically needs an *isolated* proof that `echo.on_barge_in()` itself is what lifts
/// suppression: both this fake and real GA also emit an audio-done-shaped completion for a
/// *cancelled* response (`response.output_audio.done` then `response.done`), which runs through
/// `echo.on_audio_done()` -- the same cooldown-clearing path a normal completion uses -- moments
/// after any cancellation. That means "cancel an active response, then wait (however patiently)
/// for mic audio to resume" can pass even with `on_barge_in()` gutted to a no-op, because the
/// *other* path still clears suppression a little later regardless -- confirmed empirically: a
/// timestamp-ordering variant of this test (append must beat the cancelled response's own
/// response.done on the wire) still failed under *correct* code, because the fake's own
/// audio-done+response.done sequence for a cancelled response completes fast enough in-process to
/// win that race regardless of on_barge_in. Phase A below sidesteps the confound entirely instead
/// of racing it: right after the greeting round trip, `echo.ai_speaking` is still `True` from
/// `echo.start_greeting_suppression()` (armed before the greeting's own `response.create`, per
/// rtmt.py), and -- because the greeting's own fake response here is scripted with no audio
/// output at all -- `echo.on_audio_done()` is never called for it, so nothing *but*
/// `on_barge_in()` can ever clear that particular `ai_speaking=True`. Sending `response.cancel`
/// with no active upstream response at all (harmless; the fake just errors it back) and then
/// observing mic audio start flowing is therefore proof of `on_barge_in()` specifically, with no
/// race against a competing completion event. Phase B then separately exercises the full,
/// realistic "AI is genuinely speaking real audio" sequence end-to-end for M3 and M7.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class EchoSuppressionBargeInTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);
    private const string MicWhileGreetingStuckSuppressed = "bWljLWR1cmluZy1ncmVldGluZw==";
    private const string MicAfterIsolatedBargeIn = "bWljLWFmdGVyLWlzb2xhdGVkLWJhcmdlLWlu";
    private const string MicDuringAiSpeech = "bWljLWR1cmluZy1haS1zcGVlY2g=";
    private const string MicAfterCancel = "bWljLWFmdGVyLWNhbmNlbA==";

    [Fact]
    public Task Mic_audio_is_dropped_while_ai_speaks_and_resumes_after_the_browsers_own_response_cancel() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        await browser.SendStartSessionAsync(cancellationToken: ct);
        var greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");

        // ── Phase A: isolate echo.on_barge_in() itself (M4), with no competing completion event ──
        // The greeting's own fake response produces no audio (nothing has been enqueued into
        // connection.Script yet at this point -- the default script), so echo.on_audio_done() is
        // never called for it and echo.ai_speaking stays True (armed by
        // echo.start_greeting_suppression() before the greeting fired) until something explicitly
        // clears it. Confirm that stuck suppression is genuinely active first.
        await browser.SendInputAudioAppendAsync(MicWhileGreetingStuckSuppressed, ct);

        // There is no active upstream response at this point (the greeting's own already
        // completed, and nothing else has been created), so this is sent purely to exercise
        // echo.on_barge_in() itself; the fake errors it back harmlessly.
        await browser.SendResponseCancelAsync(ct);
        var isolatedCancelFrame = await connection!.ReceivedFrames.WaitForAsync(
            f => f.Type == "response.cancel", FrameTimeout, ct);
        Assert.True(isolatedCancelFrame is not null, "Expected the browser's response.cancel to be relayed upstream even with nothing active to cancel.");

        Assert.DoesNotContain(connection.ReceivedFrames.Snapshot(), f =>
            f.Type == "input_audio_buffer.append" && f.Json.GetProperty("audio").GetString() == MicWhileGreetingStuckSuppressed);

        // The only thing that could possibly have changed echo.ai_speaking between the drop above
        // and this send is echo.on_barge_in() -- there is no active response, so no
        // echo.on_audio_done() completion can be racing this. M4 ("mic audio stays suppressed
        // after response.cancel") must fail here: with on_barge_in() gutted to a no-op, nothing
        // else would ever clear this and this send would time out.
        await browser.SendInputAudioAppendAsync(MicAfterIsolatedBargeIn, ct);
        var isolatedResume = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence > isolatedCancelFrame!.Sequence && f.Type == "input_audio_buffer.append"
                && f.Json.GetProperty("audio").GetString() == MicAfterIsolatedBargeIn,
            FrameTimeout, ct);
        Assert.True(isolatedResume is not null,
            "Expected mic audio to be forwarded upstream immediately after response.cancel with no " +
            "active response to cancel -- the only mechanism that could have lifted suppression " +
            "here is echo.on_barge_in() itself.");

        // ── Phase B: the full, realistic "AI is genuinely speaking real audio" sequence ──
        // Give the AI a still-active response to speak over -- the second delta is paced well out
        // and `DoneEvent` further still, giving a wide, non-racy window to exercise the
        // append -> cancel -> append sequence entirely while EchoSuppressor.ai_speaking is still
        // true, exactly like ResponseCancelRelayTests' barge-in-and-relay scenario.
        connection.Script.Enqueue(new ResponseScript(
        [
            new AudioDeltaEvent("YmFyZ2UtaW4tZmlyc3Q="),
            new AudioDeltaEvent("YmFyZ2UtaW4tc2Vjb25k", Pace: TimeSpan.FromSeconds(5)),
            new DoneEvent(),
        ]));
        await browser.SendResponseCreateAsync(ct);

        var firstDelta = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > greetingRoundTrip!.Sequence && f.Type == "response.audio.delta", FrameTimeout, ct);
        Assert.True(firstDelta is not null, "Expected AI audio to start playing before exercising echo suppression.");

        // 1) input_audio_buffer.append while the AI is speaking must be DROPPED -- never
        // forwarded upstream at all (M3: "no echo suppression at all" would forward it here).
        await browser.SendInputAudioAppendAsync(MicDuringAiSpeech, ct);

        // 2) The barge-in: response.cancel is always forwarded upstream, and its arrival here
        // also serves as the sentinel proving the dropped append above had every chance to show
        // up on the wire (if it were ever going to) before this assertion runs.
        // NOTE: firstDelta.Sequence is from browser.ReceivedFrames -- a *different* FrameLog
        // instance (with its own independent counter) than connection.ReceivedFrames (the fake's
        // upstream view), so it must never be used to bound a wait on connection.ReceivedFrames.
        // isolatedCancelFrame.Sequence, however, *is* on connection.ReceivedFrames, so it is a
        // valid bound here: this phase's cancel is the next one after Phase A's.
        await browser.SendResponseCancelAsync(ct);
        var cancelFrame = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence > isolatedCancelFrame!.Sequence && f.Type == "response.cancel", FrameTimeout, ct);
        Assert.True(cancelFrame is not null, "Expected the browser's response.cancel to be relayed upstream.");

        Assert.DoesNotContain(connection.ReceivedFrames.Snapshot(), f =>
            f.Type == "input_audio_buffer.append" && f.Json.GetProperty("audio").GetString() == MicDuringAiSpeech);

        // M7 ("backend sends response.cancel upstream twice"): exactly one response.cancel frame
        // per browser-initiated cancel ever reaches upstream (Phase A's own send plus this one --
        // no more), and this phase's is the one just captured above -- nothing synthesized ever
        // preceded (or followed) either of the browser's two cancels.
        var allCancelFrames = connection.ReceivedFrames.Snapshot().Where(f => f.Type == "response.cancel").ToArray();
        Assert.Equal(2, allCancelFrames.Length);
        Assert.Equal(cancelFrame!.Sequence, allCancelFrames[1].Sequence);

        // 3) M4 regression net: a further append, sent strictly after this phase's cancel, is
        // still forwarded upstream once the AI was genuinely speaking real audio end-to-end --
        // resent like a real, continuously-streaming mic would, since a legitimate brief
        // re-suppression can follow this specific cancellation (the cancelled response's own
        // completion also runs through echo.on_audio_done's cooldown path) -- but M4 itself is
        // already conclusively proven by Phase A above; a permanent latch here would still time
        // this out and fail the test regardless.
        RecordedFrame? secondAppend = null;
        var appendDeadline = DateTime.UtcNow + FrameTimeout;
        while (secondAppend is null && DateTime.UtcNow < appendDeadline)
        {
            await browser.SendInputAudioAppendAsync(MicAfterCancel, ct);
            secondAppend = await connection.ReceivedFrames.WaitForAsync(
                f => f.Sequence > cancelFrame.Sequence && f.Type == "input_audio_buffer.append"
                    && f.Json.GetProperty("audio").GetString() == MicAfterCancel,
                TimeSpan.FromMilliseconds(300), ct);
        }
        Assert.True(secondAppend is not null,
            "Expected mic audio sent after the browser's response.cancel to be forwarded upstream " +
            "(echo suppression must be lifted, not remain latched).");

        // Upstream order: cancel strictly before the resumed mic audio (already implied by the
        // WaitForAsync predicate's Sequence bound above, restated explicitly here).
        Assert.True(cancelFrame.Sequence < secondAppend!.Sequence,
            "Expected response.cancel to precede the resumed mic audio on the wire.");
    },
    // Phase A's response.cancel with nothing active to cancel is a deliberate, expected part of
    // this scenario (it is what makes the on_barge_in proof confound-free): GA/the fake correctly
    // rejects it with a generic upstream `error` event (relayed to the browser like any other
    // upstream frame, per rtmt.py's ordinary passthrough -- not swallowed or mishandled), and
    // rtmt.py additionally logs that rejection server-side as a single ERROR-level line. This is
    // proven harmless above (the browser's own barge-in still works correctly and every
    // assertion after it passes), so it is not an unexpected/unhandled error.
    allowedNewBackendErrors: 1);
}
