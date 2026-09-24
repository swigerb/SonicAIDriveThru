using System.Net.WebSockets;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// #8: the three application-range WebSocket close codes app/backend/session_manager.py defines
/// -- 4000 idle timeout, 4002 superseded-by-resume, and the standard 1000 for a guest-initiated
/// end_session -- each with its own fixed reason string. Only the close-code *shape* is asserted
/// here; idle *timing* precision (whether it fires at exactly idle_timeout_seconds) belongs to
/// issue #10. The 4000 case still needs the ShortTimers profile's shortened
/// CONFORMANCE_IDLE_TIMEOUT_SECONDS to complete in a reasonable time, since a black-box test has
/// no other way to reach an idle close at all without actually waiting out the timeout.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class CloseCodeTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Guest_initiated_end_session_closes_with_1000_session_ended() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);

        // A real browser always sends session.update as its first frame -- establish the session
        // normally (waiting for session.updated to confirm the backend has left its
        // first-frame-pending resume-decision window) before ending it, exactly like a guest who
        // opens the app and then immediately backs out.
        await browser.SendStartSessionAsync(cancellationToken: ct);
        var updated = await browser.ReceivedFrames.WaitForAsync(f => f.Type == "session.updated", FrameTimeout, ct);
        Assert.True(updated is not null, "session.updated never reached the browser after session.update.");

        await browser.SendExtensionEndSessionAsync(cancellationToken: ct);
        await browser.WaitForCloseAsync(FrameTimeout, ct);

        Assert.Equal(WebSocketCloseStatus.NormalClosure, browser.CloseStatus); // 1000
        Assert.Equal("session_ended", browser.CloseStatusDescription);
    });

    /// <summary>
    /// Resuming a session while its original socket is still attached (not merely dropped)
    /// supersedes it -- app/backend/session_manager.py's `resume()` hands back the still-attached
    /// socket as `stale_ws`, and app/backend/rtmt.py's `_close_superseded` closes it with 4002.
    /// `resume.enabled: true` and `first_frame_timeout_seconds: 2.0` are config.yaml defaults, so
    /// this needs no ShortTimers/extra env at all -- the plain Default profile already supports it.
    /// </summary>
    [Fact]
    public Task Resuming_a_still_attached_session_supersedes_the_original_socket_with_4002() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browserA = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);

        // A's very first client frame -- not a resume -- makes the resume decision "fresh", which
        // (once the upstream session.created fires, near-instant against the fake) announces
        // extension.session_metadata with a resumeId.
        await browserA.SendStartSessionAsync(cancellationToken: ct);
        var metadata = await browserA.ReceivedFrames.WaitForAsync(f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(metadata is not null, "Expected extension.session_metadata (with a resumeId) after A's first frame.");
        var resumeId = metadata!.Json.GetProperty("resumeId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(resumeId), "Expected a non-empty resumeId -- resume must be enabled for this scenario to mean anything.");

        // A stays open/attached (no close, no drop) -- B now resumes the SAME session while A is
        // still live, which is exactly what triggers the supersede-close, not an ordinary
        // resume-after-drop.
        await using var browserB = await RealtimeBrowserClient.ConnectAsync(fixture.Backend.BaseUri, cancellationToken: ct);
        await browserB.SendExtensionResumeAsync(resumeId!, cancellationToken: ct);

        var resumed = await browserB.ReceivedFrames.WaitForAsync(f => f.Type == "extension.session_resumed", FrameTimeout, ct);
        Assert.True(resumed is not null, "Expected extension.session_resumed on B once the resume was accepted.");

        await browserA.WaitForCloseAsync(FrameTimeout, ct);
        Assert.Equal((WebSocketCloseStatus)4002, browserA.CloseStatus);
        Assert.Equal("superseded", browserA.CloseStatusDescription);
    });
}

/// <summary>See <see cref="CloseCodeTests"/> -- the 4000 idle case needs its own ShortTimers
/// collection/backend process purely to make the wait short, per issue #8's note that idle
/// *timing* precision belongs to #10.</summary>
[Collection(ShortTimersConformanceCollection.Name)]
public sealed class IdleCloseCodeTests(ShortTimersConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Idle_connection_closes_with_4000_idle_timeout() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);

        // Deliberately sends nothing at all -- ShortTimers' CONFORMANCE_IDLE_TIMEOUT_SECONDS=1
        // (sweep interval 0.2s) means the idle checker closes this within a couple of seconds,
        // well inside FrameTimeout, with no client activity to drive the idle clock.
        await browser.WaitForCloseAsync(FrameTimeout, ct);

        Assert.Equal((WebSocketCloseStatus)4000, browser.CloseStatus);
        Assert.Equal("idle_timeout", browser.CloseStatusDescription);
    });
}
