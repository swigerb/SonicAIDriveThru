using System.Net;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// #8: app/backend/app.py's `_index_handler` serves the built frontend's `static/index.html` for
/// the root route with `Cache-Control: no-cache` explicitly set (so a stale cached shell never
/// masks a new deploy) — see app/backend/tests/test_app.py. Not previously covered by any
/// existing conformance test.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class StaticIndexHtmlTests(ConformanceFixture fixture)
{
    [Fact]
    public Task Root_route_serves_index_html_with_cache_control_no_cache() => fixture.RunAsync(async () =>
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(fixture.Backend!.BaseUri, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        Assert.Contains("text/html", response.Content.Headers.ContentType?.MediaType ?? "", StringComparison.OrdinalIgnoreCase);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(body), "Expected a non-empty index.html body.");
    });
}
