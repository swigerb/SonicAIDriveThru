using System.Net;
using System.Text.Json;
using Xunit;

namespace Conformance.Tests;

[Collection(ConformanceCollection.Name)]
public sealed class AuthSessionTests(ConformanceFixture fixture)
{
    [Fact]
    public Task Auth_session_endpoint_returns_a_token() => fixture.RunAsync(async () =>
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/api/auth/session"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        var token = document.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
    });
}
