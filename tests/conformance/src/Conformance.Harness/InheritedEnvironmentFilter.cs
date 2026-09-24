using System.Diagnostics;

namespace Conformance.Harness;

/// <summary>
/// Strips ambient/inherited environment variables that must never leak from the test process
/// into a launched backend-under-test process, regardless of language/launcher. Any launcher
/// (Python today, a future .NET launcher per issue #7's S2) should call <see cref="Apply"/> on
/// its <see cref="ProcessStartInfo"/> before layering its own explicit env vars on top, so those
/// explicit values always win over anything merely inherited.
///
/// This exists because <c>new ProcessStartInfo(...).Environment</c> starts out pre-populated
/// with a *copy of the current process's entire environment* (not a blank slate) — so without
/// this step, anything ambient in the coordinator's or CI runner's shell (a leftover
/// CONFORMANCE_TEST_HOOKS=1 from a prior manual run, an AZURE_SUBSCRIPTION_ID from an unrelated
/// `az account set`, or the corporate HTTP(S)_PROXY the NuGet/npm/pip proxy setup relies on)
/// would silently leak into the backend-under-test process on top of what the harness sets
/// explicitly (PR #22 review item 13).
/// </summary>
public static class InheritedEnvironmentFilter
{
    /// <summary>Every backend under test only ever needs to reach the fakes and itself, both on loopback.</summary>
    public const string NoProxyValue = "127.0.0.1,localhost";

    private static readonly string[] StripPrefixes = ["CONFORMANCE_", "AZURE_", "VERBOSE_"];

    /// <summary>
    /// Removes any inherited env var matching CONFORMANCE_*, AZURE_*, VERBOSE_* (prefixes) or
    /// *_PROXY (suffix) — case-insensitively, since Windows env var names are — then pins
    /// NO_PROXY/no_proxy to <see cref="NoProxyValue"/> so the child process's own outbound HTTP
    /// calls (to the fakes, always loopback) can never get routed through an inherited corporate
    /// forward proxy.
    /// </summary>
    public static void Apply(ProcessStartInfo startInfo)
    {
        var keysToRemove = startInfo.Environment.Keys
            .Where(key =>
                StripPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
                key.EndsWith("_PROXY", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in keysToRemove)
        {
            startInfo.Environment.Remove(key);
        }

        startInfo.Environment["NO_PROXY"] = NoProxyValue;
        startInfo.Environment["no_proxy"] = NoProxyValue;
    }
}
