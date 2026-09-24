using System.Net;
using System.Text.Json;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// #8: extends HealthEndpointTests.cs's `status` check with the rest of app/backend/app.py's
/// `/health` shape — `version` (the fixed `_APP_VERSION`) and the `checks` breakdown
/// (`prompts_loaded`/`config_loaded`/`env_vars`), all true/"healthy" for a correctly-launched
/// conformance backend. A new file, not an edit to the existing one, per this stream's own scope.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class HealthEndpointExtendedTests(ConformanceFixture fixture)
{
    [Fact]
    public Task Health_endpoint_reports_version_and_per_check_breakdown() => fixture.RunAsync(async () =>
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/health"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        var root = document.RootElement;
        Assert.Equal("healthy", root.GetProperty("status").GetString());
        Assert.Equal("1.0.0", root.GetProperty("version").GetString());

        var checks = root.GetProperty("checks");
        Assert.True(checks.GetProperty("prompts_loaded").GetBoolean());
        Assert.True(checks.GetProperty("config_loaded").GetBoolean());
        Assert.True(checks.GetProperty("env_vars").GetBoolean());
    });
}
