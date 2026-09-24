using System.Net.WebSockets;
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
        await browser.SendStartSessionAsync(cancellationToken: ct);
        var sentinelUpdate = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence >= watermark && f.Type == "session.update", FrameTimeout, ct);
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

        var firstConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using (var firstBrowser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct))
        {
            var firstConnection = await firstConnectionTask;
            Assert.True(firstConnection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

            await firstBrowser.SendStartSessionAsync(cancellationToken: ct);
            var roundTripToken = await firstBrowser.ReceivedFrames.WaitForAsync(
                f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
            Assert.True(roundTripToken is not null, "extension.round_trip_token never reached the browser (greeting never completed).");

            // Lock the connection (assistant audio already seen via the greeting above), then
            // pick a voice -- deferred: updates rtmt.py's process-wide voice_choice but sends
            // nothing upstream on this connection.
            await firstBrowser.SendExtensionSetVoiceAsync("cedar", cancellationToken: ct);

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
        Assert.Equal("cedar", pickedVoice);
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
    [Fact(Skip = "Known Python bug #43 (voice picker is process-wide across concurrent guests) -- not to be reproduced in the C# backend. Un-skip once #43 is fixed.")]
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
