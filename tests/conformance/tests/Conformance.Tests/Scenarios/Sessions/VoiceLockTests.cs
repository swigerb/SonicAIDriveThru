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
///
/// `extension.set_voice` itself (deferring, and what the deferred voice then does to the next
/// conversation) is covered by <see cref="VoicePickerTests"/> on its own dedicated collection —
/// see <see cref="VoicePickerConformanceFixture"/>'s docs for why. This class never calls
/// `extension.set_voice`, so <see cref="BackendContract.DefaultVoice"/> is safe to pin here.
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

        // Before any assistant audio, voice IS sent, and pinned to the exact default -- confirms
        // the golden "before" state so the "after" assertion below is a genuine delta, not just
        // an update that never carried voice. Safe to pin (not merely assert presence) because
        // this collection's backend process never runs extension.set_voice (see class docs above).
        var firstAudio = firstUpdate!.Json.GetProperty("session").GetProperty("audio");
        var voiceBeforeLock = firstAudio.GetProperty("output").GetProperty("voice").GetString();
        Assert.Equal(BackendContract.DefaultVoice, voiceBeforeLock);

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
}
