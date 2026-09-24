using System.Net.WebSockets;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Sessions;

/// <summary>
/// Issue #10: idle-close conformance. app/backend/session_manager.py closes an attached session
/// that has been idle beyond `idle_timeout_seconds` with code 4000/reason "idle_timeout"
/// (IDLE_CLOSE_CODE/IDLE_CLOSE_REASON), ends the session (order deleted, resume credential
/// invalidated) *before* the socket close so the close handler can never treat it as a resumable
/// detach, and the idle clock that gates a detached session's grace hold is
/// `min(detached_at + grace_seconds, last_activity + idle_timeout_seconds)` — driven by
/// last_activity, not by wall-clock time since the drop — so a drop close to the idle boundary
/// cannot buy a full fresh grace period. Uses the ShortTimers profile (idle_timeout=1s,
/// grace=1s, sweep_interval=0.2s) so these scenarios complete in seconds.
/// </summary>
[Collection(ShortTimersConformanceCollection.Name)]
public sealed class IdleTimeoutTests(ShortTimersConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Attached_session_is_closed_with_4000_after_the_idle_budget() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        // session.update is a non-append client frame, so it touches the idle clock (rtmt.py's
        // from_client_to_server: "if session_id and _MARKER_AUDIO_APPEND not in msg.data:
        // touch_activity") -- this is the last activity the idle checker will ever see for this
        // session, exactly like a guest who spoke once and then went quiet.
        await browser.SendStartSessionAsync(cancellationToken: ct);

        // ShortTimers' idle_timeout_seconds=1 plus up to one sweep_interval_seconds=0.2 pass
        // before the background _idle_check_loop notices -- 6s leaves generous headroom over the
        // ~1.2s this should actually take without ever approaching the production 300s default.
        await browser.WaitForCloseAsync(TimeSpan.FromSeconds(6), ct);
        Assert.Equal((WebSocketCloseStatus)4000, browser.CloseStatus);
        Assert.Equal("idle_timeout", browser.CloseStatusDescription);
    });

    [Fact]
    public Task Idle_closed_session_cannot_be_resumed_the_credential_is_gone() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        // No client frame is sent at all -- create_session() seeds last_activity at connect time,
        // so a bare connect with nothing afterward idles out on the same schedule as if a
        // session.update had been sent and never followed up. This also proves a session gets a
        // resume id purely from connecting (issue #10: "resumeId in extension.session_metadata"
        // is offered on the very first, otherwise-idle socket).
        var metadata = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(metadata is not null, "Expected extension.session_metadata on a fresh connect.");
        var resumeId = metadata!.Json.GetProperty("resumeId").GetString();
        Assert.True(!string.IsNullOrEmpty(resumeId), "extension.session_metadata carried no resumeId.");

        await browser.WaitForCloseAsync(TimeSpan.FromSeconds(6), ct);
        Assert.Equal((WebSocketCloseStatus)4000, browser.CloseStatus);
        await browser.DisposeAsync();

        // end_session() (called by close_idle_sessions before the 4000 close) pops both the
        // session's resume digest and its _resume_index entry -- an idle-closed session's
        // resume id must come back "unknown", the same as one that was never issued, never
        // "expired" (which implies the id was still recognised).
        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var second = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var secondConnection = await secondConnectionTask;
        Assert.True(secondConnection is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await second.SendExtensionResumeAsync(resumeId!, ct);

        var rejected = await second.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.resume_rejected", FrameTimeout, ct);
        Assert.True(rejected is not null, "Expected extension.resume_rejected for an idle-closed session's resume id.");
        Assert.Equal("unknown", rejected!.Json.GetProperty("reason").GetString());

        // A rejected resume still gets fresh metadata so the tab can start a new order (issue
        // #10: "wrong/expired/reused/malformed -> extension.resume_rejected {reason} + fresh
        // metadata").
        var freshMetadata = await second.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata" && f.Sequence > rejected.Sequence, FrameTimeout, ct);
        Assert.True(freshMetadata is not null, "Expected fresh extension.session_metadata after the rejected resume.");
    });

    [Fact]
    public Task A_drop_close_to_the_idle_boundary_does_not_buy_a_full_fresh_grace_period() => fixture.RunAsync(async () =>
    {
        // Named for the invariant under test (the idle clock isn't reset by a drop), not for the
        // exact rejection reason -- see the comment above the final assertion for why that
        // reason is "unknown" rather than the "expired" this test originally (and incorrectly)
        // expected before it was actually run against the real backend.
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        // t=0: this session.update is the last real activity the idle clock will ever see.
        await browser.SendStartSessionAsync(cancellationToken: ct);
        var metadata = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(metadata is not null, "Expected extension.session_metadata after connecting.");
        var resumeId = metadata!.Json.GetProperty("resumeId").GetString();
        Assert.True(!string.IsNullOrEmpty(resumeId));

        // t=0.6s: drop while still comfortably under idle_timeout_seconds=1 -- the attached-idle
        // sweep cannot have closed it yet, so this is a genuine detach, not a race with 4000. The
        // close code/reason the client sends here is irrelevant to the server: rtmt.py's
        // detach_session() runs unconditionally in the connection handler's finally block (see
        // rtmt.py "self._sessions.detach_session(ws, session_id, reason=f'client close
        // code={ws.close_code}')") regardless of which side closed first or what code was sent --
        // only an explicit extension.end_session *frame* takes the different, non-resumable path.
        await Task.Delay(TimeSpan.FromMilliseconds(600), ct);
        await browser.CloseAsync(cancellationToken: ct);
        await browser.WaitForCloseAsync(FrameTimeout, ct);
        await browser.DisposeAsync();

        // detached_expires_at = min(detached_at(0.6) + grace(1) = 1.6, last_activity(0) +
        // idle_timeout(1) = 1.0) = 1.0 -- only ~0.4s of hold past the drop, not the full 1s
        // grace_seconds a drop-resets-the-clock implementation would have granted. Waiting past
        // that 1.0s deadline before attempting a resume proves the idle clock (anchored to
        // last_activity, not to the drop) keeps running while detached, exactly as issue #10
        // requires ("a drop cannot extend it") -- but the *rejection reason* this observes is
        // "unknown", not "expired": ShortTimers' idle checker sweeps every
        // sweep_interval_seconds=0.2s and ends *any* session (attached or detached) whose
        // deadline has passed, popping its resume digest outright, same as
        // Idle_closed_session_cannot_be_resumed_the_credential_is_gone above. By the time a test
        // delays long enough past detached_expires_at to reliably attempt a resume (rather than
        // racing the exact deadline instant), that sweep has already run and already removed the
        // entry -- "expired" only exists in the narrow race between the deadline passing and the
        // next sweep tick, which isn't something a black-box test can reliably observe without
        // coupling itself to the sweep's own polling cadence.
        var elapsedSinceDrop = TimeSpan.FromMilliseconds(1350 - 600);
        await Task.Delay(elapsedSinceDrop, ct);

        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var second = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var secondConnection = await secondConnectionTask;
        Assert.True(secondConnection is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await second.SendExtensionResumeAsync(resumeId!, ct);

        var rejected = await second.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.resume_rejected", FrameTimeout, ct);
        Assert.True(rejected is not null,
            "Expected extension.resume_rejected once the idle clock (not the grace hold) expired.");
        Assert.Equal("unknown", rejected!.Json.GetProperty("reason").GetString());
    });
}
