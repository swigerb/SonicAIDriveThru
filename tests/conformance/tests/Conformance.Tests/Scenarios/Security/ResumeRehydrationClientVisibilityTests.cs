using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Security;

/// <summary>
/// swigerb/SonicAIDriveThru#29 (part 2, "server-authored system items reach the browser"): a
/// resume that finds a conversation already in progress briefs the fresh upstream connection with
/// a `role: "system"` conversation item (`build_rehydration_item` -- recent transcript + order
/// JSON, session_manager.py) *before* letting the model speak. GA (and this fake, via
/// <see cref="RealtimeScript.WithVadDefaults"/>'s built-in `conversation.item.create` ->
/// `conversation.item.added` echo) acknowledges every conversation item it's given by sending it
/// straight back down the same socket -- which used to be relayed to the browser unmodified,
/// putting the operator-only rehydration briefing (guest transcript + order contents) in
/// devtools. Fixed in `RTMiddleTier._process_message_to_client`'s
/// `conversation.item.created`/`conversation.item.added` case: any item with `role == "system"`
/// is dropped rather than relayed, since the model itself never originates one -- only
/// `build_rehydration_item` / `build_nudge_item` do, and both send straight to the upstream
/// socket, never to the browser.
///
/// Runs under <see cref="BackendProfiles.ShortTimers"/> so the resume grace hold (1s here vs the
/// 120s production default) fits in a fast test.
/// </summary>
[Collection(ShortTimersConformanceCollection.Name)]
public sealed class ResumeRehydrationClientVisibilityTests(ShortTimersConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Browser_never_receives_the_resume_rehydration_system_item() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        // ── First connection: start a conversation and let the greeting complete, so the
        // session is marked "conversation started" (session_manager.py's has_sent_greeting) --
        // the precondition for a later resume to rehydrate rather than just re-greet. ──
        var firstConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        var first = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var firstConnection = await firstConnectionTask;
        Assert.True(firstConnection is not null, "No upstream connection was accepted for the first browser socket.");

        await first.SendStartSessionAsync(cancellationToken: ct);

        var metadata = await first.ReceivedFrames.WaitForAsync(f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(metadata is not null, "Expected extension.session_metadata on the first connection.");
        var resumeId = metadata!.Json.GetProperty("resumeId").GetString();
        Assert.False(string.IsNullOrEmpty(resumeId), "extension.session_metadata must carry a non-empty resumeId.");

        var greetingDone = await first.ReceivedFrames.WaitForAsync(f => f.Type == "response.done", FrameTimeout, ct);
        Assert.True(greetingDone is not null, "Expected the greeting's response.done on the first connection.");

        // ── Drop the first socket; the session detaches and is held for the (shortened) grace
        // period rather than ending immediately. ──
        await first.CloseAsync(cancellationToken: ct);
        await first.WaitForCloseAsync(FrameTimeout, ct);
        await first.DisposeAsync();

        // ── Resume on a fresh socket within the grace hold. ──
        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var second = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var secondConnection = await secondConnectionTask;
        Assert.True(secondConnection is not null, "No upstream connection was accepted for the resumed browser socket.");

        await second.SendExtensionResumeAsync(resumeId!, ct);

        var resumed = await second.ReceivedFrames.WaitForAsync(f => f.Type == "extension.session_resumed", FrameTimeout, ct);
        Assert.True(resumed is not null, "Expected extension.session_resumed on the resumed connection.");

        // Precondition: the fake upstream really did get sent (and, via its default script,
        // echo back) a role="system" conversation item -- otherwise this scenario would prove
        // nothing about the drop.
        var upstreamSystemItem = await secondConnection!.ReceivedFrames.WaitForAsync(
            f => f.Type == "conversation.item.create" &&
                 f.Json.TryGetProperty("item", out var item) &&
                 item.TryGetProperty("role", out var role) &&
                 role.GetString() == "system",
            FrameTimeout, ct);
        Assert.True(upstreamSystemItem is not null,
            "Precondition failed: the backend never sent the rehydration item upstream on the resumed connection.");

        // Give the fake's auto-echo (conversation.item.create -> conversation.item.added, see
        // RealtimeScript.WithVadDefaults) time to round-trip back through the backend, then make
        // sure none of it reached the browser.
        await secondConnection.ReceivedFrames.WaitForAsync(
            f => f.Sequence > upstreamSystemItem!.Sequence, TimeSpan.FromSeconds(2), ct);

        var leaked = second.ReceivedFrames.Snapshot().FirstOrDefault(f =>
            (f.Type == "conversation.item.created" || f.Type == "conversation.item.added") &&
            f.Json.TryGetProperty("item", out var item) &&
            item.TryGetProperty("role", out var role) &&
            role.GetString() == "system");
        Assert.True(leaked is null,
            $"The browser must never receive a server-authored system conversation item, but got: {leaked?.Json.GetRawText()}");
    });
}
