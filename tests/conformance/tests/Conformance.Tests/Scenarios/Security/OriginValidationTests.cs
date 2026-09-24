using System.Net;
using System.Net.WebSockets;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Security;

/// <summary>
/// swigerb/SonicAIDriveThru#25: `_websocket_handler`'s Origin check used to be
/// `origin.endswith(host)`, which accepts any Origin whose netloc merely ends with the request's
/// `Host` header as a *string suffix* -- e.g. `https://evil-&lt;host&gt;` passes
/// `"evil-&lt;host&gt;".endswith("&lt;host&gt;")` even though it's a domain the attacker actually
/// controls, not the real host. Fixed by `_origin_matches_host`, which parses the Origin with
/// `urllib.parse.urlsplit` and requires its `netloc` (host, and port when non-default) to be an
/// *exact*, case-insensitive match for `Host` -- see app/backend/rtmt.py.
///
/// `app/backend/config.yaml`'s default `security.require_session_token: false` means these
/// scenarios don't need a valid session token: the Origin check runs (and can reject) before the
/// token check, so the token/`allowed_origins` config used by the conformance backend never
/// enters into it.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class OriginValidationTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Exact_origin_is_accepted() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = fixture.Backend!.BaseUri;
        var exactOrigin = $"{backend.Scheme}://{backend.Authority}";

        await using var browser = await RealtimeBrowserClient.ConnectAsync(backend, origin: exactOrigin, cancellationToken: ct);

        var created = await browser.ReceivedFrames.WaitForAsync(f => f.Type == "session.created", FrameTimeout, ct);
        Assert.True(created is not null,
            "An Origin that exactly matches the request Host must be accepted, but no session.created arrived.");
    });

    [Fact]
    public Task Lookalike_suffix_origin_is_rejected_with_403() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = fixture.Backend!.BaseUri;
        // "evil-<host>" ends with the real host as a string suffix -- exactly the bypass #25
        // closed. A real browser can never send this as same-origin (only the attacker's own
        // "evil-<host>" page, which is a different origin the guest never visited), but a
        // malicious page can set any Origin it likes if the check doesn't validate it properly.
        var lookalikeOrigin = $"{backend.Scheme}://evil-{backend.Authority}";

        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Origin", lookalikeOrigin);
        var wsUri = new Uri($"ws://{backend.Host}:{backend.Port}/realtime");

        var ex = await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(wsUri, ct));
        Assert.Equal(HttpStatusCode.Forbidden, socket.HttpStatusCode);
        Assert.NotEqual(WebSocketState.Open, socket.State);
        Assert.NotNull(ex);
    });

    [Fact]
    public Task Missing_origin_is_accepted_unchanged() => fixture.RunAsync(async () =>
    {
        // Documents existing, #25-unaffected behaviour: a request with no Origin header at all
        // (a non-browser client -- curl, a server-to-server caller) is still accepted. #25 only
        // hardens the case where an Origin *is* present but doesn't match.
        var ct = TestContext.Current.CancellationToken;
        var backend = fixture.Backend!.BaseUri;

        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        // Deliberately no Origin header set.
        var wsUri = new Uri($"ws://{backend.Host}:{backend.Port}/realtime");

        await socket.ConnectAsync(wsUri, ct);
        Assert.Equal(WebSocketState.Open, socket.State);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    });
}
