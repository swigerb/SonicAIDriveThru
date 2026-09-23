using System.Net;
using System.Text.Json;
using Xunit;

namespace Conformance.Tests;

[Collection(ConformanceCollection.Name)]
public sealed class HealthEndpointTests(ConformanceFixture fixture)
{
    [Fact]
    public Task Health_endpoint_returns_200() => fixture.RunAsync(async () =>
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/health"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        Assert.Equal("healthy", document.RootElement.GetProperty("status").GetString());
    });
}
