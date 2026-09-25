using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Xunit;
using Conformance.Harness;

namespace Conformance.Tests;

/// <summary>
/// PR #22 review item 11: the browser client should support a real graceful close, not just a
/// disposal-time best-effort teardown, so tests can assert on how the backend answers a
/// client-initiated close.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class BrowserClientLifecycleTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Graceful_close_is_answered_by_the_backend_and_observed_via_WaitForCloseAsync() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        await browser.SendStartSessionAsync(cancellationToken: ct);

        // Let the connection go fully live (greeting round trip complete) before tearing it down,
        // so the close we're observing is a real client-initiated close, not just an early abort
        // of a connection that never finished handshaking application-level state.
        var greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");

        Assert.Null(browser.CloseStatus);

        await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, "test requested close", ct);
        await browser.WaitForCloseAsync(FrameTimeout, ct);

        // aiohttp's WebSocketResponse auto-answers an incoming close frame with a matching close
        // frame (autoclose, the default) — rtmt.py's from_client_to_server loop just breaks on
        // WSMsgType.CLOSE without sending anything itself, so this assertion is really exercising
        // aiohttp's own protocol-level behavior, not app code.
        Assert.Equal(WebSocketCloseStatus.NormalClosure, browser.CloseStatus);
    });

    /// <summary>
    /// Runs the graceful-close-and-observe-CloseStatus scenario 50× back to back against the same
    /// long-lived backend, to shake out the platform-dependent Close-frame-observation race fixed
    /// after CI run 35936172326 failed once on Linux (never reproduced on Windows in a single run).
    /// Each iteration opens its own connection so a flaky iteration fails with its own index instead
    /// of a single opaque assertion, and the loop keeps running past the first failure so a single
    /// flaky iteration doesn't hide a second, unrelated one.
    /// </summary>
    [Fact]
    public Task Graceful_close_observes_CloseStatus_reliably_across_50_connections() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var failures = new List<string>();

        for (var i = 0; i < 50; i++)
        {
            await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
            await browser.SendStartSessionAsync(cancellationToken: ct);

            var greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
                f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
            if (greetingRoundTrip is null)
            {
                failures.Add($"iteration {i}: greeting round trip never completed.");
                continue;
            }

            await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, "stress test close", ct);
            await browser.WaitForCloseAsync(FrameTimeout, ct);

            if (browser.CloseStatus != WebSocketCloseStatus.NormalClosure)
            {
                failures.Add($"iteration {i}: expected NormalClosure, observed {browser.CloseStatus?.ToString() ?? "null"}.");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count}/50 iterations failed:\n{string.Join('\n', failures)}");
    });

    /// <summary>
    /// #28 N10: <see cref="RealtimeBrowserClient.DisposeAsync"/> used to cancel the reader loop's
    /// in-flight receive *before* attempting any close, which drives a <see cref="ClientWebSocket"/>
    /// straight to <see cref="WebSocketState.Aborted"/> — so relying on plain disposal for teardown
    /// (rather than an explicit <see cref="RealtimeBrowserClient.CloseAsync"/> +
    /// <see cref="RealtimeBrowserClient.WaitForCloseAsync"/>, as the two tests above do) always
    /// produced that same abrupt-drop signature, whether or not the scenario cared.
    /// <para/>
    /// Deliberately does <em>not</em> run against the real backend (<see cref="fixture"/>): a real
    /// backend answers a close near-instantly, which races the reader loop's own cancellation —
    /// mutation-testing this against it caught a reverted fix only 3-4 times out of 5 (confirmed
    /// while writing this test). <see cref="DelayedCloseFakeBackend"/> instead answers the client's
    /// Close frame after a fixed, small delay comfortably below <c>DisposeAsync</c>'s grace period
    /// but comfortably above how fast a local cancellation resolves, so the outcome depends on
    /// which the fix does first (close, then wait) rather than on OS-level scheduling. Never calls
    /// <see cref="RealtimeBrowserClient.CloseAsync"/> here — the whole point is to exercise plain
    /// disposal's own default.
    /// </summary>
    [Fact]
    public async Task Plain_disposal_defaults_to_a_graceful_close_not_an_abort()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fakeBackend = new DelayedCloseFakeBackend(closeReplyDelay: TimeSpan.FromMilliseconds(300));
        var browser = await RealtimeBrowserClient.ConnectAsync(fakeBackend.BaseUri, cancellationToken: ct);

        await browser.DisposeAsync();

        Assert.NotEqual(WebSocketState.Aborted, browser.SocketState);
    }

    /// <summary>
    /// #28 N10: the explicit escape hatch for a scenario whose own subject matter genuinely is an
    /// abrupt, no-close-frame disconnect — companion to the graceful-default test above, proving
    /// <see cref="RealtimeBrowserClient.AbortAsync"/> still reaches the abrupt-drop state that
    /// plain disposal no longer produces automatically. <see cref="WebSocket.Abort"/> is a
    /// synchronous, deterministic state transition (unlike the reader-loop-cancellation race the
    /// test above deliberately avoids), so this one can safely run against the real backend.
    /// </summary>
    [Fact]
    public Task AbortAsync_still_reaches_the_aborted_socket_state() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        await browser.SendStartSessionAsync(cancellationToken: ct);

        var greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");

        await browser.AbortAsync();

        Assert.Equal(WebSocketState.Aborted, browser.SocketState);

        await browser.DisposeAsync();
    });

    /// <summary>
    /// PR #52 CI follow-up (swigerb/SonicAIDriveThru#28 N10 aftermath, CI runs 36085091969): CI's
    /// conformance job failed twice on Linux with "The remote party closed the WebSocket
    /// connection without completing the close handshake" out of a resume scenario. The immediate
    /// cause was the keepalive loop's unprotected send (see the two scenario files' own fix), but
    /// while hardening <see cref="RealtimeBrowserClient.CloseAsync"/> to match
    /// <see cref="RealtimeBrowserClient.DisposeAsync"/>'s existing best-effort-close contract (part
    /// 2 of the follow-up ask), this test was written to prove the same class of exception can't
    /// escape <c>CloseAsync</c> either, for a peer that disappears -- no close frame, no answer at
    /// all -- via <see cref="AbruptPeerFakeBackend"/>.
    ///
    /// Round 1 mutation-check note (superseded below): reverting the
    /// <c>try/catch (WebSocketException)</c> around <c>CloseAsync</c>'s <c>CloseOutputAsync</c>
    /// call did not turn this test red *alone* on this machine, even under injected CPU pressure
    /// (24 saturated background processes) -- the background reader loop's own in-flight
    /// <c>ReceiveAsync</c> reliably won the race and self-transitioned <c>_socket.State</c> to
    /// <see cref="WebSocketState.Aborted"/> before <c>CloseAsync</c>'s state check ran, so the
    /// guard skipped the send entirely.
    ///
    /// Round 2 (confirmed, not hypothetical): after commit 3c03ab4 was pushed, this exact test
    /// failed 4/4 full-suite runs on the same machine (passing 3/3 run alone), proving the race
    /// genuinely does go the other way under real contention -- alone, this process has the CPU
    /// to itself and the reader loop wins every time; under full-suite load, the scheduler
    /// sometimes lets <c>CloseOutputAsync</c>'s send reach the (already-reset) socket first. When
    /// it does, <c>ManagedWebSocket</c> throws <see cref="OperationCanceledException"/> wrapping
    /// an <see cref="IOException"/>/<see cref="System.Net.Sockets.SocketException"/> -- not
    /// <see cref="WebSocketException"/> -- so the original catch clause alone missed it. Fixed by
    /// also catching <c>OperationCanceledException</c> when the caller's own token wasn't what
    /// requested it (see <c>CloseAsync</c>'s doc comment). Mutation-check: reverting that second
    /// catch clause and running the *full suite* (not this test alone) reliably reproduces the
    /// failure again; restoring it passes 5/5 full-suite runs. See the PR #52 CI follow-up report
    /// for both run logs -- this is a rare case where the mutation-check needed the full suite's
    /// contention to be meaningful, and the honest round-1 "can't force it alone" note above is
    /// preserved rather than deleted, since it was true and is why round 2 was necessary.
    ///
    /// Round 3 (isolation attempt, itself superseded): a *deterministic* per-test reproduction
    /// briefly lived in a separate <c>ThreadPoolStarvationCloseTests</c> class/collection, forcing
    /// genuine thread-pool starvation (<c>ThreadPool.SetMinThreads(1, 1)</c> plus occupying every
    /// worker with blocking tasks) instead of relying on full-suite contention. It was isolated
    /// into its own <c>DisableParallelization</c> collection so its process-wide
    /// <c>ThreadPool.SetMinThreads</c> call couldn't perturb other collections' timing while xUnit
    /// runs collections in parallel.
    ///
    /// Round 4 (root-caused and replaced): that isolation fix (commit 422b6e1) still crashed CI
    /// run 36091977281 with a native "Stack overflow." while the isolated collection was running
    /// alone. Root cause: the occupier-task count was <c>ThreadPool.GetMaxThreads()</c>'s worker
    /// ceiling times 2 -- and that ceiling defaults to <c>32767</c> on both this machine and the
    /// CI runner (confirmed by probing <c>ThreadPool.GetMaxThreads</c> directly), so every run of
    /// either starvation test spawned <b>65,534</b> real, dedicated (<c>LongRunning</c>) OS
    /// threads. That is inherently unsafe on *any* machine, not just Linux specifically -- it just
    /// happened to survive (at a cost of ~35 extra seconds of thread creation/teardown overhead)
    /// on this 24-core/large-memory workstation, while the CI runner's tighter resource limits hit
    /// a genuine allocation failure partway through spawning them. That failure then needed to be
    /// reported (the runtime tries to build a stack trace for it via <c>Exception.ToString()</c>,
    /// which itself needs to allocate/initialize more runtime state via
    /// <c>RuntimeType.InitializeCache()</c>), which failed again for the same reason, recursing
    /// through the CLR's own exception-dispatch machinery until the native stack was exhausted --
    /// exactly the <c>RhThrowEx</c>/<c>DispatchEx</c>/<c>Exception.ToString()</c> recursion in the
    /// CI trace. This is not a fixable *bound* on the occupier count (any thread-pool-starvation
    /// technique's safety margin is inherently platform- and load-dependent); it is replaced
    /// entirely below by <see cref="RealtimeBrowserClient.CreateForTesting"/>, a deterministic,
    /// zero-thread, zero-timing unit-level seam that pins the exact fault-handling behaviour
    /// directly instead of trying to reproduce it via real scheduling contention.
    /// </summary>
    [Fact]
    public async Task Explicit_close_does_not_throw_when_the_peer_aborts_first()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fakeBackend = new AbruptPeerFakeBackend(abortDelay: TimeSpan.Zero);
        var browser = await RealtimeBrowserClient.ConnectAsync(fakeBackend.BaseUri, cancellationToken: ct);

        await browser.CloseAsync(cancellationToken: ct);

        await browser.DisposeAsync();
    }

    /// <summary>
    /// PR #52 CI follow-up round 4 (swigerb/SonicAIDriveThru#28): deterministic, unit-level proof
    /// that <see cref="RealtimeBrowserClient.CloseAsync"/> treats the exact fault
    /// <c>ManagedWebSocket</c> was observed producing for a peer TCP reset mid-send (round 2:
    /// <see cref="OperationCanceledException"/> wrapping <see cref="IOException"/> wrapping
    /// <see cref="System.Net.Sockets.SocketException"/>) as a benign "already closed" rather than
    /// letting it escape -- with zero real sockets, zero real threads, and zero timing dependence,
    /// replacing round 3's thread-pool-starvation technique (see the class doc comment above for
    /// why that was unsafe by construction, not just flaky on Linux).
    /// <see cref="ThrowingCloseFakeSocket"/> is injected directly via
    /// <see cref="RealtimeBrowserClient.CreateForTesting"/>, bypassing the real HTTP/WS handshake
    /// and the background reader loop entirely.
    ///
    /// Mutation-check: removing <c>CloseAsync</c>'s <c>catch (OperationCanceledException) when
    /// (!cancellationToken.IsCancellationRequested)</c> clause turns this test red (with exactly
    /// <see cref="ThrowingCloseFakeSocket"/>'s thrown exception, uncaught); restoring it turns it
    /// green. See the PR #52 CI follow-up report for the run log.
    /// </summary>
    [Fact]
    public async Task Explicit_close_does_not_throw_when_the_peer_resets_mid_send()
    {
        var browser = RealtimeBrowserClient.CreateForTesting(new ThrowingCloseFakeSocket());
        await browser.CloseAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// PR #52 CI follow-up round 4: the flip side of
    /// <see cref="Explicit_close_does_not_throw_when_the_peer_resets_mid_send"/> -- proves the
    /// <c>when (!cancellationToken.IsCancellationRequested)</c> guard genuinely discriminates
    /// rather than unconditionally swallowing every <see cref="OperationCanceledException"/>. When
    /// the *caller's own* token is what requested cancellation, <c>CloseAsync</c> must still
    /// propagate -- even though the underlying fault happens to look identical (an
    /// <see cref="OperationCanceledException"/> out of the same <see cref="ThrowingCloseFakeSocket"/>).
    /// This is the exact safety property the round-2 fix's <c>when</c> guard exists for; without a
    /// test like this, a future edit could accidentally drop the guard (making the catch
    /// unconditional, silently swallowing genuine caller cancellations) without any test noticing.
    /// </summary>
    [Fact]
    public async Task Explicit_close_propagates_a_genuine_caller_cancellation()
    {
        var browser = RealtimeBrowserClient.CreateForTesting(new ThrowingCloseFakeSocket());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => browser.CloseAsync(cancellationToken: cts.Token));
    }

    /// <summary>
    /// PR #52 CI follow-up round 4: same technique and same round-2 finding as
    /// <see cref="Explicit_close_does_not_throw_when_the_peer_resets_mid_send"/>, but exercising
    /// <see cref="RealtimeBrowserClient.DisposeAsync"/>'s own independent attempt at a graceful
    /// <c>CloseOutputAsync</c> directly. <c>DisposeAsync</c> takes no <see cref="CancellationToken"/>
    /// parameter at all (its internal <c>CloseOutputAsync</c> call always passes
    /// <see cref="CancellationToken.None"/>), so unlike <c>CloseAsync</c> there is no
    /// caller-cancellation case to test here -- its added <c>catch (OperationCanceledException)</c>
    /// is unconditional (see that method's doc comment for why that's safe).
    ///
    /// Mutation-check: removing <c>DisposeAsync</c>'s <c>catch (OperationCanceledException)</c>
    /// clause turns this test red (with exactly <see cref="ThrowingCloseFakeSocket"/>'s thrown
    /// exception, uncaught); restoring it turns it green.
    /// </summary>
    [Fact]
    public async Task Plain_disposal_does_not_throw_when_the_peer_resets_mid_send()
    {
        var browser = RealtimeBrowserClient.CreateForTesting(new ThrowingCloseFakeSocket());
        await browser.DisposeAsync();
    }

    /// <summary>
    /// PR #52 CI follow-up (swigerb/SonicAIDriveThru#28 N10 aftermath, CI runs 36085091969):
    /// deterministic proof of the exact failure -- and exact fix -- for the resume scenarios'
    /// keepalive loops (<see cref="ResumeRehydrationClientVisibilityTests"/>,
    /// <see cref="WholeSessionLeakTests"/>). <see cref="AbruptPeerFakeBackend"/> reliably confirms
    /// (see this test's own mutation-check below) that a send on a socket that has settled into
    /// <see cref="WebSocketState.Aborted"/> throws exactly a <see cref="WebSocketException"/> ("The
    /// WebSocket is in an invalid state ('Aborted') for this operation") -- the same exception type
    /// (if not the identical message) as CI's "closed without completing the close handshake",
    /// and the type <see cref="Conformance.Harness.KeepAlive.RunAsync"/> now also catches alongside
    /// <see cref="OperationCanceledException"/>. This reproduces that class of failure without
    /// needing the real Python backend's idle-sweep timing to cooperate (which, per
    /// <see cref="ResumeMarginRegressionTests"/>'s own doc comment, could not be forced reliably
    /// via wall-clock delay on this machine for a *send* specifically, only for the *resume
    /// rejection* Fix A addresses).
    ///
    /// Round 5 (Rick's PR #52 review, S2): this test, <see cref="ResumeRehydrationClientVisibilityTests"/>,
    /// and <see cref="WholeSessionLeakTests"/> previously each had their own hand-copied keepalive
    /// loop with its own <c>catch</c> clauses -- so this self-test's own copy passing proved
    /// nothing about whether either scenario's *separate* copy still had the fix applied. Now all
    /// three call the same <see cref="Conformance.Harness.KeepAlive.RunAsync"/> helper, so there is
    /// exactly one place the fix can be removed from, and removing it fails every caller.
    ///
    /// Mutation-check: removing <c>KeepAlive.RunAsync</c>'s <c>catch (WebSocketException)</c> turns
    /// this test red with exactly the exception type above, AND turns
    /// <see cref="ResumeRehydrationClientVisibilityTests"/>/<see cref="WholeSessionLeakTests"/> red
    /// too (their own idle-sweep-vs-nudge race depends on the same swallowed exception); restoring
    /// it turns all three green again. See the PR #52 CI follow-up report for the run log.
    /// </summary>
    [Fact]
    public async Task Keepalive_style_loop_survives_the_peer_aborting_mid_loop()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fakeBackend = new AbruptPeerFakeBackend(abortDelay: TimeSpan.FromMilliseconds(150));
        var browser = await RealtimeBrowserClient.ConnectAsync(fakeBackend.BaseUri, cancellationToken: ct);

        // Mirrors the scenario files' own use of the shared helper: send on a fixed cadence for
        // long enough to run both before and after the fake's abort lands partway through (10
        // iterations at the old inline loop's 50ms cadence = ~500ms).
        var keepAlive = KeepAlive.RunAsync(browser, TimeSpan.FromMilliseconds(50), ct);
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        await keepAlive.StopAsync();

        await browser.DisposeAsync();
    }

    /// <summary>
    /// Minimal local stand-in for the two backend endpoints <see cref="RealtimeBrowserClient.ConnectAsync"/>
    /// needs (<c>/api/auth/session</c>, <c>/realtime</c>), used only so <c>#28 N10</c>'s
    /// disposal-ordering test can control exactly when the peer answers the client's Close frame —
    /// proving the fix deterministically instead of racing against however fast the real Python
    /// backend happens to reply.
    /// </summary>
    private sealed class DelayedCloseFakeBackend : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly TimeSpan _closeReplyDelay;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        public DelayedCloseFakeBackend(TimeSpan closeReplyDelay)
        {
            _closeReplyDelay = closeReplyDelay;

            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            BaseUri = new Uri($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(BaseUri.ToString());
            _listener.Start();
            _acceptLoop = AcceptLoopAsync(_cts.Token);
        }

        public Uri BaseUri { get; }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or HttpListenerException)
                {
                    return;
                }

                _ = HandleAsync(context, ct);
            }
        }

        private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
        {
            if (context.Request.Url?.AbsolutePath == "/api/auth/session")
            {
                var body = System.Text.Encoding.UTF8.GetBytes("""{"token":"fake-token"}""");
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body, ct).ConfigureAwait(false);
                context.Response.Close();
                return;
            }

            if (context.Request.Url?.AbsolutePath == "/realtime")
            {
                var wsContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
                var socket = wsContext.WebSocket;
                var buffer = new byte[4096];
                try
                {
                    while (true)
                    {
                        var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            // The whole point: answer only after a fixed delay, so the client's
                            // disposal path has to actually wait for it rather than racing it.
                            await Task.Delay(_closeReplyDelay, ct).ConfigureAwait(false);
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "ack", ct).ConfigureAwait(false);
                            return;
                        }
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
                {
                    // Best-effort: the client tore down before we got to answer.
                }

                return;
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            _listener.Close();
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort teardown of the accept loop.
            }
        }
    }

    /// <summary>
    /// Minimal local stand-in used by
    /// <see cref="Explicit_close_does_not_throw_when_the_peer_aborts_first"/> (PR #52 CI follow-up,
    /// swigerb/SonicAIDriveThru#28 N10 aftermath) to deterministically simulate the peer
    /// disappearing -- no close frame, no answer, nothing -- shortly after accepting the
    /// connection, mirroring the CI runs 36085091969 backend log ("Session … detached (client
    /// close code=1000)" racing "Resume nudge cancelled: socket closed") on a loaded runner.
    /// Unlike <see cref="DelayedCloseFakeBackend"/> (which answers the client's Close frame late,
    /// on request), this fake never answers at all -- it aborts unconditionally, without waiting
    /// for the client to send anything, so it lands regardless of exactly when
    /// <see cref="RealtimeBrowserClient.CloseAsync"/> is called relative to it.
    /// </summary>
    private sealed class AbruptPeerFakeBackend : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly TimeSpan _abortDelay;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        public AbruptPeerFakeBackend(TimeSpan abortDelay)
        {
            _abortDelay = abortDelay;

            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            BaseUri = new Uri($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(BaseUri.ToString());
            _listener.Start();
            _acceptLoop = AcceptLoopAsync(_cts.Token);
        }

        public Uri BaseUri { get; }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or HttpListenerException)
                {
                    return;
                }

                _ = HandleAsync(context, ct);
            }
        }

        private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
        {
            if (context.Request.Url?.AbsolutePath == "/api/auth/session")
            {
                var body = System.Text.Encoding.UTF8.GetBytes("""{"token":"fake-token"}""");
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body, ct).ConfigureAwait(false);
                context.Response.Close();
                return;
            }

            if (context.Request.Url?.AbsolutePath == "/realtime")
            {
                var wsContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
                var socket = wsContext.WebSocket;

                // The whole point: disappear on our own schedule, regardless of anything the
                // client sends or doesn't send -- an abrupt drop, not an answered close.
                await Task.Delay(_abortDelay, ct).ConfigureAwait(false);
                socket.Abort();
                return;
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            _listener.Close();
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort teardown of the accept loop.
            }
        }
    }

    /// <summary>
    /// PR #52 CI follow-up round 4 (swigerb/SonicAIDriveThru#28): replaces round 3's
    /// thread-pool-starvation reproduction technique (see
    /// <see cref="Explicit_close_does_not_throw_when_the_peer_aborts_first"/>'s doc comment for
    /// the full history of why that was unsafe by construction -- it spawned
    /// <c>ThreadPool.GetMaxThreads()</c>'s worker ceiling times 2 (65,534 on both this machine and
    /// the CI runner, since that ceiling defaults to 32767) real dedicated OS threads every run,
    /// which crashed CI run 36091977281 with a native stack overflow while the runtime was already
    /// out of resources trying to report a resulting allocation failure).
    ///
    /// A minimal <see cref="WebSocket"/> stand-in whose <see cref="CloseOutputAsync"/> always
    /// throws exactly the exception shape <c>ManagedWebSocket</c> was observed producing for a
    /// peer TCP reset encountered mid-send (PR #52 CI follow-up round 2, CI run 36085091969):
    /// <see cref="OperationCanceledException"/> wrapping <see cref="IOException"/> wrapping
    /// <see cref="SocketException"/>. Injected directly into <see cref="RealtimeBrowserClient"/>
    /// via its internal <see cref="RealtimeBrowserClient.CreateForTesting"/> seam, which bypasses
    /// the real HTTP/WS handshake and never starts the background reader loop -- so none of this
    /// type's other members (<see cref="ReceiveAsync"/>, <see cref="SendAsync"/>,
    /// <see cref="CloseAsync"/>) are ever exercised by the tests that use it, and deliberately
    /// throw <see cref="NotSupportedException"/> rather than silently no-op, so a future test that
    /// accidentally does exercise them fails loudly instead of passing for the wrong reason.
    /// </summary>
    private sealed class ThrowingCloseFakeSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            throw new NotSupportedException($"{nameof(ThrowingCloseFakeSocket)} only rigs {nameof(CloseOutputAsync)}.");

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            // Mirrors CI run 36085091969's exact exception chain verbatim (PR #52 CI follow-up
            // round 2) -- the fault CloseAsync/DisposeAsync's added catch clauses exist to treat
            // as "already closed" rather than let escape.
            var socketException = new SocketException((int)SocketError.ConnectionReset);
            var ioException = new IOException(
                "Unable to write data to the transport connection: An existing connection was forcibly closed by the remote host.",
                socketException);
            throw new OperationCanceledException("The operation was canceled.", ioException);
        }

        public override void Dispose() => _state = WebSocketState.Closed;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException(
                $"{nameof(ThrowingCloseFakeSocket)} never receives -- the reader loop is never started (see CreateForTesting).");

        public override Task SendAsync(
            ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            throw new NotSupportedException($"{nameof(ThrowingCloseFakeSocket)} only rigs {nameof(CloseOutputAsync)}.");
    }
}
