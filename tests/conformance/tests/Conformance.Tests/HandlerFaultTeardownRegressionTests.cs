using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// PR #22 review item N3 regression: a backend closing its upstream socket mid-stream (a normal,
/// expected occurrence — it happens on every browser drop) must never be misattributed as a
/// handler fault on a *later*, unrelated scenario sharing the same fixture. This exercises
/// <see cref="ConformanceFixture.RunAsync"/> directly (not a bare <see cref="FakeRealtimeUpstreamServer"/>)
/// because the bug this guards against was specifically a race in <c>RunAsync</c>'s own teardown
/// ordering: scenario A's own checks used to run immediately after its body returned, before the
/// connection it dropped had actually finished tearing down server-side — so A passed clean, and
/// the fault only showed up once recorded, misattributed to whichever scenario ran next (scenario
/// B here, which touches nothing of its own).
///
/// Connects a raw <see cref="ClientWebSocket"/> straight to <see cref="ConformanceFixture.Realtime"/>
/// rather than going through the real Python backend and a real browser client — this simulates
/// exactly what the backend's own outgoing OpenAI connection looks like from the fake's side, and
/// the race under test lives entirely inside <see cref="FakeRealtimeUpstreamServer"/> /
/// <see cref="ConformanceFixture"/>, not in any Python behaviour, so a real backend process adds
/// nothing but slowness to this regression test.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class HandlerFaultTeardownRegressionTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Dropping_a_streaming_upstream_connection_mid_response_never_fails_the_next_scenario()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            await StreamThenDropMidResponseAsync(iteration);
            await EmptyScenarioAsync();
        }
    }

    /// <summary>
    /// Scenario A: opens a raw upstream connection, scripts a paced response, and drops the
    /// connection mid-stream via a normal graceful close (<see cref="WebSocket.CloseOutputAsync"/>,
    /// not <see cref="FakeRealtimeConnection.Abort"/>) — exactly Rick's repro shape ("a backend
    /// closing its upstream socket mid-stream is normal, it happens on every browser drop").
    /// </summary>
    private Task StreamThenDropMidResponseAsync(int iteration) => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("api-key", BackendContract.OpenAiApiKey);
        var wsUri = new Uri($"ws://{fixture.Realtime.BaseUri.Host}:{fixture.Realtime.BaseUri.Port}/openai/v1/realtime?model=gpt-realtime-test");
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await socket.ConnectAsync(wsUri, ct).ConfigureAwait(false);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"[iteration {iteration}] No upstream connection was accepted within {FrameTimeout}.");

        Assert.NotNull(await WebSocketJson.ReceiveJsonAsync(socket, ct).ConfigureAwait(false)); // session.created

        // Paced so there's a real window in which to drop the socket mid-stream, mirroring a
        // slow real browser/backend disconnect rather than an instantaneous one.
        connection!.Script.Enqueue(new ResponseScript(
        [
            new AudioDeltaEvent("Zmlyc3Q=", Pace: TimeSpan.FromMilliseconds(150)),
            new AudioDeltaEvent("c2Vjb25k", Pace: TimeSpan.FromMilliseconds(150)),
            new DoneEvent(),
        ]));

        await WebSocketJson.SendAsync(socket, new JsonObject { ["type"] = "response.create" }, ct).ConfigureAwait(false);

        // Let response.created and the stream's first (unpaced) frames go out, then drop while
        // the paced second delta is still pending — simulates "the browser disconnects
        // mid-stream" via a normal graceful close, not an abrupt Abort().
        await Task.Delay(60, ct).ConfigureAwait(false);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "dropped mid-stream", ct).ConfigureAwait(false);
    });

    /// <summary>
    /// Scenario B: touches nothing at all — proves a fault recorded on scenario A's connection
    /// (which may still be asynchronously tearing down server-side when this scenario starts)
    /// never leaks into failing an unrelated, completely passive scenario.
    /// </summary>
    private Task EmptyScenarioAsync() => fixture.RunAsync(() => Task.CompletedTask);
}
