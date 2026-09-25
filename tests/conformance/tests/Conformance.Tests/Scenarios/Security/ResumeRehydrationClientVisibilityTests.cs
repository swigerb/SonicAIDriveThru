using System.Net.WebSockets;
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
/// `conversation.item.added` -> `conversation.item.done` echo, see PR #30 review "M2") acknowledges
/// every conversation item it's given by sending it back down the same socket at least twice --
/// which used to be relayed to the browser unmodified, putting the operator-only rehydration
/// briefing (guest transcript + order contents) in devtools. Fixed in
/// `RTMiddleTier._process_message_to_client`'s `conversation.item.created` /
/// `conversation.item.added` / `conversation.item.done` / `conversation.item.retrieved` cases (PR
/// #30 review "M1"): any item authored by the middle tier itself (identified primarily by its
/// `sonic_mt_`-prefixed `item.id`, with `role == "system"` kept as a second guard -- see #29
/// follow-up commit b32c656) is dropped rather than relayed, since the model itself never
/// originates one -- only `build_rehydration_item` / `build_nudge_item` do, and both send straight
/// to the upstream socket, never to the browser.
///
/// PR #30 review "S3": originally this waited a fixed 2s after seeing the rehydration item go
/// upstream and then inspected whatever had arrived -- a real gap Rick's review caught, because a
/// leak that took slightly longer than 2s to round-trip (or that only reached the browser via a
/// frame type the fixed-window snapshot never checked) could slip through undetected. Waiting
/// instead for a browser-visible frame that is *causally* guaranteed to follow the rehydration
/// item's full echo removes the race: after a mid-conversation resume, the only upstream traffic
/// on this connection is the rehydration item followed (after
/// <see cref="BackendProfiles.ResumeMargin"/>'s ~1s nudge timer) by the silence nudge item and its
/// `response.create` -- so the nudge's `response.created` reaching the browser cannot have
/// happened before the backend finished relaying (or correctly dropping) every
/// `conversation.item.*` frame the fake sent for both the rehydration and nudge items, on the same
/// single, order-preserving connection.
///
/// Runs under <see cref="BackendProfiles.ResumeMargin"/> (PR #52 CI follow-up) so the nudge timer
/// stays a fast ~1s (vs the 30s production default) while idle timeout and grace get real margin
/// (5s vs the 300s / 120s production defaults) above what this scenario's own pre-detach setup
/// (session establishment + greeting) should ever take, even on a loaded CI runner — see that
/// profile's doc comment for why the original, equal 1s/1s <see cref="BackendProfiles.ShortTimers"/>
/// raced CI run 36085091969's "holding order for 0s" detach.
/// </summary>
[Collection(ResumeMarginConformanceCollection.Name)]
public sealed class ResumeRehydrationClientVisibilityTests(ResumeMarginConformanceFixture fixture)
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

        // Wait for a browser-visible frame that is causally guaranteed to follow the rehydration
        // item's full upstream echo (PR #30 review "S3"), instead of a fixed-window sleep: the
        // only upstream traffic on a mid-conversation resume is the rehydration item, then (after
        // ResumeMargin's ~1s nudge timer) the silence nudge item plus its own response.create. The
        // nudge's response.created cannot reach the browser before the backend has already
        // forwarded-or-dropped every conversation.item.* frame the fake sent for both items, since
        // frames on a single connection are relayed in the order the backend's upstream reader
        // receives them.
        //
        // ResumeMargin's own idle timeout has real margin above the nudge timer (PR #52 CI
        // follow-up), unlike ShortTimers' original equal 1s/1s, but without any browser traffic
        // the idle sweep could still in principle race the nudge on an unusually slow runner
        // (rtmt.py's touch_activity resets on any non-audio-append client frame without cancelling
        // the pending nudge). Send an inert, already-false extension.set_verbose_logging as a
        // keepalive every 200ms while waiting, so the nudge always gets to fire.
        using var keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var keepAliveTask = Task.Run(async () =>
        {
            try
            {
                while (!keepAliveCts.IsCancellationRequested)
                {
                    await second.SendExtensionSetVerboseLoggingAsync(false, keepAliveCts.Token);
                    await Task.Delay(TimeSpan.FromMilliseconds(200), keepAliveCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected once the wait below cancels the keepalive loop.
            }
            catch (WebSocketException)
            {
                // PR #52 CI follow-up (swigerb/SonicAIDriveThru#28 N10 aftermath): this loop's
                // only job is best-effort activity to stop the idle sweep beating the nudge (see
                // the comment above) -- if the resumed socket is already gone (the backend
                // closed/aborted it, e.g. because even BackendProfiles.ResumeMargin's margin lost
                // a race on an unusually slow runner), there is nothing left to keep alive.
                // Swallowing it here lets the *real* assertions below (on nudgeResponseCreated,
                // on the leaked-item check) report what actually happened instead of this
                // unrelated, misleading send exception pre-empting them.
            }
        }, CancellationToken.None);

        var nudgeResponseCreated = await second.ReceivedFrames.WaitForAsync(
            f => f.Type == "response.created", FrameTimeout, ct);

        keepAliveCts.Cancel();
        await keepAliveTask;

        Assert.True(nudgeResponseCreated is not null,
            "Expected the silence nudge's response.created to reach the browser after the resume.");

        var leaked = second.ReceivedFrames.Snapshot().FirstOrDefault(f =>
            (f.Type == "conversation.item.created" || f.Type == "conversation.item.added" ||
             f.Type == "conversation.item.done" || f.Type == "conversation.item.retrieved") &&
            f.Json.TryGetProperty("item", out var item) &&
            item.TryGetProperty("role", out var role) &&
            role.GetString() == "system");
        Assert.True(leaked is null,
            $"The browser must never receive a server-authored system conversation item, but got: {leaked?.Json.GetRawText()}");
    });
}
