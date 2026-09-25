using System.Net.WebSockets;
using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// #8 (PR #42 review item 6): <c>extension.set_voice</c> scenarios that must run on their own
/// dedicated backend process -- see <see cref="VoicePickerConformanceFixture"/>'s docs for why.
/// </summary>
[Collection(VoicePickerConformanceCollection.Name)]
public sealed class VoicePickerTests(VoicePickerConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

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

        // PR #42 review item 8: wait for actual assistant audio to reach the browser, not just
        // the round trip token -- see VoiceLockTests.cs's matching comment for why round_trip_token
        // alone doesn't prove assistant_audio_seen.
        var greetingAudio = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "response.audio.delta", FrameTimeout, ct);
        Assert.True(greetingAudio is not null, "Expected the greeting to send assistant audio to the browser before the voice lock can be exercised.");

        var roundTripToken = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(roundTripToken is not null, "extension.round_trip_token never reached the browser (greeting never completed).");

        var watermark = connection!.ReceivedFrames.Snapshot().Count;
        var updateCountBefore = connection.ReceivedFrames.Snapshot().Count(f => f.Type == "session.update");

        // The picker offers ten GA voices (config.yaml) -- pick any one different from the
        // default so this would be a genuine voice change if it were sent at all.
        await browser.SendExtensionSetVoiceAsync("cedar", cancellationToken: ct);

        // PR #42 review item 8 ("VoicePickerTests defer test: sentinel instead of a bounded
        // absence window"): rather than sleeping for a fixed window and hoping nothing shows up
        // in it, provoke a second, deterministic upstream frame right after the pick -- the
        // browser's own follow-up session.update (rtmt.py always forwards this; voice omitted
        // once locked, see VoiceLockTests.cs) -- and assert THAT is the very next session.update
        // to reach upstream, with the update count only having increased by one. If
        // extension.set_voice had regressed to sending its own (voice-carrying) session.update,
        // it would arrive before this sentinel and be the frame this wait actually captures,
        // failing the "voice omitted" assertion below immediately rather than relying on a race
        // against a fixed sleep.
        //
        // #28 N20 (Rick's final review of #42, follow-up 1): matching on `Type == "session.update"`
        // alone was not enough -- a locked extension.set_voice regressed to a *voice-less*
        // fallback update (`instructions`/`tools` only, no `audio` block at all) still arrives
        // first and gets captured as "the sentinel", passing the voice-omitted check below for
        // the wrong reason (survived 3/3). The browser's own SendStartSessionAsync follow-up
        // always carries a full `session.audio.input.turn_detection` block; the fallback-shaped
        // update never does -- requiring it here rejects that impostor frame and keeps waiting
        // for the real one.
        await browser.SendStartSessionAsync(cancellationToken: ct);
        var sentinelUpdate = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence >= watermark && f.Type == "session.update" && HasTurnDetection(f), FrameTimeout, ct);
        Assert.True(sentinelUpdate is not null, "Expected the browser's own follow-up session.update to reach upstream.");

        var updateCountAfter = connection.ReceivedFrames.Snapshot().Count(f => f.Type == "session.update");
        Assert.Equal(updateCountBefore + 1, updateCountAfter);

        var sentinelSession = sentinelUpdate!.Json.GetProperty("session");
        var sentinelHasVoice = sentinelSession.TryGetProperty("audio", out var sentinelAudio)
            && sentinelAudio.TryGetProperty("output", out var sentinelOutput) && sentinelOutput.TryGetProperty("voice", out _);
        Assert.False(sentinelHasVoice,
            "extension.set_voice must defer (send nothing upstream) once assistant audio was already seen -- " +
            "the only session.update to reach upstream after the pick must be the browser's own follow-up, " +
            $"with voice still omitted (locked): {sentinelUpdate.Json}");
    });

    /// <summary>#28 N20: true only for a session.update that carries the full
    /// `session.audio.input.turn_detection` block the browser's own follow-up session.update
    /// always sends -- a voice-less *fallback* session.update (the shape a locked
    /// extension.set_voice would regress to) never has an `audio` block at all, so this predicate
    /// tells the two apart instead of matching on `type` alone.</summary>
    private static bool HasTurnDetection(RecordedFrame frame) =>
        frame.Type == "session.update"
        && frame.Json.TryGetProperty("session", out var session)
        && session.TryGetProperty("audio", out var audio)
        && audio.TryGetProperty("input", out var input)
        && input.TryGetProperty("turn_detection", out _);

    /// <summary>
    /// Mutation-check for #28 N20's fix, reproduced from the issue's own description of mutation
    /// D2 ("a locked set_voice sends a voice-less update with instructions and tools only")
    /// without needing to actually regress <c>app/backend/rtmt.py</c> (out of Stream C's scope) to
    /// prove it: hand-build the exact two frame shapes the real defer test's sentinel wait must
    /// tell apart, and assert <see cref="HasTurnDetection"/> accepts the real one and rejects the
    /// D2-shaped impostor. Reverting this predicate to `Type == "session.update"` alone (the
    /// pre-N20 code) makes the second assertion below fail, which is exactly the false-pass the
    /// issue reports the live suite previously had.
    /// </summary>
    [Fact]
    public void HasTurnDetection_accepts_the_real_follow_up_and_rejects_the_D2_fallback_shape()
    {
        var realFollowUp = MakeSessionUpdateFrame("""
            {
              "type": "session.update",
              "session": {
                "instructions": "You are Sonic.",
                "tools": [],
                "audio": { "input": { "turn_detection": { "type": "server_vad" } } }
              }
            }
            """);
        Assert.True(HasTurnDetection(realFollowUp), "The browser's own follow-up session.update always carries audio.input.turn_detection.");

        var d2Fallback = MakeSessionUpdateFrame("""
            {
              "type": "session.update",
              "session": {
                "instructions": "You are Sonic.",
                "tools": []
              }
            }
            """);
        Assert.False(HasTurnDetection(d2Fallback), "Mutation D2's voice-less fallback update never has an audio block at all -- it must not be mistaken for the real sentinel.");
    }

    private static RecordedFrame MakeSessionUpdateFrame(string json) =>
        new(Sequence: 0, Type: "session.update", Json: JsonDocument.Parse(json).RootElement, ReceivedAt: DateTimeOffset.UtcNow);

    /// <summary>
    /// PR #42 review item 8 ("set_voice before lock"): the deferred behaviour only applies once
    /// <c>assistant_audio_seen</c> is true. Picking a voice BEFORE the greeting has sent any
    /// assistant audio must take effect immediately -- a session.update carrying the new voice
    /// forwarded upstream right away -- exercising the `else` branch of rtmt.py's
    /// extension.set_voice handler that the other tests in this class never reach.
    /// </summary>
    [Fact]
    public Task Voice_picker_updates_immediately_before_any_assistant_audio_has_been_sent() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        // Deliberately pick the voice before ever calling SendStartSessionAsync (no browser
        // session.update, no greeting, no assistant audio at all yet) -- only the bootstrap
        // session.update (Sequence == 0) has been sent on this connection so far.
        var bootstrap = await connection!.ReceivedFrames.WaitForAsync(f => f.Sequence == 0, FrameTimeout, ct);
        Assert.True(bootstrap is not null, "Bootstrap session.update never arrived.");

        await browser.SendExtensionSetVoiceAsync("cedar", cancellationToken: ct);

        var immediateUpdate = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence > bootstrap!.Sequence && f.Type == "session.update", FrameTimeout, ct);
        Assert.True(immediateUpdate is not null,
            "extension.set_voice must be forwarded upstream immediately when no assistant audio has been seen yet, not deferred.");

        var pickedVoice = immediateUpdate!.Json.GetProperty("session").GetProperty("audio")
            .GetProperty("output").GetProperty("voice").GetString();
        Assert.Equal("cedar", pickedVoice);
    });

    /// <summary>
    /// #43 fix (PR #49 review round 6, "S1"): the round-5 fix left <c>self._voice_override</c> as
    /// a process-wide sticky default for every future NEW connection, so guest A's pick still
    /// became guest B's, C's, ... default -- exactly Rick's S1 finding ("Guest A can still change
    /// every guest's voice"). A brand-new, UNRELATED connection (no resume presented, so it has
    /// nothing to do with the first guest's session) must always bootstrap with the server's
    /// config default, never another guest's pick. Renamed and inverted from the round-5 test of
    /// the same shape, which asserted the (now fixed) opposite behaviour.
    /// </summary>
    [Fact]
    public Task Voice_picked_after_lock_does_not_carry_to_a_brand_new_unrelated_connection() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        const string pickedVoice = "verse";

        var firstConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using (var firstBrowser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct))
        {
            var firstConnection = await firstConnectionTask;
            Assert.True(firstConnection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

            var firstBootstrap = await firstConnection!.ReceivedFrames.WaitForAsync(f => f.Sequence == 0, FrameTimeout, ct);
            Assert.True(firstBootstrap is not null, "Bootstrap session.update never arrived on the first connection.");
            var voiceBeforePick = firstBootstrap!.Json.GetProperty("session").GetProperty("audio")
                .GetProperty("output").GetProperty("voice").GetString();
            Assert.NotEqual(pickedVoice, voiceBeforePick);

            await firstBrowser.SendStartSessionAsync(cancellationToken: ct);

            // Wait for actual assistant audio to reach the browser, not just the round trip
            // token -- see VoiceLockTests.cs's matching comment for why round_trip_token alone
            // doesn't prove assistant_audio_seen.
            var firstGreetingAudio = await firstBrowser.ReceivedFrames.WaitForAsync(
                f => f.Type == "response.audio.delta", FrameTimeout, ct);
            Assert.True(firstGreetingAudio is not null, "Expected the greeting to send assistant audio to the browser before the voice lock can be exercised.");

            var roundTripToken = await firstBrowser.ReceivedFrames.WaitForAsync(
                f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
            Assert.True(roundTripToken is not null, "extension.round_trip_token never reached the browser (greeting never completed).");

            // Lock the connection (assistant audio already seen via the greeting above), then
            // pick a voice -- persisted to THIS session only, and (locked) sends nothing upstream
            // on this connection.
            await firstBrowser.SendExtensionSetVoiceAsync(pickedVoice, cancellationToken: ct);

            await firstBrowser.CloseAsync(WebSocketCloseStatus.NormalClosure, "voice picked, moving to next conversation", ct);
            await firstBrowser.WaitForCloseAsync(FrameTimeout, ct);
        }

        var firstClosed = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(firstClosed, "Expected the first connection's upstream socket to close before starting the next conversation.");

        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var secondBrowser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var secondConnection = await secondConnectionTask;
        Assert.True(secondConnection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        var bootstrap = await secondConnection!.ReceivedFrames.WaitForAsync(f => f.Sequence == 0, FrameTimeout, ct);
        Assert.True(bootstrap is not null, "Bootstrap session.update never arrived on the next, unrelated conversation.");

        var bootstrapVoice = bootstrap!.Json.GetProperty("session").GetProperty("audio")
            .GetProperty("output").GetProperty("voice").GetString();
        Assert.NotEqual(pickedVoice, bootstrapVoice);
    });

    /// <summary>
    /// #43 fix (PR #49 review round 6, "S1"): the picked voice belongs to the guest's OWN
    /// session, persisted server-side (<c>session_manager.py</c>'s <c>set_voice</c>/<c>get_voice</c>),
    /// and restored on resume -- a Wi-Fi blip must not silently revert the guest's own choice
    /// back to the default. A resume opens a brand-new upstream connection (see rtmt.py's module
    /// docstring), so the bootstrap always uses the config default before the resume is even
    /// known; only a follow-up session.update (sent once the resumed session_id is confirmed)
    /// can carry the restored voice -- mirrors the rehydration item's own "brief the new upstream
    /// after bootstrap" pattern (see <see cref="ResumeRehydrationClientVisibilityTests"/>).
    /// </summary>
    [Fact]
    public Task Resumed_connection_restores_the_picked_voice() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        const string pickedVoice = "cedar";

        var firstConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        var first = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var firstConnection = await firstConnectionTask;
        Assert.True(firstConnection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        await first.SendStartSessionAsync(cancellationToken: ct);
        var metadata = await first.ReceivedFrames.WaitForAsync(f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(metadata is not null, "Expected extension.session_metadata on the first connection.");
        var resumeId = metadata!.Json.GetProperty("resumeId").GetString();
        Assert.False(string.IsNullOrEmpty(resumeId), "extension.session_metadata must carry a non-empty resumeId.");

        // Wait for actual assistant audio to reach the browser, not just the round trip token --
        // see VoiceLockTests.cs's matching comment for why round_trip_token alone doesn't prove
        // assistant_audio_seen.
        var greetingAudio = await first.ReceivedFrames.WaitForAsync(
            f => f.Type == "response.audio.delta", FrameTimeout, ct);
        Assert.True(greetingAudio is not null, "Expected the greeting to send assistant audio to the browser before the voice lock can be exercised.");

        var roundTripToken = await first.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(roundTripToken is not null, "extension.round_trip_token never reached the browser (greeting never completed).");

        // Assistant audio has already been seen (the greeting above), so this pick is deferred --
        // sends nothing upstream on THIS connection -- but must still be PERSISTED to the session
        // for a later resume to restore. Sentinel on a second browser session.update (same idiom
        // as Voice_picker_defers_the_update_entirely_once_assistant_audio_has_been_sent) to prove
        // the pick was fully processed server-side before we drop the connection.
        await first.SendExtensionSetVoiceAsync(pickedVoice, cancellationToken: ct);
        var watermark = firstConnection!.ReceivedFrames.Snapshot().Count;
        await first.SendStartSessionAsync(cancellationToken: ct);
        var sentinelUpdate = await firstConnection.ReceivedFrames.WaitForAsync(
            f => f.Sequence >= watermark && f.Type == "session.update", FrameTimeout, ct);
        Assert.True(sentinelUpdate is not null, "Expected the browser's own follow-up session.update to reach upstream.");

        await first.CloseAsync(cancellationToken: ct);
        await first.WaitForCloseAsync(FrameTimeout, ct);
        await first.DisposeAsync();

        var firstClosed = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(firstClosed, "Expected the first connection's upstream socket to close before resuming.");

        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var second = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var secondConnection = await secondConnectionTask;
        Assert.True(secondConnection is not null, "No upstream connection was accepted for the resumed browser socket.");

        await second.SendExtensionResumeAsync(resumeId!, ct);
        var resumed = await second.ReceivedFrames.WaitForAsync(f => f.Type == "extension.session_resumed", FrameTimeout, ct);
        Assert.True(resumed is not null, "Expected extension.session_resumed on the resumed connection.");

        var bootstrap = await secondConnection!.ReceivedFrames.WaitForAsync(f => f.Sequence == 0, FrameTimeout, ct);
        Assert.True(bootstrap is not null, "Bootstrap session.update never arrived on the resumed connection.");
        var bootstrapVoice = bootstrap!.Json.GetProperty("session").GetProperty("audio")
            .GetProperty("output").GetProperty("voice").GetString();
        // The bootstrap fires before the resume is confirmed, so it must always use the config
        // default first -- see this test's own doc comment.
        Assert.NotEqual(pickedVoice, bootstrapVoice);

        var restoreUpdate = await secondConnection.ReceivedFrames.WaitForAsync(
            f => f.Type == "session.update" && f.Sequence > bootstrap!.Sequence, FrameTimeout, ct);
        Assert.True(restoreUpdate is not null,
            "Expected a follow-up session.update restoring this session's own picked voice on the resumed connection.");
        var restoredVoice = restoreUpdate!.Json.GetProperty("session").GetProperty("audio")
            .GetProperty("output").GetProperty("voice").GetString();
        Assert.Equal(pickedVoice, restoredVoice);
    });

    /// <summary>
    /// PR #49 review round 5, Must (M1): extension.set_voice used to trust any non-empty string
    /// the browser sent. A forged voice name must now be dropped entirely -- it must not reach
    /// upstream on the sender's own connection (immediate-pick path, no assistant audio yet), and
    /// it must not become the new default for a later, unrelated guest's bootstrap either.
    /// </summary>
    [Fact]
    public Task Unknown_voice_is_rejected_and_never_reaches_upstream_or_a_new_guests_bootstrap() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        const string forgedVoice = "rick_probe_voice";

        var guestAConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using (var guestA = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct))
        {
            var guestAConnection = await guestAConnectionTask;
            Assert.True(guestAConnection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

            var bootstrap = await guestAConnection!.ReceivedFrames.WaitForAsync(f => f.Sequence == 0, FrameTimeout, ct);
            Assert.True(bootstrap is not null, "Bootstrap session.update never arrived.");
            var voiceBeforePick = bootstrap!.Json.GetProperty("session").GetProperty("audio")
                .GetProperty("output").GetProperty("voice").GetString();
            Assert.NotEqual(forgedVoice, voiceBeforePick);

            var watermarkA = guestAConnection.ReceivedFrames.Snapshot().Count;
            await guestA.SendExtensionSetVoiceAsync(forgedVoice, cancellationToken: ct);

            // No assistant audio has been seen yet, so a legitimate pick would forward
            // immediately (see Voice_picker_updates_immediately_before_any_assistant_audio_has_been_sent)
            // -- a bounded absence window here proves the forged voice was dropped, not merely
            // deferred.
            var spuriousUpdateOnA = await guestAConnection.ReceivedFrames.WaitForAsync(
                f => f.Sequence >= watermarkA && f.Type == "session.update", TimeSpan.FromSeconds(2), ct);
            Assert.True(spuriousUpdateOnA is null,
                $"A forged voice ({forgedVoice}) must never reach upstream, but a session.update arrived: " +
                $"{spuriousUpdateOnA?.Json}");

            await guestA.CloseAsync(WebSocketCloseStatus.NormalClosure, "forged voice attempted, moving to next guest", ct);
            await guestA.WaitForCloseAsync(FrameTimeout, ct);
        }

        var firstClosed = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(firstClosed, "Expected guest A's upstream socket to close before starting the next guest.");

        var guestBConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var guestB = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var guestBConnection = await guestBConnectionTask;
        Assert.True(guestBConnection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        var guestBBootstrap = await guestBConnection!.ReceivedFrames.WaitForAsync(f => f.Sequence == 0, FrameTimeout, ct);
        Assert.True(guestBBootstrap is not null, "Bootstrap session.update never arrived for guest B.");
        var guestBVoice = guestBBootstrap!.Json.GetProperty("session").GetProperty("audio")
            .GetProperty("output").GetProperty("voice").GetString();
        Assert.NotEqual(forgedVoice, guestBVoice);
    });

    /// <summary>
    /// #43 (filed by Rick from this suite's PR #42 review, fixed in PR #49 review rounds 5-6):
    /// two concurrent guests share the same `voice_choice`, so picking a voice as guest A also
    /// changed guest B's in-flight conversation. Un-skipped now that #43 is fixed: `self.voice_choice`
    /// (the config-level default) is never mutated after construction, and a picker's choice is
    /// stored per session_id on <c>self._sessions</c> (never a field on <c>RTMiddleTier</c>) plus
    /// this connection's own local `voice` variable -- never re-read by an already-open,
    /// unrelated connection, and never consulted for any OTHER session's connection either (see
    /// <see cref="Voice_picked_after_lock_does_not_carry_to_a_brand_new_unrelated_connection"/>).
    /// </summary>
    [Fact]
    public Task Two_concurrent_guests_voice_choices_do_not_leak_into_each_other() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        var guestAConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var guestA = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var guestAConnection = await guestAConnectionTask;
        Assert.True(guestAConnection is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await guestA.SendStartSessionAsync(cancellationToken: ct);
        Assert.True(await guestA.ReceivedFrames.WaitForAsync(f => f.Type == "extension.round_trip_token", FrameTimeout, ct) is not null,
            "Guest A's greeting round trip never completed.");

        var guestBConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var guestB = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var guestBConnection = await guestBConnectionTask;
        Assert.True(guestBConnection is not null, $"No second upstream connection was accepted within {FrameTimeout}.");
        await guestB.SendStartSessionAsync(cancellationToken: ct);
        Assert.True(await guestB.ReceivedFrames.WaitForAsync(f => f.Type == "extension.round_trip_token", FrameTimeout, ct) is not null,
            "Guest B's greeting round trip never completed.");

        // Guest A picks a voice mid-conversation (locked, so it's deferred to A's own next
        // conversation) -- this must have NO effect on guest B, who is still active right now.
        await guestA.SendExtensionSetVoiceAsync("cedar", cancellationToken: ct);

        var watermarkB = guestBConnection!.ReceivedFrames.Snapshot().Count;
        var spuriousUpdateOnB = await guestBConnection.ReceivedFrames.WaitForAsync(
            f => f.Sequence >= watermarkB && f.Type == "session.update", TimeSpan.FromSeconds(2), ct);
        Assert.True(spuriousUpdateOnB is null,
            "Guest A's voice pick must not affect guest B's already-open connection.");
    });
}
