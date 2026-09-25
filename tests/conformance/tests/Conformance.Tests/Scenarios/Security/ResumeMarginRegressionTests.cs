using System.Net.WebSockets;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Security;

/// <summary>
/// PR #52 CI follow-up (swigerb/SonicAIDriveThru#28 N10 aftermath): CI's conformance job failed
/// twice on Linux, in two different resume scenarios (<see cref="ResumeRehydrationClientVisibilityTests"/>,
/// <see cref="WholeSessionLeakTests"/>), with "The remote party closed the WebSocket connection
/// without completing the close handshake." Root cause: both scenarios ran under
/// <see cref="BackendProfiles.ShortTimers"/> (idle_timeout=1s, grace=1s), and
/// <c>session_manager.py</c>'s <c>detached_expires_at = min(detached_at + grace_seconds,
/// last_activity + idle_timeout_seconds)</c> means a scenario whose own pre-detach setup (session
/// establishment, greeting, in <see cref="WholeSessionLeakTests"/>'s case also a voice change and a
/// tool round trip) takes close to or over that 1s under CI load leaves <c>last_activity + 1s</c>
/// already at/past "now" by the time the detach is processed -- exactly the backend log's
/// "holding order for 0s" -- so the resumed session has near-zero real margin before the periodic
/// sweep ends it, and the scenarios' own keepalive sends into that now-dead socket.
///
/// Reproducing the *exact* CI race (real wall-clock delay from thread-pool/scheduler contention on
/// a loaded Linux runner) inside a unit test proved impractical: 24 saturated background processes
/// and processor-affinity-constrained test runs on this dev machine both still completed the two
/// original scenarios in ~2s, nowhere near the 30s-61s the CI logs show, and neither forced the
/// race (see the PR #52 CI follow-up report for the full account of both attempts). This test
/// instead reproduces the *timing relationship* deterministically: it stands in for "the pre-detach
/// setup took a while on a loaded runner" with an explicit, fixed delay long enough to exceed
/// <see cref="BackendProfiles.ShortTimers"/>' 1s idle timeout but comfortably inside
/// <see cref="BackendProfiles.ResumeMargin"/>'s 5s one -- the same causal mechanism as the CI
/// failure, just driven by a controlled sleep instead of hoping a shared runner is slow enough on
/// the day the suite happens to run.
///
/// Mutation-check: temporarily switching this test's <c>[Collection]</c>/constructor from
/// <see cref="ResumeMarginConformanceCollection"/>/<see cref="ResumeMarginConformanceFixture"/> to
/// <see cref="ShortTimersConformanceCollection"/>/<see cref="ShortTimersConformanceFixture"/> (with
/// the delay below unchanged) reproduces the original failure: the assertion that the resumed
/// session survives 1s past the resume fails, because ShortTimers' equal 1s/1s idle/grace has
/// already elapsed by the time the second connection resumes. Restoring
/// <see cref="BackendProfiles.ResumeMargin"/> makes it pass reliably. See the PR #52 CI follow-up
/// report for the full revert/restore run log.
/// </summary>
[Collection(ResumeMarginConformanceCollection.Name)]
public sealed class ResumeMarginRegressionTests(ResumeMarginConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Stands in for the CI runs' slow pre-detach setup: comfortably longer than
    /// <see cref="BackendProfiles.ShortTimers"/>' 1s idle timeout (so the original bug's exact
    /// condition -- <c>last_activity + idle_timeout</c> already elapsed at detach -- reproduces
    /// under that profile), while staying well inside <see cref="BackendProfiles.ResumeMargin"/>'s
    /// 5s one.
    /// </summary>
    private static readonly TimeSpan SimulatedSlowPreDetachSetup = TimeSpan.FromMilliseconds(1500);

    [Fact]
    public Task Resumed_session_survives_a_slow_pre_detach_setup_with_real_margin() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        // ── First connection: start a conversation and let the greeting complete, exactly like
        // the resume scenarios this regression test is modelled on. ──
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

        // ── The reproduction: simulate a slow pre-detach setup on a loaded runner (see the class
        // doc comment) before dropping the first socket. ──
        await Task.Delay(SimulatedSlowPreDetachSetup, ct);

        await first.CloseAsync(cancellationToken: ct);
        await first.WaitForCloseAsync(FrameTimeout, ct);
        await first.DisposeAsync();

        // ── Resume on a fresh socket. ──
        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var second = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var secondConnection = await secondConnectionTask;
        Assert.True(secondConnection is not null, "No upstream connection was accepted for the resumed browser socket.");

        await second.SendExtensionResumeAsync(resumeId!, ct);

        var resumed = await second.ReceivedFrames.WaitForAsync(f => f.Type == "extension.session_resumed", FrameTimeout, ct);
        Assert.True(resumed is not null, "Expected extension.session_resumed on the resumed connection.");

        // ── The actual regression check: the resumed session must still be alive a full second
        // later. Under the original ShortTimers profile plus the delay above, the idle sweep would
        // already have ended the session by this point (detached_expires_at already elapsed at
        // detach time) and WaitForCloseAsync would observe that close well within this window --
        // that is the exact CI failure this test reproduces on demand. Under ResumeMargin, the
        // session must still be open, so WaitForCloseAsync times out instead. ──
        await Assert.ThrowsAsync<TimeoutException>(
            () => second.WaitForCloseAsync(TimeSpan.FromSeconds(1), ct));

        Assert.NotEqual(WebSocketState.Closed, second.SocketState);
        Assert.NotEqual(WebSocketState.Aborted, second.SocketState);
    });
}
