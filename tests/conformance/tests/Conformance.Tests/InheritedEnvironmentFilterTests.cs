using System.Diagnostics;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Unit tests for <see cref="InheritedEnvironmentFilter"/> (PR #22 review item 13). Pure and
/// process-free -- no backend or fakes involved -- so this exercises the stripping/NO_PROXY
/// policy directly and deterministically instead of relying on an end-to-end Python run to
/// prove it indirectly.
/// </summary>
public sealed class InheritedEnvironmentFilterTests
{
    [Fact]
    public void Apply_strips_conformance_azure_proxy_and_verbose_prefixed_vars_but_keeps_others()
    {
        var startInfo = new ProcessStartInfo();
        // Simulate exactly the ambient contamination item 13 is worried about: leftovers from a
        // prior manual CONFORMANCE_TEST_HOOKS run, an unrelated `az account set`, and the
        // corporate proxy env vars the NuGet/npm/pip proxy setup relies on -- mixed casing, since
        // Windows env var names are case-insensitive and any of these could show up either way.
        startInfo.Environment["CONFORMANCE_TEST_HOOKS"] = "1";
        startInfo.Environment["CONFORMANCE_FIXED_NOW"] = "2026-01-01T00:00:00-06:00";
        startInfo.Environment["AZURE_SUBSCRIPTION_ID"] = "44847a42-6b69-4e6c-b7e5-ce7140469dd6";
        startInfo.Environment["azure_openai_eastus2_api_key"] = "leaked-real-key";
        startInfo.Environment["HTTP_PROXY"] = "http://corp-proxy.contoso.com:8080";
        startInfo.Environment["https_proxy"] = "http://corp-proxy.contoso.com:8080";
        startInfo.Environment["VERBOSE_LOGGING"] = "true";
        // Unrelated vars a normal shell always has -- must survive untouched.
        startInfo.Environment["PATH"] = "C:\\some\\path";
        startInfo.Environment["USERNAME"] = "brswig";

        InheritedEnvironmentFilter.Apply(startInfo);

        Assert.False(startInfo.Environment.ContainsKey("CONFORMANCE_TEST_HOOKS"));
        Assert.False(startInfo.Environment.ContainsKey("CONFORMANCE_FIXED_NOW"));
        Assert.False(startInfo.Environment.ContainsKey("AZURE_SUBSCRIPTION_ID"));
        Assert.False(startInfo.Environment.ContainsKey("azure_openai_eastus2_api_key"));
        Assert.False(startInfo.Environment.ContainsKey("HTTP_PROXY"));
        Assert.False(startInfo.Environment.ContainsKey("https_proxy"));
        Assert.False(startInfo.Environment.ContainsKey("VERBOSE_LOGGING"));

        Assert.Equal("C:\\some\\path", startInfo.Environment["PATH"]);
        Assert.Equal("brswig", startInfo.Environment["USERNAME"]);
    }

    [Fact]
    public void Apply_pins_NO_PROXY_and_lowercase_no_proxy_to_loopback_only()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["NO_PROXY"] = "some.internal.contoso.com";
        startInfo.Environment["no_proxy"] = "some.internal.contoso.com";

        InheritedEnvironmentFilter.Apply(startInfo);

        Assert.Equal("127.0.0.1,localhost", startInfo.Environment["NO_PROXY"]);
        Assert.Equal("127.0.0.1,localhost", startInfo.Environment["no_proxy"]);
    }

    [Fact]
    public void Apply_when_no_inherited_vars_present_still_pins_NO_PROXY()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment.Clear();
        startInfo.Environment["PATH"] = "C:\\some\\path";

        InheritedEnvironmentFilter.Apply(startInfo);

        Assert.Equal("C:\\some\\path", startInfo.Environment["PATH"]);
        Assert.Equal(InheritedEnvironmentFilter.NoProxyValue, startInfo.Environment["NO_PROXY"]);
        Assert.Equal(InheritedEnvironmentFilter.NoProxyValue, startInfo.Environment["no_proxy"]);
    }
}
