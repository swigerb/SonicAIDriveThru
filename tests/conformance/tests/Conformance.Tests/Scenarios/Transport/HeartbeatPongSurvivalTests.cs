using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// #8: aiohttp's `web.WebSocketResponse(heartbeat=_WS_HEARTBEAT_SEC, ...)` (config.yaml's
/// `connection.ws_heartbeat_seconds: 15.0`) sends an automatic PING on this connection roughly
/// every 15 real seconds of otherwise-idle traffic; .NET's <c>ClientWebSocket</c> intrinsically
/// answers any incoming Ping with an automatic Pong at the transport layer (this is not optional
/// or configurable, and neither the Ping nor the Pong is ever surfaced as an application frame),
/// so this exchange is invisible to <see cref="RealtimeBrowserClient.ReceivedFrames"/>. What issue
/// #8 actually asks to prove -- "PONG-then-data survives" -- is that a real data frame sent right
/// after that (invisible) heartbeat round trip is still processed normally, i.e. neither aiohttp
/// nor rtmt.py's forwarding loop chokes on the PONG-then-data sequence (the exact aiohttp
/// regression <see cref="WebSocketCompressionTests"/> also references, aio-libs/aiohttp#13274,
/// was specifically about the frame immediately following a PONG).
///
/// There is no test hook to shorten `ws_heartbeat_seconds` (unlike the timers ShortTimers
/// controls) and config.yaml has no generic env-var override mechanism, so proving this
/// black-box genuinely means waiting past one real heartbeat interval -- the deliberate,
/// documented `Task.Delay` below is a real-time protocol assertion (there is no frame to
/// synchronize on instead: the heartbeat round trip is invisible by design), not a
/// synchronization workaround.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class HeartbeatPongSurvivalTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    // config.yaml's connection.ws_heartbeat_seconds default (15.0) plus a comfortable margin so
    // at least one full PING/(automatic-)PONG round trip has definitely happened by the time the
    // follow-up frame below is sent.
    private static readonly TimeSpan PastOneHeartbeatInterval = TimeSpan.FromSeconds(17);

    [Fact]
    public Task Data_frame_sent_after_a_heartbeat_pong_round_trip_is_still_processed() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        await browser.SendStartSessionAsync(cancellationToken: ct);

        var greetingToken = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(greetingToken is not null, "extension.round_trip_token never reached the browser (greeting never completed).");

        // Deliberate real-time wait -- see the type doc comment above for why this is the only
        // way to observe genuine production heartbeat timing black-box.
        await Task.Delay(PastOneHeartbeatInterval, ct);

        Assert.Null(browser.CloseStatus); // the connection must still be open at this point.

        // A real data frame right after the (invisible) heartbeat round trip -- response.create
        // needs no prior state and gets a clean, observable reply if the pipe is still healthy.
        await browser.SendResponseCreateAsync(cancellationToken: ct);
        var secondRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token" && f.Sequence > greetingToken!.Sequence, FrameTimeout, ct);
        Assert.True(secondRoundTrip is not null,
            "Expected a second extension.round_trip_token after a response.create sent past one heartbeat " +
            "interval -- the connection must survive the PONG-then-data sequence, not silently stop " +
            "forwarding frames.");
    });
}
