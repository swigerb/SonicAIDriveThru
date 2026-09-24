using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// #8 "voice lock": once assistant audio has been seen on a connection, GA rejects any later
/// session.update whose voice differs from the current one with `cannot_update_voice` — and
/// rejects the WHOLE event, tools included (see app/backend/rtmt.py's <c>_build_session</c> and
/// <c>_strip_output_voice</c>). rtmt.py avoids ever hitting that rejection by proactively omitting
/// `audio.output.voice` from every session.update it forwards once locked, and by having
/// `extension.set_voice` defer entirely (update its own <c>voice_choice</c> but send nothing
/// upstream) instead of trying to change the voice mid-conversation. Ported from the behaviour
/// covered by app/backend/tests/test_rtmt.py's voice-lock cases and the fake's own self-test
/// (<c>assistant_audio_seen</c> is a per-connection local in rtmt.py, so this is fully provable on
/// a single connection: greet once, then re-send the browser's own session.update and inspect it).
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class VoiceLockTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Voice_is_omitted_from_session_update_once_assistant_audio_has_been_sent() => fixture.RunAsync(async () =>
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

        var firstUpdate = await connection!.ReceivedFrames.WaitForAsync(
            f => f.Sequence > 0 && f.Type == "session.update", FrameTimeout, ct);
        Assert.True(firstUpdate is not null, "The browser's first session.update was never forwarded upstream.");

        // Before any assistant audio, voice IS sent -- confirms the golden "before" state so the
        // "after" assertion below is a genuine delta, not just an update that never carried voice.
        // Captured dynamically (not compared against BackendContract.DefaultVoice) because
        // self.voice_choice on rtmt.py's RTMiddleTier is a single process-wide attribute shared by
        // every connection in this test run (see extension.set_voice handling at rtmt.py ~1252,
        // which updates it unconditionally even when the update itself is deferred) -- an earlier
        // test picking a non-default voice would otherwise make this assertion order-dependent.
        var firstAudio = firstUpdate!.Json.GetProperty("session").GetProperty("audio");
        var voiceBeforeLock = firstAudio.GetProperty("output").GetProperty("voice").GetString();
        Assert.False(string.IsNullOrWhiteSpace(voiceBeforeLock), "Expected session.audio.output.voice to be present before any assistant audio.");

        // Wait for the greeting's assistant audio to actually arrive at the browser (proves
        // assistant_audio_seen is genuinely true on the backend's connection to the fake before
        // the second session.update below is sent, not merely "greeting requested").
        var roundTripToken = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(roundTripToken is not null, "extension.round_trip_token never reached the browser (greeting never completed).");

        // A second startSession() call (e.g. the guest re-toggles the mic) -- same payload as
        // before, but the voice must now be omitted rather than repeated.
        await browser.SendStartSessionAsync(cancellationToken: ct);
        var secondUpdate = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence > firstUpdate.Sequence && f.Type == "session.update", FrameTimeout, ct);
        Assert.True(secondUpdate is not null, "The browser's second session.update was never forwarded upstream.");

        var secondSession = secondUpdate!.Json.GetProperty("session");
        Assert.True(secondSession.TryGetProperty("audio", out var secondAudio),
            "Expected session.audio.input.* to still be present (turn_detection/transcription) -- only voice should be stripped.");
        var stillHasVoice = secondAudio.TryGetProperty("output", out var output) && output.TryGetProperty("voice", out _);
        Assert.False(stillHasVoice, "Voice must be omitted from any session.update sent after assistant audio was seen.");
    });

    [Fact]
    public Task Voice_picker_defers_the_update_entirely_once_assistant_audio_has_been_sent() => fixture.RunAsync(async () =>
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
        var roundTripToken = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(roundTripToken is not null, "extension.round_trip_token never reached the browser (greeting never completed).");

        var watermark = connection!.ReceivedFrames.Snapshot().Count;
        var updateCountBefore = connection.ReceivedFrames.Snapshot().Count(f => f.Type == "session.update");

        // The picker offers ten GA voices (config.yaml) -- pick any one different from the
        // default so this would be a genuine voice change if it were sent at all.
        await browser.SendExtensionSetVoiceAsync("cedar", cancellationToken: ct);

        // Bounded absence check: rtmt.py's extension.set_voice handler updates its own
        // voice_choice in-process but -- once assistant_audio_seen -- sends NO session.update
        // upstream at all (fully deferred, not merely voice-stripped). A short, generous window
        // is enough to catch a regression that resumed sending it; there is nothing else to wait
        // on, since correctly deferring is the absence of a frame.
        var spurious = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence >= watermark && f.Type == "session.update", TimeSpan.FromSeconds(2), ct);
        Assert.True(spurious is null,
            "extension.set_voice must defer (send nothing upstream) once assistant audio was already seen, " +
            $"but a session.update arrived: {spurious?.Json}");

        var updateCountAfter = connection.ReceivedFrames.Snapshot().Count(f => f.Type == "session.update");
        Assert.Equal(updateCountBefore, updateCountAfter);
    });
}
