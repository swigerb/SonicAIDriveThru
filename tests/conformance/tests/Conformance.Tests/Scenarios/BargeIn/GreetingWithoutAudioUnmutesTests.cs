using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// swigerb/SonicAIDriveThru#48: a greeting that produces no audio at all (a text-only fallback, a
/// response cancelled/failed before any audio, a rate-limited retry with no output) never reaches
/// <c>audio_pipeline.EchoSuppressor.on_audio_done()</c> -- before this fix, only a real audio
/// delta/done pair (or the browser's own `response.cancel`, via `on_barge_in()`) ever cleared
/// `ai_speaking`/`cooldown_end`. Without either, `should_suppress_audio()` dropped every
/// `input_audio_buffer.append` forever: the guest's mic stayed muted until they physically
/// interrupted, which they have no reason to do since the AI never said anything requiring
/// interruption in the first place.
///
/// This scenario scripts the fake's greeting response with a bare <see cref="DoneEvent"/> (no
/// <see cref="AudioDeltaEvent"/> at all -- the fake has no distinct text-delta event type, but a
/// text-only completion and a fully output-less one share the exact root cause exercised here:
/// neither ever calls `on_audio_done()`), then proves the guest's very next mic append is
/// forwarded upstream *without the browser ever sending `response.cancel`*. This is the direct,
/// unassisted regression proof for the bug: `response.done` alone, with no audio and no browser
/// interrupt, must be what unmutes the mic (`audio_pipeline.EchoSuppressor.on_response_done`, the
/// #48 fix). Contrast with <see cref="EchoSuppressionBargeInTests"/>'s Phase A, which deliberately
/// *delays* the greeting's completion (via `DoneEvent.Pace`) and relies on an explicit browser
/// barge-in to isolate `on_barge_in()` specifically -- a different, narrower claim than this test's.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class GreetingWithoutAudioUnmutesTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);
    private const string MicAfterSilentGreeting = "bWljLWFmdGVyLXNpbGVudC1ncmVldGluZw==";

    [Fact]
    public Task Mic_audio_is_forwarded_after_an_audio_free_greeting_with_no_barge_in() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        // The greeting produces no audio at all and completes immediately (no Pace) -- exactly
        // the #48 bug's trigger: echo.on_audio_done() is never called for it.
        connection!.Script.Enqueue(new ResponseScript([new DoneEvent()]));

        await browser.SendStartSessionAsync(cancellationToken: ct);
        var greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");

        // No response.cancel anywhere in this test -- unlike EchoSuppressionBargeInTests, the mic
        // must be unmuted by response.done itself (echo.on_response_done), with no browser
        // interrupt at all. Before the #48 fix, this send would simply never reach upstream and
        // the WaitForAsync below would time out.
        await browser.SendInputAudioAppendAsync(MicAfterSilentGreeting, ct);
        var forwarded = await connection.ReceivedFrames.WaitForAsync(
            f => f.Type == "input_audio_buffer.append" && f.Json.GetProperty("audio").GetString() == MicAfterSilentGreeting,
            FrameTimeout, ct);
        Assert.True(forwarded is not null,
            "Expected mic audio to be forwarded upstream after an audio-free greeting completed, " +
            "with no browser response.cancel at all — #48: response.done alone must end greeting " +
            "suppression when no audio was ever produced.");
    });
}
