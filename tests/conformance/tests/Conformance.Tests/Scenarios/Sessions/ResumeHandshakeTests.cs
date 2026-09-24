using System.Diagnostics;
using System.Net.WebSockets;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Sessions;

/// <summary>
/// Issue #10: the `extension.resume` handshake itself. app/backend/rtmt.py only ever inspects
/// the *very first* client frame for `extension.resume` (first_frame_pending); once any frame —
/// resume or not — has been seen, the resume decision is locked in for the life of the socket.
/// A non-resume first frame (or the first_frame_timeout_seconds deadline, covered by
/// IdleTimeoutTests) decides "fresh" immediately and announces `extension.session_metadata`
/// with a fresh resumeId. A resume attempt is validated by session_manager.py's `resume()`:
/// malformed (wrong length), unknown (right length, never issued or already consumed), and
/// expired (covered by IdleTimeoutTests' boundary test) are all rejected with
/// `extension.resume_rejected {reason}` followed by a fresh re-announce; a *valid* resume is
/// single-use (the presented id is popped before a rotated one is issued) and, if the resumed
/// session is still attached to another socket, that socket is superseded with 4002.
/// </summary>
[Collection(ResumeTimersConformanceCollection.Name)]
public sealed class ResumeHandshakeTests(ResumeTimersConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    // Comfortably below ResumeTimers' first_frame_timeout_seconds=0.5s -- proves the metadata came
    // from the immediate "first frame decided non-resume" path in rtmt.py's from_client_to_server
    // (resume_decided.set() + announce_fresh() run inline, no sleep), not from first_frame_deadline's
    // 0.5s fallback timer.
    private static readonly TimeSpan WellUnderFirstFrameTimeout = TimeSpan.FromMilliseconds(400);

    private static string RandomResumeLookingId() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"); // 64 chars, in [32,128]

    [Fact]
    public Task A_non_resume_first_frame_decides_fresh_immediately_not_after_the_timeout() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        var stopwatch = Stopwatch.StartNew();
        await browser.SendStartSessionAsync(cancellationToken: ct);
        var metadata = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        stopwatch.Stop();

        Assert.True(metadata is not null, "Expected extension.session_metadata after a non-resume first frame.");
        Assert.True(stopwatch.Elapsed < WellUnderFirstFrameTimeout,
            $"Metadata took {stopwatch.Elapsed} -- expected the immediate first-frame decision path, " +
            $"not the {WellUnderFirstFrameTimeout} first-frame-timeout fallback.");
        var resumeId = metadata!.Json.GetProperty("resumeId").GetString();
        Assert.True(!string.IsNullOrEmpty(resumeId));
    });

    [Fact]
    public Task A_late_resume_attempt_is_rejected_and_the_session_continues() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        // First frame: not a resume, decides "fresh" for this socket.
        await browser.SendStartSessionAsync(cancellationToken: ct);
        var firstMetadata = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(firstMetadata is not null, "Expected extension.session_metadata after the first frame.");

        // Second frame: extension.resume is no longer eligible -- only the first frame is.
        await browser.SendExtensionResumeAsync(RandomResumeLookingId(), ct);
        var rejected = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.resume_rejected" && f.Sequence > firstMetadata.Sequence, FrameTimeout, ct);
        Assert.True(rejected is not null, "Expected extension.resume_rejected for a late (non-first-frame) resume attempt.");
        Assert.Equal("not_first_frame", rejected!.Json.GetProperty("reason").GetString());

        // The browser drops its stored id on any rejection, so the socket re-announces its own
        // (already-running) session with a freshly rotated id -- proof the session itself was
        // never torn down by the rejected resume attempt.
        var freshMetadata = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata" && f.Sequence > rejected.Sequence, FrameTimeout, ct);
        Assert.True(freshMetadata is not null, "Expected a fresh re-announce after the late-resume rejection.");
        var firstResumeId = firstMetadata.Json.GetProperty("resumeId").GetString();
        var freshResumeId = freshMetadata!.Json.GetProperty("resumeId").GetString();
        Assert.NotEqual(firstResumeId, freshResumeId);

        // The socket itself was never closed by the rejected attempt: no close observed shortly after.
        // (Not a Task.WhenAny of two identical 500ms timers -- ContinueWith runs regardless of
        // whether its antecedent ran to completion or faulted, so a WhenAny between "close-wait
        // times out" and "plain delay elapses" is itself a coin-flip race between two ~500ms
        // clocks, not a real check. WaitForCloseAsync's own TimeoutException already *is* the
        // "no close" signal, so catch it directly instead.)
        var closedQuickly = true;
        try
        {
            await browser.WaitForCloseAsync(TimeSpan.FromMilliseconds(500), ct);
        }
        catch (TimeoutException)
        {
            closedQuickly = false;
        }
        Assert.False(closedQuickly, "The session must continue normally after a rejected late resume, not close.");
    });

    [Fact]
    public Task A_malformed_resume_id_as_the_first_frame_is_rejected() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        // Well under session_manager.py's _RESUME_ID_MIN_LEN=32 -- rejected before any lookup.
        await browser.SendExtensionResumeAsync("too-short", ct);

        var rejected = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.resume_rejected", FrameTimeout, ct);
        Assert.True(rejected is not null, "Expected extension.resume_rejected for a malformed resume id.");
        Assert.Equal("malformed", rejected!.Json.GetProperty("reason").GetString());

        var freshMetadata = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata" && f.Sequence > rejected.Sequence, FrameTimeout, ct);
        Assert.True(freshMetadata is not null, "Expected fresh extension.session_metadata after the malformed resume rejection.");
    });

    [Fact]
    public Task An_unknown_resume_id_as_the_first_frame_is_rejected() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        // Right length (64 chars, inside [32,128]) but never issued by this (or any) backend run.
        await browser.SendExtensionResumeAsync(RandomResumeLookingId(), ct);

        var rejected = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.resume_rejected", FrameTimeout, ct);
        Assert.True(rejected is not null, "Expected extension.resume_rejected for an unknown resume id.");
        Assert.Equal("unknown", rejected!.Json.GetProperty("reason").GetString());

        var freshMetadata = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata" && f.Sequence > rejected.Sequence, FrameTimeout, ct);
        Assert.True(freshMetadata is not null, "Expected fresh extension.session_metadata after the unknown resume rejection.");
    });

    [Fact]
    public Task A_resume_id_is_single_use_a_second_attempt_with_the_same_id_is_unknown() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        var firstConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        Assert.True(await firstConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await browser.SendStartSessionAsync(cancellationToken: ct);
        var firstMetadata = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(firstMetadata is not null);
        var originalResumeId = firstMetadata!.Json.GetProperty("resumeId").GetString();

        // Drop without extension.end_session -- a genuine, resumable detach.
        await browser.CloseAsync(cancellationToken: ct);
        await browser.WaitForCloseAsync(FrameTimeout, ct);
        await browser.DisposeAsync();

        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var second = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        Assert.True(await secondConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await second.SendExtensionResumeAsync(originalResumeId!, ct);

        var resumed = await second.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_resumed", FrameTimeout, ct);
        Assert.True(resumed is not null, "Expected the first resume attempt (id never used before) to succeed.");

        // The presented id was popped before a rotated one was issued -- using the SAME
        // (now-consumed) original id again must be "unknown", never "expired" (which would
        // imply the id was still recognised as belonging to a session).
        var thirdConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var third = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        Assert.True(await thirdConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await third.SendExtensionResumeAsync(originalResumeId!, ct);

        var rejected = await third.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.resume_rejected", FrameTimeout, ct);
        Assert.True(rejected is not null, "Expected the second attempt with the already-consumed resume id to be rejected.");
        Assert.Equal("unknown", rejected!.Json.GetProperty("reason").GetString());
    });

    [Fact]
    public Task Resuming_from_a_still_attached_socket_supersedes_it_with_4002() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        var firstConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var first = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        Assert.True(await firstConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await first.SendStartSessionAsync(cancellationToken: ct);
        var metadata = await first.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(metadata is not null);
        var resumeId = metadata!.Json.GetProperty("resumeId").GetString();

        // `first` is deliberately left open (never closed) -- session_manager.py's resume() does
        // not require a detach: it only checks the digest and the idle-timeout window, so a
        // still-attached session's resume id is equally valid. This is the "steal" path.
        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var second = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        Assert.True(await secondConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await second.SendExtensionResumeAsync(resumeId!, ct);

        var resumed = await second.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_resumed", FrameTimeout, ct);
        Assert.True(resumed is not null, "Expected the still-attached session's resume id to be honoured.");

        // rtmt.py's handle_resume spawns _close_superseded(stale_ws) as a background task once
        // the resume completes -- the old socket's close is asynchronous, not synchronous with
        // the resume frame itself.
        await first.WaitForCloseAsync(FrameTimeout, ct);
        Assert.Equal((WebSocketCloseStatus)4002, first.CloseStatus);
        Assert.Equal("superseded", first.CloseStatusDescription);
    });

    [Fact]
    public Task Resume_id_never_appears_in_backend_diagnostics() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        Assert.True(await connectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await browser.SendStartSessionAsync(cancellationToken: ct);
        var metadata = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(metadata is not null);
        var resumeId = metadata!.Json.GetProperty("resumeId").GetString();
        Assert.True(!string.IsNullOrEmpty(resumeId));

        await browser.CloseAsync(cancellationToken: ct);
        await browser.WaitForCloseAsync(FrameTimeout, ct);
        await browser.DisposeAsync();

        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var second = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        Assert.True(await secondConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await second.SendExtensionResumeAsync(resumeId!, ct);
        var resumed = await second.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_resumed", FrameTimeout, ct);
        Assert.True(resumed is not null);

        // The harness's own client never puts the resume id anywhere but a JSON WebSocket frame
        // (SendExtensionResumeAsync) -- there is no HTTP endpoint or URL parameter for it anywhere
        // in the wire protocol, so "never in any URL" is true by construction. This asserts the
        // complementary half of session_manager.py's own contract ("the raw value goes to the
        // browser over the socket and nowhere else (never in URLs or logs)") against the backend's
        // own captured diagnostics/log output.
        var diagnostics = fixture.Backend!.DumpDiagnostics();
        Assert.DoesNotContain(resumeId!, diagnostics, StringComparison.Ordinal);
    });
}
