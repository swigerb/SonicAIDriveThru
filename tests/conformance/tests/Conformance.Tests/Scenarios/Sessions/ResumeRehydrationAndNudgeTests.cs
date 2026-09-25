using System.Net.WebSockets;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Sessions;

/// <summary>
/// Issue #10: what happens to the *conversation* across a resume, not just the handshake
/// (covered by <see cref="ResumeHandshakeTests"/>). app/backend/rtmt.py sends every new upstream
/// connection a bootstrap `session.update` before relaying any client traffic; a successful
/// mid-conversation resume (`outcome.conversation_started` — the session had already greeted)
/// then appends exactly one system `conversation.item.create` carrying the order and recent
/// turns (session_manager.py's `build_rehydration_item`), and deliberately sends no
/// `response.create` — the carhop must not greet again or speak until the guest does. A
/// `nudge_after_silence()` task fires exactly once, `nudge_after_seconds` after the resume, but
/// only once `session.updated` has confirmed the new upstream is configured, and only if the
/// guest hasn't spoken (speech/transcript cancels it). `extension.end_session` is the one path
/// that permanently deletes the order and the resume credential — 1000/"session_ended", after
/// which the same resume id comes back "unknown", not "expired".
/// </summary>
[Collection(ResumeTimersConformanceCollection.Name)]
public sealed class ResumeRehydrationAndNudgeTests(ResumeTimersConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// PR #54 review ("black-box couplings"): the rehydration item and the nudge item are both
    /// a plain system-role conversation.item.create -- rather than matching either on its
    /// hard-coded English prose (session_manager.py's own text, which a conformant C# backend
    /// has no obligation to reproduce word-for-word), identify them structurally by
    /// type=="message" + role=="system". Both this suite's own send_greeting_once call site and
    /// a future browser turn use role=="user" (see session_manager.py's own comment on
    /// MIDDLE_TIER_ITEM_ID_PREFIX: "as the greeting, role='user', already did"), so role=="system"
    /// unambiguously means "rehydration or nudge" in this resumed-connection flow, and their
    /// relative order (rehydration always first, the nudge -- if any -- always second) tells
    /// them apart from each other.
    /// </summary>
    private static bool IsSystemMessageItem(RecordedFrame f) =>
        f.Type == "conversation.item.create" &&
        f.Json.TryGetProperty("item", out var item) &&
        item.TryGetProperty("type", out var itemType) && itemType.GetString() == "message" &&
        item.TryGetProperty("role", out var role) && role.GetString() == "system";

    private async Task<(RealtimeBrowserClient Browser, FakeRealtimeConnection Connection, string ResumeId)>
        ConnectPastGreetingWithResumeIdAsync(CancellationToken ct)
    {
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        await browser.SendStartSessionAsync(cancellationToken: ct);
        var metadata = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(metadata is not null, "Expected extension.session_metadata after connecting.");
        var resumeId = metadata!.Json.GetProperty("resumeId").GetString();
        Assert.True(!string.IsNullOrEmpty(resumeId));

        // Past the greeting turn (extension.round_trip_token) so the session is marked as having
        // greeted -- resume()'s `conversation_started` (and thus rehydration-with-no-greeting,
        // rather than a plain fresh greeting) depends on that.
        var greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");

        return (browser, connection!, resumeId!);
    }

    private async Task<(RealtimeBrowserClient Browser, FakeRealtimeConnection Connection)> DropAndResumeAsync(
        RealtimeBrowserClient oldBrowser, string resumeId, CancellationToken ct)
    {
        await oldBrowser.CloseAsync(cancellationToken: ct);
        await oldBrowser.WaitForCloseAsync(FrameTimeout, ct);
        await oldBrowser.DisposeAsync();

        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await browser.SendExtensionResumeAsync(resumeId, ct);
        return (browser, connection!);
    }

    /// <summary>
    /// PR #52 review ("F1"): the Wi-Fi-blip path -- no Close frame ever crosses the wire, unlike
    /// every other resume scenario in this file (which all go through <see cref="DropAndResumeAsync"/>'s
    /// graceful <see cref="RealtimeBrowserClient.CloseAsync"/>). This exercises a genuinely
    /// different code path on the backend: app/backend/rtmt.py's `async for msg in ws:` loop
    /// ends via a receive fault/EOF rather than an orderly CLOSE-type message, so `ws.close_code`
    /// is still None by the time `_forward_messages`'s own `finally:` block runs
    /// `detach_session(...)` -- the same unconditional detach path every other close (graceful or
    /// not) already goes through, per that finally block's own comment. This helper (and its
    /// asserted-null <see cref="RealtimeBrowserClient.CloseStatus"/> below) is the proof that the
    /// scenario actually took that different path rather than silently degrading to a graceful
    /// close.
    /// </summary>
    private async Task<(RealtimeBrowserClient Browser, FakeRealtimeConnection Connection)> AbortAndResumeAsync(
        RealtimeBrowserClient oldBrowser, string resumeId, CancellationToken ct)
    {
        oldBrowser.Abort();
        await oldBrowser.WaitForCloseAsync(FrameTimeout, ct);
        Assert.True(oldBrowser.CloseStatus is null,
            "An abrupt abort must not produce a Close frame -- CloseStatus should stay null, " +
            "otherwise this scenario isn't actually exercising the abnormal-close code path.");
        await oldBrowser.DisposeAsync();

        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await browser.SendExtensionResumeAsync(resumeId, ct);
        return (browser, connection!);
    }

    [Fact]
    public Task Resuming_mid_conversation_rehydrates_the_order_with_no_greeting() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (oldBrowser, oldConnection, resumeId) = await ConnectPastGreetingWithResumeIdAsync(ct);

        // "size" is a required argument the real tool schema enforces (tools.py:
        // update_order_tool_schema's "required": [..., "size", ...]) -- update_order() does a
        // bare `args["size"]` with no default, so omitting it raises an unhandled KeyError that
        // silently aborts the tool call before extension.middle_tier_tool_response is ever sent.
        const string callId = "call_resume_rehydrate_1";
        oldConnection.Script.Enqueue(new ResponseScript([
            new FunctionCallEvent(
                Name: "update_order",
                ArgumentsJson: """{"action":"add","item_name":"Bacon Cheeseburger","size":"N/A","quantity":1,"price":6.99}""",
                CallId: callId),
            new DoneEvent(),
        ]));
        await oldBrowser.SendResponseCreateAsync(ct);
        var toolResponse = await oldBrowser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.middle_tier_tool_response", FrameTimeout, ct);
        Assert.True(toolResponse is not null, "Expected the update_order tool call to complete before dropping.");

        var (newBrowser, newConnection) = await DropAndResumeAsync(oldBrowser, resumeId, ct);
        await using var _ = newBrowser;

        var resumed = await newBrowser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_resumed", FrameTimeout, ct);
        Assert.True(resumed is not null, "Expected extension.session_resumed after a valid mid-conversation resume.");
        var orderSummaryJson = resumed!.Json.GetProperty("order_summary").GetRawText();
        Assert.Contains("Bacon Cheeseburger", orderSummaryJson);

        // Upstream ordering: bootstrap session.update (sent before any client traffic is
        // relayed, on every new connection) then the rehydration conversation.item.create -- and
        // no response.create at all in between or shortly after; the carhop stays silent until
        // the guest speaks or the nudge fires (covered separately below).
        var bootstrap = await newConnection.ReceivedFrames.WaitForAsync(
            f => f.Type == "session.update", FrameTimeout, ct);
        Assert.True(bootstrap is not null, "Expected a bootstrap session.update on the new upstream connection.");

        var rehydration = await newConnection.ReceivedFrames.WaitForAsync(
            f => IsSystemMessageItem(f) && f.Sequence > bootstrap!.Sequence,
            FrameTimeout, ct);
        Assert.True(rehydration is not null,
            "Expected the rehydration conversation.item.create (system-role message) on the new upstream connection.");
        Assert.True(rehydration!.Sequence > bootstrap!.Sequence,
            "The rehydration item must follow the bootstrap session.update.");

        // PR #54 review: a fixed "nothing arrives within 700ms" wall-clock window is fragile
        // under load -- measured, the check started about 1ms after rehydration, leaving only
        // about 300ms of slack, and WaitForAsync's own timer can fire late enough under
        // concurrent load to stretch the window into the (correct) 1s-later nudge, producing a
        // spurious pass or, worse, an unreliable kill. Instead, wait for the nudge item to
        // actually arrive (it structurally always follows the rehydration item -- see
        // IsSystemMessageItem's doc comment), then assert nothing between the two is a
        // response.create. This is deterministic regardless of how long the wait itself took,
        // and still kills the greeting-on-resume mutation (a reintroduced greeting sends a
        // response.create in exactly this window).
        var nudge = await newConnection.ReceivedFrames.WaitForAsync(
            f => IsSystemMessageItem(f) && f.Sequence > rehydration!.Sequence,
            TimeSpan.FromSeconds(6), ct); // ResumeTimers' nudge_after_seconds=1s, generous headroom
        Assert.True(nudge is not null, "Expected the nudge to eventually fire so the window has a deterministic end.");

        var prematureResponse = newConnection.ReceivedFrames.Snapshot()
            .FirstOrDefault(f => f.Type == "response.create" &&
                                  f.Sequence > rehydration!.Sequence && f.Sequence < nudge!.Sequence);
        Assert.True(prematureResponse is null,
            "No response.create (greeting or otherwise) should fire between the rehydration item and the nudge.");
    });

    /// <summary>
    /// PR #52 review ("F1"): every other resume scenario in this file drops the old connection
    /// gracefully (<see cref="DropAndResumeAsync"/>). A real Wi-Fi blip never sends a Close
    /// frame at all -- <see cref="AbortAndResumeAsync"/> reproduces that with
    /// <see cref="RealtimeBrowserClient.Abort"/>, which severs the socket the way
    /// <c>ClientWebSocket.Abort()</c> does (no close handshake, in-flight receive faults). This
    /// otherwise mirrors <see cref="Resuming_mid_conversation_rehydrates_the_order_with_no_greeting"/>
    /// exactly -- same order-restore, same no-greeting, same rehydration-upstream assertions --
    /// because app/backend/rtmt.py's `_forward_messages` finally block runs
    /// `detach_session(...)` unconditionally, regardless of whether `ws.close_code` was ever set.
    /// The mutation for this scenario (see the report) proves that unconditional detach actually
    /// matters: making the backend treat a close-code-less (abrupt) disconnect as a hard
    /// `end_session` instead turns this red.
    /// </summary>
    [Fact]
    public Task Resuming_after_an_abrupt_abort_with_no_close_frame_restores_the_order_with_no_greeting() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (oldBrowser, oldConnection, resumeId) = await ConnectPastGreetingWithResumeIdAsync(ct);

        const string callId = "call_resume_abort_1";
        oldConnection.Script.Enqueue(new ResponseScript([
            new FunctionCallEvent(
                Name: "update_order",
                ArgumentsJson: """{"action":"add","item_name":"Bacon Cheeseburger","size":"N/A","quantity":1,"price":6.99}""",
                CallId: callId),
            new DoneEvent(),
        ]));
        await oldBrowser.SendResponseCreateAsync(ct);
        var toolResponse = await oldBrowser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.middle_tier_tool_response", FrameTimeout, ct);
        Assert.True(toolResponse is not null, "Expected the update_order tool call to complete before aborting.");

        var (newBrowser, newConnection) = await AbortAndResumeAsync(oldBrowser, resumeId, ct);
        await using var _ = newBrowser;

        var resumed = await newBrowser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_resumed", FrameTimeout, ct);
        Assert.True(resumed is not null, "Expected extension.session_resumed after resuming from an abrupt abort within grace.");
        var orderSummaryJson = resumed!.Json.GetProperty("order_summary").GetRawText();
        Assert.Contains("Bacon Cheeseburger", orderSummaryJson);

        var bootstrap = await newConnection.ReceivedFrames.WaitForAsync(
            f => f.Type == "session.update", FrameTimeout, ct);
        Assert.True(bootstrap is not null, "Expected a bootstrap session.update on the new upstream connection.");

        var rehydration = await newConnection.ReceivedFrames.WaitForAsync(
            f => IsSystemMessageItem(f) && f.Sequence > bootstrap!.Sequence,
            FrameTimeout, ct);
        Assert.True(rehydration is not null,
            "Expected the rehydration conversation.item.create (system-role message) on the new upstream connection.");
        Assert.True(rehydration!.Sequence > bootstrap!.Sequence,
            "The rehydration item must follow the bootstrap session.update.");

        var nudge = await newConnection.ReceivedFrames.WaitForAsync(
            f => IsSystemMessageItem(f) && f.Sequence > rehydration!.Sequence,
            TimeSpan.FromSeconds(6), ct); // ResumeTimers' nudge_after_seconds=1s, generous headroom
        Assert.True(nudge is not null, "Expected the nudge to eventually fire so the window has a deterministic end.");

        var prematureResponse = newConnection.ReceivedFrames.Snapshot()
            .FirstOrDefault(f => f.Type == "response.create" &&
                                  f.Sequence > rehydration!.Sequence && f.Sequence < nudge!.Sequence);
        Assert.True(prematureResponse is null,
            "No response.create (greeting or otherwise) should fire between the rehydration item and the nudge.");
    });

    [Fact]
    public Task A_silent_guest_gets_nudged_exactly_once_after_the_resume() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (oldBrowser, _, resumeId) = await ConnectPastGreetingWithResumeIdAsync(ct);
        var (newBrowser, newConnection) = await DropAndResumeAsync(oldBrowser, resumeId, ct);
        await using var _ = newBrowser;

        var resumed = await newBrowser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_resumed", FrameTimeout, ct);
        Assert.True(resumed is not null);

        // The rehydration item (see IsSystemMessageItem's doc comment) is always sent first, on
        // every resume -- the nudge, if any, is structurally the *next* system-message item
        // after it, not simply "the first one seen" (that would be the rehydration item itself).
        var rehydration = await newConnection.ReceivedFrames.WaitForAsync(
            f => IsSystemMessageItem(f), FrameTimeout, ct);
        Assert.True(rehydration is not null, "Expected the rehydration item before the nudge can be observed.");

        // ResumeTimers' nudge_after_seconds=1 -- allow generous headroom over that before
        // concluding the nudge never fired.
        var nudge = await newConnection.ReceivedFrames.WaitForAsync(
            f => IsSystemMessageItem(f) && f.Sequence > rehydration!.Sequence, TimeSpan.FromSeconds(6), ct);
        Assert.True(nudge is not null, "Expected exactly one nudge conversation.item.create after nudge_after_seconds of silence.");

        var nudgeResponseCreate = await newConnection.ReceivedFrames.WaitForAsync(
            f => f.Type == "response.create" && f.Sequence > nudge!.Sequence, FrameTimeout, ct);
        Assert.True(nudgeResponseCreate is not null, "Expected a response.create immediately following the nudge item.");

        // Exactly once: no second nudge item ever follows, even after waiting well past another
        // full nudge_after_seconds interval.
        var secondNudge = await newConnection.ReceivedFrames.WaitForAsync(
            f => IsSystemMessageItem(f) && f.Sequence > nudge!.Sequence,
            TimeSpan.FromSeconds(2), ct);
        Assert.True(secondNudge is null, "The nudge must fire at most once per resume.");
    });

    [Fact]
    public Task The_nudge_is_cancelled_by_guest_speech() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (oldBrowser, _, resumeId) = await ConnectPastGreetingWithResumeIdAsync(ct);
        var (newBrowser, newConnection) = await DropAndResumeAsync(oldBrowser, resumeId, ct);
        await using var _ = newBrowser;

        var resumed = await newBrowser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_resumed", FrameTimeout, ct);
        Assert.True(resumed is not null);

        // Well before ResumeTimers' nudge_after_seconds=1s elapses, the guest "speaks" -- the
        // fake's VAD-default script replies to input_audio_buffer.append with speech_started,
        // which rtmt.py's from_client_to_server relays down to this same connection and uses to
        // cancel any pending nudge (cancel_nudge("guest speech")).
        var rehydration = await newConnection.ReceivedFrames.WaitForAsync(
            f => IsSystemMessageItem(f), FrameTimeout, ct);
        Assert.True(rehydration is not null, "Expected the rehydration item before a nudge could be observed.");

        await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        await newBrowser.SendInputAudioAppendAsync("dGVzdA==", ct);

        var nudge = await newConnection.ReceivedFrames.WaitForAsync(
            f => IsSystemMessageItem(f) && f.Sequence > rehydration!.Sequence,
            TimeSpan.FromSeconds(3), ct); // comfortably past the 1s nudge_after_seconds it would have fired at
        Assert.True(nudge is null, "Guest speech before the nudge deadline must cancel it.");
    });

    [Fact]
    public Task The_nudge_never_fires_without_session_updated_confirming_the_resumed_connection() => fixture.RunAsync(async () =>
    {
        // PR #54 review: a mutation removing rtmt.py's nudge_after_silence() `await
        // session_configured.wait()` line previously survived, because nothing in this suite
        // ever withheld session.updated on the resumed connection to actually exercise that gate
        // -- every other scenario's fake upstream acknowledges session.update immediately, so the
        // gate was always a no-op wait. Arm SuppressSessionUpdatedOnNextConnection() (the same
        // one-shot switch the greeting-timeout-fallback path uses) for the *resumed* connection:
        // the fake still validates/merges the bootstrap session.update as normal but never sends
        // back session.updated, so session_configured is never set for this connection's
        // lifetime. Unlike the greeting (which has its own _SESSION_CONFIGURED_TIMEOUT_SEC
        // fallback and fires anyway with a warning), the nudge has no such fallback: it must wait
        // forever. The rehydration item itself is NOT gated on session.updated (rtmt.py sends it
        // synchronously right after the resume decision, before nudge_after_silence() is even
        // scheduled), so we still expect exactly one system-message item -- just never a second.
        var ct = TestContext.Current.CancellationToken;
        var (oldBrowser, _, resumeId) = await ConnectPastGreetingWithResumeIdAsync(ct);

        fixture.Realtime.SuppressSessionUpdatedOnNextConnection();
        var (newBrowser, newConnection) = await DropAndResumeAsync(oldBrowser, resumeId, ct);
        await using var _ = newBrowser;

        var resumed = await newBrowser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_resumed", FrameTimeout, ct);
        Assert.True(resumed is not null);

        var rehydration = await newConnection.ReceivedFrames.WaitForAsync(
            f => IsSystemMessageItem(f), FrameTimeout, ct);
        Assert.True(rehydration is not null,
            "The rehydration item is sent unconditionally on resume, regardless of session.updated.");

        // Confirm the suppression actually took effect -- without this, a harness regression in
        // SuppressSessionUpdatedOnNextConnection itself could silently turn this into a
        // no-op test that passes for the wrong reason (session.updated arrives normally, the
        // gate is satisfied quickly, and "no second item" would look identical to "gate held").
        var sessionUpdated = await newConnection.ReceivedFrames.WaitForAsync(
            f => f.Type == "session.updated", TimeSpan.FromSeconds(2), ct);
        Assert.True(sessionUpdated is null,
            "Test setup error: session.updated was NOT suppressed on the resumed connection.");

        // Well past ResumeTimers' nudge_after_seconds=1s -- if the session.updated gate were
        // bypassed, the nudge would already have fired by now.
        var nudge = await newConnection.ReceivedFrames.WaitForAsync(
            f => IsSystemMessageItem(f) && f.Sequence > rehydration!.Sequence,
            TimeSpan.FromSeconds(5), ct);
        Assert.True(nudge is null,
            "The nudge must not fire while session.updated has never confirmed the resumed connection is configured.");
    });

    [Fact]
    public Task Ending_the_session_closes_with_1000_and_the_order_and_credential_are_gone() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, _, resumeId) = await ConnectPastGreetingWithResumeIdAsync(ct);

        await browser.SendExtensionEndSessionAsync(ct);
        await browser.WaitForCloseAsync(FrameTimeout, ct);
        Assert.Equal((WebSocketCloseStatus)1000, browser.CloseStatus);
        Assert.Equal("session_ended", browser.CloseStatusDescription);
        await browser.DisposeAsync();

        // end_session() deletes the order and pops both the resume digest and its index entry --
        // the id from before end_session must come back "unknown" (the credential itself is
        // gone), never "expired" (which would imply the session was merely held in grace).
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var second = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        Assert.True(await connectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await second.SendExtensionResumeAsync(resumeId, ct);

        var rejected = await second.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.resume_rejected", FrameTimeout, ct);
        Assert.True(rejected is not null, "Expected extension.resume_rejected for an ended session's resume id.");
        Assert.Equal("unknown", rejected!.Json.GetProperty("reason").GetString());
    });
}
