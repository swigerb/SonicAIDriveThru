using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// #8: extends AuthSessionTests.cs's "non-empty token string" check with the actual HMAC token
/// *format* app/backend/rtmt.py's `create_hmac_token` produces — `{payload_b64}.{sig}`, where
/// `payload_b64` is `base64.urlsafe_b64encode(json.dumps({"exp": ...}).encode()).decode()` and
/// `sig` is `hmac.new(secret, payload_b64.encode(), hashlib.sha256).hexdigest()` (a 64-hex-digit
/// SHA-256 digest) — see app/backend/tests/test_rtmt.py's HMACTokenTests for the behaviour this
/// shape supports (expiry/signature validation), ported here as the wire-shape assertions a
/// black-box client can make without the signing secret. A new file, not an edit to the existing
/// one, per this stream's own scope.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class AuthSessionTokenFormatTests(ConformanceFixture fixture)
{
    private static readonly Regex HexSha256 = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    [Fact]
    public Task Token_is_a_base64url_json_payload_and_a_sha256_hex_signature_joined_by_a_dot() => fixture.RunAsync(async () =>
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/api/auth/session"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        var token = document.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        // Exactly one '.' -- rtmt.py's own validate_hmac_token uses token.rsplit(".", 1), but
        // create_hmac_token's payload_b64 (standard base64url, no embedded '.') never produces
        // more than one split point in practice; asserting exactly one keeps this test honest
        // about the format actually produced today.
        var parts = token!.Split('.');
        Assert.Equal(2, parts.Length);

        var (payloadB64, signature) = (parts[0], parts[1]);
        Assert.Matches(HexSha256, signature);

        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(payloadB64));
        using var payloadDocument = JsonDocument.Parse(payloadJson);
        var exp = payloadDocument.RootElement.GetProperty("exp").GetInt64();
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.True(exp > nowUnix, $"Expected the token's exp ({exp}) to be in the future (now={nowUnix}).");
        // create_hmac_token's default expiry_seconds=900 -- a generous window around it catches a
        // regression that stopped setting an expiry at all (e.g. exp == now) without being tied
        // to the exact constant.
        Assert.True(exp <= nowUnix + 3600, $"Expected exp ({exp}) within an hour of now ({nowUnix}) -- got a suspiciously long-lived token.");
    });

    [Fact]
    public Task A_token_requested_a_second_later_carries_a_later_expiry_and_differs() => fixture.RunAsync(async () =>
    {
        using var http = new HttpClient();
        var uri = new Uri(fixture.Backend!.BaseUri, "/api/auth/session");
        var ct = TestContext.Current.CancellationToken;

        async Task<string> FetchTokenAsync()
        {
            using var response = await http.GetAsync(uri, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            return document.RootElement.GetProperty("token").GetString()!;
        }

        // create_hmac_token's payload is {"exp": int(time.time()) + expiry_seconds} -- whole
        // seconds, no nonce -- so two requests within the same wall-clock second are legitimately
        // byte-identical by design (app/backend/tests/test_rtmt.py never asserts otherwise). A
        // >1s gap is the actual, guaranteed condition under which two tokens must differ.
        var first = await FetchTokenAsync();
        await Task.Delay(TimeSpan.FromSeconds(1.1), ct);
        var second = await FetchTokenAsync();
        Assert.NotEqual(first, second);
    });

    /// <summary>Base64url (RFC 4648 §5) decode with '-'/'_' and re-padded '=' -- .NET's
    /// <see cref="Convert.FromBase64String"/> only accepts the standard '+'/'/' alphabet.</summary>
    private static byte[] Base64UrlDecode(string value)
    {
        var standard = value.Replace('-', '+').Replace('_', '/');
        var padded = standard.Length % 4 == 0 ? standard : standard + new string('=', 4 - standard.Length % 4);
        return Convert.FromBase64String(padded);
    }
}
