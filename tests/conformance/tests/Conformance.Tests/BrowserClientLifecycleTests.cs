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
}
