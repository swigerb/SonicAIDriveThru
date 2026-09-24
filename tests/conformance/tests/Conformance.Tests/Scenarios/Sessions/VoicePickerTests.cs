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
