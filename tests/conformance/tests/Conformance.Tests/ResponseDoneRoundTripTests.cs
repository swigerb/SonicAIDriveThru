using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Scenarios exercising a full response.done round trip past the greeting, per PR #22 review
/// item 2: `response.done` must always carry GA-shaped `response.output[]` + `response.usage`,
/// otherwise rtmt.py's `response.done` handler raises a KeyError reading
/// `message["response"]["output"]`, which the outer handler logs as "Unexpected error in
/// WebSocket forwarding" and — because that exception aborts both directions of the
/// asyncio.gather'd forwarding loop — silently kills the whole connection. The browser never
/// gets a second frame again, including `extension.round_trip_token`.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class ResponseDoneRoundTripTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Browser_receives_round_trip_token_after_the_greeting_and_backend_logs_no_traceback() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        await browser.SendStartSessionAsync(cancellationToken: ct);

        // The greeting response.create's response.done (Script default: one audio delta then
        // done) is what rtmt.py's response.done handler turns into extension.round_trip_token —
        // this is the frame that never used to arrive with a KeyError-shaped response.done.
        var roundTripToken = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(roundTripToken is not null,
            $"extension.round_trip_token never reached the browser within {FrameTimeout}.");

        var diagnostics = fixture.Backend!.DumpDiagnostics();
        Assert.DoesNotContain("Traceback", diagnostics, StringComparison.Ordinal);
    });
}
