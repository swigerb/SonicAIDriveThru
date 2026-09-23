using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// PR #22 review item 12: proves the ShortTimers backend profile actually shortens
/// app/backend/rtmt.py's session-configured fallback timeout for the Python process it launches,
/// not just that the launcher accepts the extra environment variable.
/// </summary>
[Collection(ShortTimersConformanceCollection.Name)]
public sealed class GreetingTimeoutFallbackTests(ShortTimersConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Greeting_still_fires_via_the_shortened_session_configured_timeout() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        // Armed before the browser even connects: this backend process's very first two
        // session.update frames are (1) its own bootstrap send and (2) the browser's
        // session.update forwarded upstream once SendStartSessionAsync below is called --
        // rtmt.py's session_configured event is set on *either* one's reply (see rtmt.py's
        // "bootstrap session.updated arrives as soon as the socket opens" comment), so both
        // must be suppressed or the second reply would set it before the fallback timeout ever
        // has to fire. There's no race to win here — suppression is consumed synchronously by
        // whichever session.update the fake processes next, and nothing else could reach the
        // fake first.
        fixture.Realtime.SuppressNextSessionUpdatedResponse();
        fixture.Realtime.SuppressNextSessionUpdatedResponse();

        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        var bootstrap = await connection!.ReceivedFrames.WaitForAsync(f => f.Sequence == 0, FrameTimeout, ct);
        Assert.True(bootstrap is not null, "Bootstrap session.update never arrived.");

        await browser.SendStartSessionAsync(cancellationToken: ct);

        // Comfortably above the ShortTimers profile's ~1s override but well under the 5s
        // production default -- this scenario can only pass if CONFORMANCE_GREETING_TIMEOUT_SECONDS
        // actually took effect for this backend process.
        var shortTimeout = TimeSpan.FromSeconds(3);
        var greeting = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence > bootstrap!.Sequence && f.Type == "response.create", shortTimeout, ct);
        Assert.True(greeting is not null,
            $"Expected the greeting response.create within {shortTimeout} despite session.updated " +
            "being suppressed -- CONFORMANCE_GREETING_TIMEOUT_SECONDS should have shortened the " +
            "fallback wait for this backend process. If this backend used the 5s production " +
            "default instead, this would time out.");

        var diagnostics = fixture.Backend!.DumpDiagnostics();
        Assert.Contains("No session.updated within", diagnostics, StringComparison.Ordinal);
    });
}
