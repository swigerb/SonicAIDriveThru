using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// #8: app/backend/rtmt.py's `_websocket_handler` origin check --
/// <c>if origin and not origin.endswith(host) and origin not in allowed_origins: return 403</c> --
/// with config.yaml's default `security.allowed_origins: []` (empty = same-origin only). Ported
/// from app/backend/tests/test_rtmt.py's `test_origin_validation_rejects_foreign_origin`
/// (`https://evil.com` vs `Host: localhost:8080`), plus the exact-host success case implicit in
/// every other scenario using <see cref="RealtimeBrowserClient.ConnectAsync"/>'s default Origin.
///
/// <c>origin.endswith(host)</c> is a raw string-suffix check with no domain-boundary requirement:
/// an attacker origin that merely ends with the exact `Host` header value as a *substring* (e.g.
/// "evil-" prefixed onto the real host) incorrectly passes. Python's own test above never
/// exercises this lookalike-suffix shape -- only a completely unrelated origin -- so this is an
/// undiscovered bug, not a regression of tested behaviour. Reported to the coordinator as #25
/// (owned by another stream); the scenario below documents the *correct* behaviour and is
/// skipped until #25 lands.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class OriginValidationTests(ConformanceFixture fixture)
{
    private static async Task<string> FetchTokenAsync(Uri backendBaseUri, CancellationToken ct)
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(backendBaseUri, "/api/auth/session"), ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        return document.RootElement.GetProperty("token").GetString() ?? "";
    }

    private static async Task<(bool Connected, HttpStatusCode? StatusCode)> TryConnectWithOriginAsync(
        Uri backendBaseUri, string origin, CancellationToken ct)
    {
        var token = await FetchTokenAsync(backendBaseUri, ct);
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Origin", origin);
        var wsUri = new Uri($"ws://{backendBaseUri.Host}:{backendBaseUri.Port}/realtime?token={Uri.EscapeDataString(token)}");
        try
        {
            await socket.ConnectAsync(wsUri, ct);
            return (true, null);
        }
        catch (WebSocketException)
        {
            return (false, socket.HttpStatusCode);
        }
        finally
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
            }
        }
    }

    [Fact]
    public Task Exact_host_origin_is_accepted() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var origin = $"{fixture.Backend!.BaseUri.Scheme}://{fixture.Backend.BaseUri.Authority}";
        var (connected, _) = await TryConnectWithOriginAsync(fixture.Backend.BaseUri, origin, ct);
        Assert.True(connected, "A same-origin (exact Host match) handshake must be accepted.");
    });

    [Fact]
    public Task Unrelated_foreign_origin_is_rejected_with_403() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (connected, statusCode) = await TryConnectWithOriginAsync(fixture.Backend!.BaseUri, "https://evil.com", ct);
        Assert.False(connected, "A completely unrelated Origin must be rejected.");
        Assert.Equal(HttpStatusCode.Forbidden, statusCode);
    });

    [Fact(Skip = "Known Python bug: rtmt.py's origin check (`origin.endswith(host)`) is a raw " +
        "string-suffix comparison with no domain-boundary requirement, so an attacker origin " +
        "that merely ends with the exact Host value as a substring incorrectly passes same-origin " +
        "validation instead of being rejected. Reported to the coordinator as #25 (owned by " +
        "another stream); un-skip once #25 lands.")]
    public Task Lookalike_suffix_origin_is_rejected() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var host = fixture.Backend!.BaseUri.Authority; // e.g. "127.0.0.1:54321"
        var lookalikeOrigin = $"http://evil-lookalike-{host}";
        var (connected, statusCode) = await TryConnectWithOriginAsync(fixture.Backend.BaseUri, lookalikeOrigin, ct);
        Assert.False(connected, $"Origin '{lookalikeOrigin}' is not same-origin with Host '{host}' and must be rejected.");
        Assert.Equal(HttpStatusCode.Forbidden, statusCode);
    });
}
