using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

[Collection(ConformanceCollection.Name)]
public sealed class WebSocketCompressionTests(ConformanceFixture fixture)
{
    /// <summary>
    /// app/backend/config.yaml pins `ws_compression: false` on the browser-facing socket
    /// (`web.WebSocketResponse(compress=_WS_COMPRESS)` in rtmt.py) — aiohttp 3.14.2/3.14.3 reject
    /// the first compressed frame after an initial PONG (aio-libs/aiohttp#13274). Even when we
    /// offer permessage-deflate, the handshake response must not grant it.
    /// </summary>
    [Fact]
    public Task Deflate_is_not_negotiated_on_the_browser_socket() => fixture.RunAsync(async () =>
    {
        await using var client = await RealtimeBrowserClient.ConnectAsync(
            fixture.Backend!.BaseUri, offerDeflate: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(client.NegotiatedExtensions);
    });
}
