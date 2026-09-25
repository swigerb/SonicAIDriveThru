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
/// suppression: both this fake and real GA also emit an audio-done-shaped completion
/// (`response.output_audio.done`, `response.content_part.done`, etc. -- confirmed against the
/// official GA server-events reference, see below) for a *cancelled* response, which runs
/// through `echo.on_audio_done()` -- the same cooldown-clearing path a normal completion uses --
/// moments after any cancellation. That means "cancel an active response, then wait (however
/// patiently) for mic audio to resume" can pass even with `on_barge_in()` gutted to a no-op,
/// because the *other* path still clears suppression a little later regardless. Phase A below
/// sidesteps the confound entirely instead of racing it: right after the greeting fires,
/// `echo.ai_speaking` is still `True` from `echo.start_greeting_suppression()` (armed before the
/// greeting's own `response.create`, per rtmt.py), and -- because the greeting's own fake
/// response is explicitly scripted (see the `connection.Script.Enqueue` call before
/// `SendStartSessionAsync` below) with no audio output *and* a deliberately delayed completion
/// (`DoneEvent.Pace`, swigerb/SonicAIDriveThru#48 follow-up) -- neither `echo.on_audio_done()`
/// nor (post-#48) `echo.on_response_done()`'s own fallback ever get a chance to run before Phase
/// A's own `response.cancel`, so nothing *but* `on_barge_in()` can ever clear that particular
/// `ai_speaking=True`. Cancelling the still-open greeting response and then observing mic audio
/// start flowing is therefore proof of `on_barge_in()` specifically, with no race against any
/// competing completion event. Phase B then separately exercises the full, realistic "AI is
/// genuinely speaking real audio" sequence end-to-end for M3 and M7.
///
/// **Bug found and fixed (#8, PR #42 follow-up):** the "no audio output at all" premise above was
/// only true in the *docstring*, not in the code -- nothing was actually enqueued into
/// `connection.Script` before the greeting fired, so the greeting used
/// `ResponseScript.Default`, which *does* contain one `AudioDeltaEvent` plus a completing
/// `DoneEvent`. That meant the greeting's own NORMAL completion silently called
/// `echo.on_audio_done()` (clearing `ai_speaking` and arming a real, greeting-doubled cooldown)
/// well before Phase A's `response.cancel` ever ran, so by the time `on_barge_in()` executed,
/// `ai_speaking` was already `False` and suppression was being held open only by `cooldown_end`
/// -- which `on_barge_in()`'s surviving `cooldown_end = 0.0` line was sufficient to clear on its
/// own. A live mutation of `on_barge_in()` (removing only its `self.ai_speaking = False` line,
/// keeping `self.cooldown_end = 0.0`) survived this test as a result. Explicitly enqueuing an
/// audio-free `ResponseScript` for the greeting restored the isolation this docstring always
/// claimed.
///
/// **Reworked again (#48 follow-up):** fixing #48 (`echo.on_response_done()`: `response.done`
/// alone, with no audio, now also ends greeting suppression -- see
/// <see cref="GreetingWithoutAudioUnmutesTests"/> for that fix's own direct regression proof) put
/// the *same* confound back into this test, one level up: an audio-free greeting that completes
/// immediately (the old, un-paced `new DoneEvent()`) now has its `ai_speaking` cleared by
/// `on_response_done()`'s fallback before Phase A's `response.cancel` ever runs, exactly the way
/// the cancelled-response completion used to confound `on_barge_in()`'s isolation pre-#8. The
/// greeting's `DoneEvent` is now scripted with a long `Pace` (comfortably longer than this
/// phase's own work, always interrupted almost instantly by Phase A's own `response.cancel` --
/// see `DoneEvent.Pace`'s own doc comment) so the response is still genuinely open (no
/// `response.done` at all yet, from either path) when Phase A cancels it. Because the greeting's
/// own round trip token can no longer be waited on *before* Phase A (it doesn't arrive until the
/// greeting response itself completes, i.e. once Phase A's cancel interrupts it), the
/// synchronization point moved: this test now waits for the greeting's `response.create` to
/// reach the fake upstream instead (still a valid barrier -- `echo.start_greeting_suppression()`
/// always runs before that send, per rtmt.py's `send_greeting_once`), and waits for the
/// (now genuinely real, not "nothing to cancel") cancelled greeting's own round trip token right
/// after Phase A, to bound Phase B's own `firstDelta` wait the same way the old `greetingRoundTrip`
/// did.
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

        // Phase A's isolation depends on the greeting's response.done never arriving (from
        // either the normal audio path or, post-#48, echo.on_response_done()'s own fallback)
        // until *after* Phase A's own response.cancel has run -- otherwise on_barge_in() is no
        // longer the only thing that could have cleared ai_speaking. No audio at all (so
        // on_audio_done() is never called) plus a long Pace on the completion (so
        // on_response_done()'s fallback doesn't fire early either) keeps the response genuinely
        // open for this phase's whole duration. The Pace is always interrupted almost instantly
        // by this phase's own response.cancel below (DoneEvent.Pace uses the same
        // responseCts.Token-linked cancellation AudioDeltaEvent.Pace does), so this never actually
        // waits anywhere close to the scripted duration in a healthy run.
        connection!.Script.Enqueue(new ResponseScript([new DoneEvent(Pace: TimeSpan.FromSeconds(20))]));

        await browser.SendStartSessionAsync(cancellationToken: ct);

        // Can no longer wait for the greeting's own extension.round_trip_token here (post-#48
        // rework) -- it doesn't arrive until the greeting response actually completes, which this
        // phase deliberately delays. echo.start_greeting_suppression() always runs before the
        // greeting's response.create is sent (rtmt.py's send_greeting_once), so waiting for that
        // response.create to reach the fake upstream is an equally valid barrier: suppression is
        // guaranteed armed by the time it arrives.
        var greetingResponseCreate = await connection.ReceivedFrames.WaitForAsync(
            f => f.Type == "response.create", FrameTimeout, ct);
        Assert.True(greetingResponseCreate is not null, "Expected the greeting's own response.create to reach the fake upstream.");

        // ── Phase A: isolate echo.on_barge_in() itself (M4), with no competing completion event ──
        // The greeting's own fake response produces no audio and its completion is paced well out
        // (the audio-free, delayed script enqueued above), so neither echo.on_audio_done() nor
        // echo.on_response_done()'s fallback ever run for it before this phase's cancel, and
        // echo.ai_speaking stays True (armed by echo.start_greeting_suppression() before the
        // greeting fired) until something explicitly clears it. Confirm that stuck suppression is
        // genuinely active first.
        await browser.SendInputAudioAppendAsync(MicWhileGreetingStuckSuppressed, ct);

        // The greeting's response is still genuinely open at this point (its DoneEvent is
        // deliberately paced out above), so this response.cancel actually cancels it -- unlike
        // before the #48 rework, this is no longer "nothing to cancel". echo.on_barge_in() runs
        // synchronously as this response.cancel is relayed upstream (rtmt.py's
        // from_client_to_server), before the fake's own (fast, since the cancellation interrupts
        // its Pace immediately) cancelled-response completion can arrive back.
        await browser.SendResponseCancelAsync(ct);
        var isolatedCancelFrame = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence > greetingResponseCreate!.Sequence && f.Type == "response.cancel", FrameTimeout, ct);
        Assert.True(isolatedCancelFrame is not null, "Expected the browser's response.cancel to be relayed upstream.");

        Assert.DoesNotContain(connection.ReceivedFrames.Snapshot(), f =>
            f.Type == "input_audio_buffer.append" && f.Json.GetProperty("audio").GetString() == MicWhileGreetingStuckSuppressed);

        // The only thing that could possibly have changed echo.ai_speaking between the drop above
        // and this send is echo.on_barge_in() -- the greeting response was still open, and its
        // own Pace-delayed completion cannot be racing this (the Pace is interrupted by the
        // cancel itself, but on_response_done()'s "already cleared, just bookkeeping" branch is a
        // pure no-op for ai_speaking once on_barge_in() has already run -- see
        // EchoSuppressor.on_response_done's own doc comment). M4 ("mic audio stays suppressed
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

        // The greeting's own response is now cancelled (Phase A's response.cancel above
        // interrupted its Pace) -- wait for its round trip token so Phase B's firstDelta wait
        // below has a browser.ReceivedFrames sequence bound to start from, same role the old
        // pre-Phase-A greetingRoundTrip wait used to serve.
        var greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");

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
    });
}
