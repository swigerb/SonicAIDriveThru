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
    /// M5 ("voice picked after the lock is thrown away -- next conversation doesn't get it") and
    /// M6 ("bootstrap hard-codes 'alloy'"): the voice picked (and deferred) on a locked connection
    /// must actually apply to the *next* conversation's bootstrap, not just get silently dropped.
    /// Closes the first connection gracefully (CloseAsync + WaitForCloseAsync, the same
    /// drain-guarantee idiom as BrowserClientLifecycleTests) before opening the second, so the
    /// backend has fully processed the picker message and there is no ambiguity about which
    /// connection's bootstrap is being inspected.
    /// </summary>
    [Fact]
    public Task Voice_picked_after_lock_carries_to_the_next_conversation() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        // Rick's PR #42 review (M5 blocker): every other active test in this collection (which
        // shares one backend process/voice_choice -- see VoicePickerConformanceFixture's docs)
        // also picks "cedar", so with M5 applied (the picked voice discarded, never carried to
        // the next conversation) the bootstrap below could still show "cedar" purely because an
        // earlier test in the run already left the process-wide voice_choice at "cedar" -- not
        // because *this* test's own pick carried over. Picking a voice nothing else in this
        // collection ever picks, and asserting the FIRST connection's own bootstrap voice is
        // something else beforehand, closes that gap: only this test's own set_voice call can
        // explain the second connection's bootstrap voice matching the target below.
        const string targetVoice = "verse";

        var firstConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using (var firstBrowser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct))
        {
            var firstConnection = await firstConnectionTask;
            Assert.True(firstConnection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

            var firstBootstrap = await firstConnection!.ReceivedFrames.WaitForAsync(f => f.Sequence == 0, FrameTimeout, ct);
            Assert.True(firstBootstrap is not null, "Bootstrap session.update never arrived on the first connection.");
            var voiceBeforePick = firstBootstrap!.Json.GetProperty("session").GetProperty("audio")
                .GetProperty("output").GetProperty("voice").GetString();
            Assert.NotEqual(targetVoice, voiceBeforePick);

            await firstBrowser.SendStartSessionAsync(cancellationToken: ct);
            var roundTripToken = await firstBrowser.ReceivedFrames.WaitForAsync(
                f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
            Assert.True(roundTripToken is not null, "extension.round_trip_token never reached the browser (greeting never completed).");

            // Lock the connection (assistant audio already seen via the greeting above), then
            // pick a voice -- deferred: updates rtmt.py's process-wide voice_choice but sends
            // nothing upstream on this connection.
            await firstBrowser.SendExtensionSetVoiceAsync(targetVoice, cancellationToken: ct);

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
        Assert.True(bootstrap is not null, "Bootstrap session.update never arrived on the next conversation.");

        var pickedVoice = bootstrap!.Json.GetProperty("session").GetProperty("audio")
            .GetProperty("output").GetProperty("voice").GetString();
        Assert.Equal(targetVoice, pickedVoice);
    });

    /// <summary>
    /// The Python bug behind the process-wide leak (issue #43, filed by Rick from this suite's
    /// PR #42 review): two concurrent guests share the same `voice_choice`, so picking a voice as
    /// guest A also changes guest B's in-flight conversation. This is real, reproducible behaviour
    /// of app/backend/rtmt.py today -- not a fake/harness defect -- so it is written and Skipped
    /// here rather than fixed: this conformance suite documents rtmt.py's behaviour (including its
    /// bugs) for the C# backend to reproduce faithfully, and #43 explicitly is NOT something the
    /// C# backend should copy. Un-skip once #43 is fixed.
    /// </summary>
    [Fact(Skip = "Known Python bug #43 (voice picker is process-wide across concurrent guests) -- not to be reproduced in the C# backend. Un-skip once #43 is fixed.",
        SkipWhen = nameof(BackendUnderTest.IsPython), SkipType = typeof(BackendUnderTest))]
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
