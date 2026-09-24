namespace Conformance.Harness;

/// <summary>
/// Detects whether the suite is running on a CI runner, so behaviours that must never be
/// silently softened in CI (e.g. the CONFORMANCE_BACKEND=dotnet placeholder failing instead of
/// skipping -- PR #22 review item 15) can tell a developer's own machine apart from a runner.
/// </summary>
public static class CiEnvironment
{
    /// <summary>
    /// True when GitHub Actions' own CI env vars are present. GITHUB_ACTIONS=true is set
    /// unconditionally by every GitHub-hosted and self-hosted Actions runner and nothing else,
    /// so it is checked first as the most specific signal; the more generic CI=true (a de facto
    /// convention many CI systems set) is checked as a fallback in case this suite is ever run
    /// under a different CI provider.
    /// </summary>
    public static bool IsCi =>
        IsTruthy(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"))
        || IsTruthy(Environment.GetEnvironmentVariable("CI"));

    private static bool IsTruthy(string? value) =>
        string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
}
