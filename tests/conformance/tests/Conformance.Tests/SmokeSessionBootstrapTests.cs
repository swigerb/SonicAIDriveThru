using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// The #7 acceptance smoke scenario: connect -> the bootstrap `session.update` (4 tools,
/// `tool_choice=auto`) is the first frame the fake sees on *this test's own* upstream socket ->
/// a browser `session.update` is forwarded -> the greeting `response.create` arrives only after
/// that. Uses <see cref="FakeRealtimeUpstreamServer.WaitForNextConnectionAsync"/> to capture the
/// specific <see cref="FakeRealtimeConnection"/> this test's browser connect causes, rather than
/// a baseline index into a server-wide log every connection used to share (which let another
/// test's frame satisfy a "first frame" assertion — see PR #22 review item 5).
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class SmokeSessionBootstrapTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Bootstrap_session_update_with_four_tools_is_the_first_upstream_frame() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        await AssertNoOpenConnectionsAtStartAsync(ct);
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);

        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);

        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        var bootstrap = await connection!.ReceivedFrames.WaitForAsync(f => f.Sequence == 0, FrameTimeout, ct);
        Assert.True(bootstrap is not null, $"Expected the first frame on this connection (the bootstrap session.update) within {FrameTimeout}.");
        Assert.Equal("session.update", bootstrap!.Type);

        var session = bootstrap.Json.GetProperty("session");
        Assert.Equal("auto", session.GetProperty("tool_choice").GetString());
        Assert.Equal(4, session.GetProperty("tools").GetArrayLength());
    });

    [Fact]
    public Task Greeting_response_create_arrives_only_after_the_browser_session_update() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        await AssertNoOpenConnectionsAtStartAsync(ct);
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);

        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);

        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        var bootstrap = await connection!.ReceivedFrames.WaitForAsync(f => f.Sequence == 0, FrameTimeout, ct);
        Assert.True(bootstrap is not null, "Bootstrap session.update never arrived.");

        await browser.SendStartSessionAsync(cancellationToken: ct);

        var browserUpdate = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence > 0 && f.Type == "session.update", FrameTimeout, ct);
        Assert.True(browserUpdate is not null, "The browser's session.update was never forwarded upstream.");

        // GA nests turn_detection/input_audio_transcription under session.audio.input — confirms
        // _to_ga_session translated the browser's payload rather than passing something else.
        Assert.True(browserUpdate!.Json.GetProperty("session").TryGetProperty("audio", out _),
            "Expected the browser session.update to be GA-translated (session.audio.input.*).");

        var greeting = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence > browserUpdate.Sequence && f.Type == "response.create", FrameTimeout, ct);
        Assert.True(greeting is not null, "The greeting response.create never arrived after the browser's session.update.");
        Assert.True(greeting!.Sequence > browserUpdate.Sequence);
    });

    /// <summary>
    /// A connection left open by a previous test (or a bug in this one) would let its frames
    /// satisfy this test's own frame-index assertions — fail loudly and immediately instead of
    /// silently passing on the wrong socket's data.
    /// </summary>
    private async Task AssertNoOpenConnectionsAtStartAsync(CancellationToken ct)
    {
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");
    }
}
