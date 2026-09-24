using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Microsoft.Playwright;
using Xunit;

namespace Conformance.Tests.Scenarios.Browser;

/// <summary>
/// Issue #26: a .NET port of scripts/e2e_order_resume.py's real-browser resume scenarios,
/// against the real built frontend (served by the real Python backend under test) with our fake
/// realtime upstream (<see cref="ConformanceFixture.Realtime"/>, via <see
/// cref="BrowserConformanceFixture"/>) standing in for GA/OpenAI -- the same "real middle tier,
/// fake upstream" shape the Python script uses, just black-box (a separate OS process reached
/// only over HTTP/WebSocket) instead of in-process.
///
/// Two adaptations from the Python script, both forced by that black-box constraint (no
/// in-process access to session_manager.py's `_attached` dict or to a raw client socket the
/// harness itself opened):
///
///  - "drop 1011" is simulated by having the *page* call the browser's own WebSocket.close()
///    (no arguments -> close code 1000, since 1011 is a server-reserved code a page is not
///    permitted to pass to WebSocket.close and would throw InvalidAccessError) on the live
///    /realtime socket, instead of the server force-closing it with 1011. app/backend/rtmt.py's
///    detach_session() runs unconditionally in a `finally` block regardless of close code (see
///    Scenarios/Sessions/IdleTimeoutTests.cs's comment on the same point) and the frontend's
///    reconnect logic reacts to the socket closing at all, not to a specific code, so this
///    exercises the identical resumable-detach path the real 1011 drop would.
///  - "same session" is proven by the wire-level extension.session_metadata /
///    extension.session_resumed sessionToken matching before and after (captured client-side via
///    an init script that wraps window.WebSocket), rather than by reading
///    session_manager.py's internal `_attached` dict identity -- a black-box-only signal that is
///    if anything a *stronger* proof of "same session" than the Python script's in-process check.
///    Building a populated order via a mid-conversation tool call is deliberately not attempted
///    here: Conformance.Fakes.RealtimeScript.WithVadDefaults() stops at
///    transcription.completed and never auto-issues a follow-up response.create (by design, for
///    determinism -- see its own doc comment), and the real frontend never sends response.create
///    itself (only rtmt.py does, server-side, for the greeting and the nudge) -- so there is no
///    black-box-safe way to drive a *second* scripted turn purely through simulated mic audio.
///    The one turn that *is* reliably, deterministically triggered -- the server-driven greeting
///    -- is used instead to prove the fresh-vs-resumed no-greeting contract, which is what these
///    scenarios are actually about; a populated order ticket is not required to prove any of the
///    six behaviors this suite is scoped to (drop/reconnect identity, nudge-once, idle-no-
///    reconnect, reload-tap-to-continue, strict-autoplay, tap-while-reconnecting).
/// </summary>
[Collection(BrowserConformanceCollection.Name)]
[Trait("Category", "Browser")]
public sealed class OrderResumeBrowserTests(BrowserConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    // Wraps window.WebSocket so every /realtime socket's sent/received JSON frames are captured
    // for inspection from .NET, and exposes window.__dropLastSocket() to simulate a transport
    // drop from the page itself (see the class doc comment for why this replaces the Python
    // script's server-side sm._attached[sid].close(1011, ...) hack). Also captures an
    // AudioContext's state without any prior user gesture, mirroring the Python script's own
    // INIT_SCRIPT gesture probe for the strict-autoplay-policy scenario.
    private const string InitScript = """
    (() => {
      window.__sockets = [];
      const OrigWS = window.WebSocket;
      window.WebSocket = new Proxy(OrigWS, {
        construct(target, args) {
          const ws = new target(...args);
          if (String(args[0] ?? '').includes('/realtime')) {
            const rec = { url: String(args[0]), sent: [], received: [], ws };
            const origSend = ws.send.bind(ws);
            ws.send = (data) => {
              try { rec.sent.push(JSON.parse(data)); } catch { rec.sent.push({ type: '<binary>' }); }
              return origSend(data);
            };
            ws.addEventListener('message', (ev) => {
              try { rec.received.push(JSON.parse(ev.data)); } catch { rec.received.push({ type: '<binary>' }); }
            });
            window.__sockets.push(rec);
          }
          return ws;
        }
      });
      window.__dropLastSocket = () => {
        const rec = window.__sockets[window.__sockets.length - 1];
        if (rec) { rec.ws.close(); }
      };
      window.__probe = null;
      document.addEventListener('DOMContentLoaded', () => {
        const AC = window.AudioContext || window.webkitAudioContext;
        const ctx = new AC();
        setTimeout(() => { window.__probe = ctx.state; ctx.close(); }, 300);
      });
    })();
    """;

    private static Task<int> SocketCountAsync(IPage page) => page.EvaluateAsync<int>("window.__sockets.length");

    private static Task<JsonElement> ReceivedAsync(IPage page, int index) =>
        page.EvaluateAsync<JsonElement>($"window.__sockets[{index}] ? window.__sockets[{index}].received : []");

    private static Task<JsonElement> SentAsync(IPage page, int index) =>
        page.EvaluateAsync<JsonElement>($"window.__sockets[{index}] ? window.__sockets[{index}].sent : []");

    private static Task DropLastSocketAsync(IPage page) => page.EvaluateAsync<bool?>("window.__dropLastSocket()");

    private static string? SessionTokenOf(JsonElement frames, string type) =>
        frames.EnumerateArray()
            .Where(f => f.TryGetProperty("type", out var t) && t.GetString() == type)
            .Select(f => f.TryGetProperty("sessionToken", out var st) ? st.GetString()
                       : f.TryGetProperty("session_token", out var st2) ? st2.GetString() : null)
            .FirstOrDefault(v => v is not null);

    /// <summary>Polls <paramref name="probe"/> until <paramref name="ready"/> is satisfied or
    /// <paramref name="timeout"/> elapses -- the black-box equivalent of the Python script's own
    /// polling waits, since none of the state this suite reads (open socket count, captured
    /// frames, DOM text) is push-notified to a .NET caller.</summary>
    private static async Task<T> UntilAsync<T>(
        Func<Task<T>> probe, Func<T, bool> ready, TimeSpan timeout, string what, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            var value = await probe().ConfigureAwait(false);
            if (ready(value))
            {
                return value;
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out after {timeout} waiting for {what}.");
            }
            ct.ThrowIfCancellationRequested();
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    private static async Task<(IBrowserContext Context, IPage Page)> NewPageAsync(IBrowser browser, CancellationToken ct)
    {
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { Permissions = ["microphone"] })
            .ConfigureAwait(false);
        await context.AddInitScriptAsync(InitScript).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        return (context, page);
    }

    [Fact]
    public Task Drop_reconnects_with_the_same_session_no_greeting_the_mic_restarts_and_the_nudge_fires_once() =>
        fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (context, page) = await NewPageAsync(fixture.Browser!, ct);
        await using var _ = context;

        var firstConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await page.GotoAsync(fixture.Backend!.BaseUri.ToString()).ConfigureAwait(false);
        var firstConnection = await firstConnectionTask;
        Assert.True(firstConnection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        await page.GetByRole(AriaRole.Button, new() { Name = "Start recording", Exact = true }).ClickAsync().ConfigureAwait(false);
        await UntilAsync(() => SocketCountAsync(page), n => n >= 1, FrameTimeout, "the frontend's realtime socket to open", ct);
        var firstReceived = await UntilAsync(
            () => ReceivedAsync(page, 0),
            frames => SessionTokenOf(frames, "extension.session_metadata") is not null,
            FrameTimeout, "extension.session_metadata on the first socket", ct);
        var firstSessionToken = SessionTokenOf(firstReceived, "extension.session_metadata");
        Assert.True(!string.IsNullOrEmpty(firstSessionToken));

        await UntilAsync(
            () => Task.FromResult(firstConnection!.ReceivedFrames.Snapshot().Any(f => f.Type == "input_audio_buffer.append")),
            ok => ok, FrameTimeout, "mic audio reaching the first upstream connection", ct);

        // ── Drop, then the frontend's own reconnect logic should bring a second socket up ──
        var socketsBeforeDrop = await SocketCountAsync(page);
        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await DropLastSocketAsync(page).ConfigureAwait(false);
        await UntilAsync(() => SocketCountAsync(page), n => n > socketsBeforeDrop, FrameTimeout, "browser auto-reconnect", ct);
        var secondConnection = await secondConnectionTask;
        Assert.True(secondConnection is not null, $"No upstream reconnection was accepted within {FrameTimeout}.");

        // Opt out of WithVadDefaults' speech simulation on this connection before any resumed
        // audio arrives: the fake device's continuous append stream would otherwise have every
        // chunk answered with a synthetic speech_started/transcription pair, which touch_activity
        // (rtmt.py) and the nudge's cancel_nudge("guest speech") both treat exactly like real
        // guest speech -- and unlike touch_activity (which just needs to not starve the idle
        // clock), the nudge task is scheduled once per resume and never rescheduled once
        // cancelled, so a single stray transcription here would permanently and silently prevent
        // it from ever firing, well before we get to asserting it. This test only needs to prove
        // real appends *reach* the fake, not that the fake pretends the guest spoke.
        secondConnection!.Script.ClearRules();

        var secondSentFirstFrame = await UntilAsync(
            () => SentAsync(page, socketsBeforeDrop),
            sent => sent.GetArrayLength() > 0,
            FrameTimeout, "the reconnect socket to send its first frame", ct);
        Assert.Equal("extension.resume", secondSentFirstFrame[0].GetProperty("type").GetString());

        var secondReceived = await UntilAsync(
            () => ReceivedAsync(page, socketsBeforeDrop),
            frames => SessionTokenOf(frames, "extension.session_resumed") is not null,
            FrameTimeout, "extension.session_resumed on the reconnect socket", ct);
        var resumedSessionToken = SessionTokenOf(secondReceived, "extension.session_resumed");
        Assert.Equal(firstSessionToken, resumedSessionToken);

        await page.GetByText("Reconnected — your order is still here.", new() { Exact = true })
            .WaitForAsync(new() { Timeout = 5000 }).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Stop recording", Exact = true })
            .WaitForAsync(new() { Timeout = 5000 }).ConfigureAwait(false);

        // Mic auto-restarted with no tap: audio reaches the new upstream connection on its own.
        await UntilAsync(
            () => Task.FromResult(secondConnection!.ReceivedFrames.Snapshot().Any(f => f.Type == "input_audio_buffer.append")),
            ok => ok, FrameTimeout, "mic audio reaching the resumed upstream connection", ct);

        // Stop the mic before checking the nudge: FakeRealtimeConnection's default script
        // (RealtimeScript.WithVadDefaults(), applied to every connection by the harness) replies
        // to *every* input_audio_buffer.append with a synthetic speech_started/transcription pair
        // -- realistic for a single scripted "turn", but the fake device's continuous audio
        // stream would otherwise touch_activity/cancel_nudge on every chunk, exactly like real
        // guest speech would, and the nudge is specifically about the guest going quiet. Stopping
        // the mic here is the black-box equivalent of the guest actually falling silent.
        await page.GetByRole(AriaRole.Button, new() { Name = "Stop recording", Exact = true }).ClickAsync().ConfigureAwait(false);

        // Bootstrap session.update -> rehydration -> no response.create until the nudge.
        var bootstrap = await secondConnection!.ReceivedFrames.WaitForAsync(f => f.Type == "session.update", FrameTimeout, ct);
        Assert.True(bootstrap is not null, "Expected a bootstrap session.update on the resumed upstream connection.");
        var rehydration = await secondConnection.ReceivedFrames.WaitForAsync(
            f => f.Type == "conversation.item.create" &&
                 f.Json.TryGetProperty("item", out var item) &&
                 item.TryGetProperty("content", out var content) &&
                 content.EnumerateArray().Any(part =>
                     part.TryGetProperty("text", out var text) && (text.GetString() ?? "").Contains("Connection restored")),
            FrameTimeout, ct);
        Assert.True(rehydration is not null, "Expected the rehydration conversation.item.create on the resumed upstream connection.");
        Assert.True(rehydration!.Sequence > bootstrap!.Sequence);

        var prematureResponse = await secondConnection.ReceivedFrames.WaitForAsync(
            f => f.Type == "response.create" && f.Sequence > rehydration.Sequence, TimeSpan.FromMilliseconds(700), ct);
        Assert.True(prematureResponse is null, "No greeting should fire immediately after a resume.");

        // Nudge, exactly once, BrowserTimers' nudge_after_seconds=2s after the mic went quiet.
        var nudge = await secondConnection.ReceivedFrames.WaitForAsync(
            f => f.Type == "conversation.item.create" &&
                 f.Json.TryGetProperty("item", out var item) &&
                 item.TryGetProperty("content", out var content) &&
                 content.EnumerateArray().Any(part =>
                     part.TryGetProperty("text", out var text) && (text.GetString() ?? "").Contains("quiet since their connection")),
            TimeSpan.FromSeconds(6), ct);
        Assert.True(nudge is not null, "Expected exactly one nudge after nudge_after_seconds of silence.");
        var nudgeResponseCreate = await secondConnection.ReceivedFrames.WaitForAsync(
            f => f.Type == "response.create" && f.Sequence > nudge!.Sequence, FrameTimeout, ct);
        Assert.True(nudgeResponseCreate is not null, "Expected a response.create immediately following the nudge item.");
        var secondNudge = await secondConnection.ReceivedFrames.WaitForAsync(
            f => f.Type == "conversation.item.create" &&
                 f.Sequence > nudge!.Sequence &&
                 f.Json.TryGetProperty("item", out var item) &&
                 item.TryGetProperty("content", out var content) &&
                 content.EnumerateArray().Any(part =>
                     part.TryGetProperty("text", out var text) && (text.GetString() ?? "").Contains("quiet since their connection")),
            TimeSpan.FromSeconds(2), ct);
        Assert.True(secondNudge is null, "The nudge must fire at most once per resume.");
    });

    [Fact]
    public Task Idle_close_does_not_reconnect_a_tap_starts_a_fresh_session() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (context, page) = await NewPageAsync(fixture.Browser!, ct);
        await using var _ = context;

        var firstConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await page.GotoAsync(fixture.Backend!.BaseUri.ToString()).ConfigureAwait(false);
        var firstConnection = await firstConnectionTask;
        Assert.True(firstConnection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        // Opt out of WithVadDefaults' speech simulation before the mic ever starts: every
        // input_audio_buffer.append the fake receives would otherwise get an immediate synthetic
        // speech_started/transcription reply, and rtmt.py's from_server_to_client loop treats
        // that exactly like real guest speech -- touch_activity(session_id) on every one of them
        // (app/backend/rtmt.py's speech_started/transcription.completed handling). With real mic
        // audio streaming continuously once "Start recording" is clicked, that would keep
        // resetting the idle clock forever and the attached-idle sweep this scenario exists to
        // prove would never fire. Raw appends still reach the fake and are recorded either way.
        firstConnection!.Script.ClearRules();

        await page.GetByRole(AriaRole.Button, new() { Name = "Start recording", Exact = true }).ClickAsync().ConfigureAwait(false);
        await UntilAsync(() => SocketCountAsync(page), n => n >= 1, FrameTimeout, "the frontend's realtime socket to open", ct);
        await page.GetByText("Session ended after inactivity. Tap the mic to start a new order.", new() { Exact = true })
            .WaitForAsync(new() { Timeout = 14000 }).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(2), ct); // would a background reconnect happen anyway?
        Assert.Equal(1, await SocketCountAsync(page));

        var freshConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await page.GetByRole(AriaRole.Button, new() { Name = "Start recording", Exact = true }).ClickAsync().ConfigureAwait(false);
        var freshConnection = await freshConnectionTask;
        Assert.True(freshConnection is not null, $"Expected a fresh upstream connection after the tap within {FrameTimeout}.");

        await UntilAsync(() => SocketCountAsync(page), n => n == 2, FrameTimeout, "a second (fresh) realtime socket after the tap", ct);
        var freshSent = await UntilAsync(() => SentAsync(page, 1), sent => sent.GetArrayLength() > 0,
            FrameTimeout, "the fresh socket to send its first frame", ct);
        Assert.NotEqual("extension.resume", freshSent[0].GetProperty("type").GetString());

        // A genuinely fresh session still greets -- unlike a resume, which stays silent.
        var greeting = await freshConnection!.ReceivedFrames.WaitForAsync(f => f.Type == "response.create", FrameTimeout, ct);
        Assert.True(greeting is not null, "Expected the fresh session to greet.");
    });

    [Fact]
    public Task Reloading_the_page_restores_the_session_and_waits_for_a_tap_to_continue() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (context, page) = await NewPageAsync(fixture.Browser!, ct);
        await using var _ = context;

        var firstConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await page.GotoAsync(fixture.Backend!.BaseUri.ToString()).ConfigureAwait(false);
        Assert.True(await firstConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Start recording", Exact = true }).ClickAsync().ConfigureAwait(false);
        await UntilAsync(() => SocketCountAsync(page), n => n >= 1, FrameTimeout, "the frontend's realtime socket to open", ct);
        var firstReceived = await UntilAsync(
            () => ReceivedAsync(page, 0),
            frames => SessionTokenOf(frames, "extension.session_metadata") is not null,
            FrameTimeout, "extension.session_metadata on the first socket", ct);
        var firstSessionToken = SessionTokenOf(firstReceived, "extension.session_metadata");
        Assert.True(!string.IsNullOrEmpty(firstSessionToken));

        // Reload in the same tab -- the resume id survives in sessionStorage (unlike a fresh tab
        // or a closed browser context), and the reload wipes the page's own mic/audio refs, so
        // the frontend needs a fresh gesture to restart the mic even though the session resumes.
        //
        // Note: unlike a raw socket drop (see Drop_reconnects, which reuses the same document),
        // a full page navigation tears down window.__sockets entirely -- the init script re-runs
        // from scratch on the new document and the array restarts at index 0. So the post-reload
        // socket is always window.__sockets[0] in the *new* page, not
        // window.__sockets[<pre-reload count>]; indexing by the pre-reload count would read past
        // the end of the freshly-reset array and never see any frames.
        var reloadConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await page.ReloadAsync().ConfigureAwait(false);
        var reloadConnection = await reloadConnectionTask;
        Assert.True(reloadConnection is not null, $"No upstream connection was accepted after reload within {FrameTimeout}.");
        await UntilAsync(() => SocketCountAsync(page), n => n >= 1, FrameTimeout, "the post-reload realtime socket to open", ct);

        var reloadReceived = await UntilAsync(
            () => ReceivedAsync(page, 0),
            frames => SessionTokenOf(frames, "extension.session_resumed") is not null,
            FrameTimeout, "extension.session_resumed on the post-reload socket", ct);
        var reloadSessionToken = SessionTokenOf(reloadReceived, "extension.session_resumed");
        Assert.Equal(firstSessionToken, reloadSessionToken);

        // Not asserting App.tsx's "Reconnected -- your order is still here. Tap the mic to
        // continue." notice text here: it only renders when the rehydrated order_summary has
        // items (App.tsx's onReceivedSessionResumed), and per this class's own doc comment,
        // populating an order via a mid-conversation tool call is deliberately out of scope for
        // these mic-driven scenarios. The button staying "Start recording" (mic did NOT
        // auto-restart) is what "waits for a tap to continue" actually means here, and is
        // asserted below, immediately before the tap.
        await page.GetByRole(AriaRole.Button, new() { Name = "Start recording", Exact = true })
            .WaitForAsync(new() { Timeout = 3000 }).ConfigureAwait(false);

        await page.GetByRole(AriaRole.Button, new() { Name = "Start recording", Exact = true }).ClickAsync().ConfigureAwait(false);
        await UntilAsync(
            () => Task.FromResult(reloadConnection!.ReceivedFrames.Snapshot().Any(f => f.Type == "input_audio_buffer.append")),
            ok => ok, FrameTimeout, "mic audio reaching the resumed (post-reload) upstream connection", ct);

        var reloadResponseCreate = await reloadConnection!.ReceivedFrames.WaitForAsync(
            f => f.Type == "response.create", TimeSpan.FromMilliseconds(800), ct);
        Assert.True(reloadResponseCreate is null, "No greeting should fire after a resume, including one after a reload.");
    });

    [Fact]
    public Task Tapping_the_mic_while_reconnecting_still_sends_resume_as_the_literal_first_frame() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (context, page) = await NewPageAsync(fixture.Browser!, ct);
        await using var _ = context;

        var firstConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await page.GotoAsync(fixture.Backend!.BaseUri.ToString()).ConfigureAwait(false);
        Assert.True(await firstConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Start recording", Exact = true }).ClickAsync().ConfigureAwait(false);
        await UntilAsync(() => SocketCountAsync(page), n => n >= 1, FrameTimeout, "the frontend's realtime socket to open", ct);

        // Wait for extension.session_metadata (carrying the resumeId) to round-trip before
        // dropping: without this, the drop can race ahead of the frontend ever learning it has a
        // resume id to use, and the "reconnect" socket falls back to a plain fresh session.update
        // instead of extension.resume -- a race this test must not have, since it exists
        // specifically to test a *different* race (the guest's tap vs. the reconnect socket
        // opening), not this one.
        await UntilAsync(
            () => ReceivedAsync(page, 0),
            frames => SessionTokenOf(frames, "extension.session_metadata") is not null,
            FrameTimeout, "extension.session_metadata on the first socket", ct);

        var socketsBeforeDrop = await SocketCountAsync(page);
        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await DropLastSocketAsync(page).ConfigureAwait(false);

        // Tap immediately -- before the reconnect socket has necessarily even opened -- exactly
        // like the Python script's "tap while reconnecting" scenario. The tap must not race
        // ahead of the queued resume: extension.resume is still the literal first frame the new
        // socket sends.
        await page.GetByRole(AriaRole.Button, new() { Name = "Start recording", Exact = true }).ClickAsync().ConfigureAwait(false);

        await UntilAsync(() => SocketCountAsync(page), n => n > socketsBeforeDrop, FrameTimeout, "browser auto-reconnect", ct);
        Assert.True(await secondConnectionTask is not null, $"No upstream reconnection was accepted within {FrameTimeout}.");
        var sent = await UntilAsync(() => SentAsync(page, socketsBeforeDrop), s => s.GetArrayLength() > 0,
            FrameTimeout, "the reconnect socket to send its first frame", ct);
        Assert.Equal("extension.resume", sent[0].GetProperty("type").GetString());
    });

    [Fact]
    public async Task Strict_autoplay_policy_still_auto_restarts_the_mic_after_a_drop_but_not_after_a_reload()
    {
        var ct = TestContext.Current.CancellationToken;
        if (fixture.Browser is null)
        {
            // fixture.RunAsync itself would skip cleanly on the same condition, but this
            // scenario needs its own separately-launched browser (extra launch args) before
            // that gate would even run -- check the same underlying condition directly first.
            Assert.Skip(BrowserChannelPolicy.BuildMessage(fixture.BrowserChannel, CiEnvironment.IsCi));
            return;
        }

        await fixture.RunAsync(async () =>
        {
            var strictBrowser = await fixture.Playwright!.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Channel = fixture.BrowserChannel,
                Headless = true,
                Args =
                [
                    "--use-fake-ui-for-media-stream",
                    "--use-fake-device-for-media-stream",
                    "--autoplay-policy=document-user-activation-required",
                ],
            }).ConfigureAwait(false);
            try
            {
                var (context, page) = await NewPageAsync(strictBrowser, ct);
                await using var _ = context;

                var firstConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
                await page.GotoAsync(fixture.Backend!.BaseUri.ToString()).ConfigureAwait(false);
                Assert.True(await firstConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");

                var noGestureState = await UntilAsync(() => page.EvaluateAsync<string?>("window.__probe"),
                    s => s is not null, FrameTimeout, "the gesture probe's AudioContext state", ct);
                Assert.Equal("suspended", noGestureState);

                await page.GetByRole(AriaRole.Button, new() { Name = "Start recording", Exact = true }).ClickAsync().ConfigureAwait(false);
                await UntilAsync(() => SocketCountAsync(page), n => n >= 1, FrameTimeout, "the frontend's realtime socket to open", ct);

                // Wait for extension.session_metadata (the resumeId) to round-trip before
                // dropping -- otherwise the drop can race ahead of the frontend ever learning it
                // has a resume id, and the reconnect falls back to a brand new session instead of
                // resuming (see Tapping_the_mic_while_reconnecting's identical fix), which would
                // make the "no new gesture" mic-auto-restart assertion below meaningless.
                await UntilAsync(
                    () => ReceivedAsync(page, 0),
                    frames => SessionTokenOf(frames, "extension.session_metadata") is not null,
                    FrameTimeout, "extension.session_metadata on the first socket", ct);

                var socketsBeforeDrop = await SocketCountAsync(page);
                var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
                await DropLastSocketAsync(page).ConfigureAwait(false);
                await UntilAsync(() => SocketCountAsync(page), n => n > socketsBeforeDrop, FrameTimeout, "browser auto-reconnect", ct);
                var secondConnection = await secondConnectionTask;
                Assert.True(secondConnection is not null, $"No upstream reconnection was accepted within {FrameTimeout}.");

                // The AudioContext created on the very first tap keeps running across the drop
                // (no new gesture happened) -- the mic must still auto-restart on the resume.
                await UntilAsync(
                    () => Task.FromResult(secondConnection!.ReceivedFrames.Snapshot().Any(f => f.Type == "input_audio_buffer.append")),
                    ok => ok, FrameTimeout, "mic audio reaching the resumed upstream connection with no new gesture", ct);

                // A reload, though, drops the page's own AudioContext/mic refs entirely -- even
                // under this same strict policy, the mic must stay off until a fresh tap.
                var reloadConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
                await page.ReloadAsync().ConfigureAwait(false);
                var reloadConnection = await reloadConnectionTask;
                Assert.True(reloadConnection is not null, $"No upstream connection was accepted after reload within {FrameTimeout}.");
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                Assert.DoesNotContain(reloadConnection!.ReceivedFrames.Snapshot(), f => f.Type == "input_audio_buffer.append");
                await page.GetByRole(AriaRole.Button, new() { Name = "Start recording", Exact = true }).ClickAsync().ConfigureAwait(false);
                await UntilAsync(
                    () => Task.FromResult(reloadConnection!.ReceivedFrames.Snapshot().Any(f => f.Type == "input_audio_buffer.append")),
                    ok => ok, FrameTimeout, "mic audio reaching the post-reload upstream connection after a fresh tap", ct);
            }
            finally
            {
                await strictBrowser.CloseAsync().ConfigureAwait(false);
            }
        });
    }
}
