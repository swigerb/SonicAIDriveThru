using System.Net;
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
}
