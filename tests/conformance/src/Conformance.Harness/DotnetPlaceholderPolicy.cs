namespace Conformance.Harness;

/// <summary>
/// Pure decision logic for whether CONFORMANCE_BACKEND=dotnet should skip the suite or fail it
/// (PR #22 review item 15). Extracted out of <see cref="BackendLauncherFactory"/> so the
/// CI-safety rule -- skipping the S2 .NET-backend placeholder is only ever allowed on a
/// developer's own machine, never in CI, and only when explicitly opted into -- is unit-testable
/// as a plain function of its inputs, without needing to mutate real process environment
/// variables (which are global mutable state and unsafe to flip from a parallel test run).
/// </summary>
public static class DotnetPlaceholderPolicy
{
    public const string ReasonPrefix =
        "CONFORMANCE_BACKEND=dotnet is a placeholder until the S2 .NET backend exists (see issue #7).";

    /// <summary>
    /// True only when a developer has explicitly opted in (CONFORMANCE_ALLOW_SKIP=1) AND this
    /// does not look like a CI runner. Both conditions are required: CI must never silently skip
    /// real backend coverage no matter what a stray/inherited env var says -- there is
    /// deliberately no way to force a skip in CI.
    /// </summary>
    public static bool ShouldSkip(string? conformanceAllowSkip, bool isCi) =>
        !isCi && conformanceAllowSkip?.Trim() == "1";

    /// <summary>
    /// Builds the exception <see cref="BackendLauncherFactory"/> should throw for
    /// CONFORMANCE_BACKEND=dotnet: <see cref="ConformanceBackendNotImplementedException"/> (which
    /// <see cref="ConformanceFixture"/> turns into a skip) when <see cref="ShouldSkip"/> allows
    /// it, otherwise <see cref="ConformanceBackendUnavailableException"/> (which is never caught
    /// specially, so it fails every test in the collection).
    /// </summary>
    public static Exception BuildException(string? conformanceAllowSkip, bool isCi) =>
        ShouldSkip(conformanceAllowSkip, isCi)
            ? new ConformanceBackendNotImplementedException(
                ReasonPrefix + " Skipping because CONFORMANCE_ALLOW_SKIP=1 and this doesn't look " +
                "like CI -- set CONFORMANCE_BACKEND=python (default) or CONFORMANCE_BACKEND_URL to " +
                "actually run this suite.")
            : new ConformanceBackendUnavailableException(
                ReasonPrefix + " This FAILS the suite by default so CI can never silently skip " +
                "real backend coverage -- set CONFORMANCE_ALLOW_SKIP=1 on your own machine " +
                "(detected via GITHUB_ACTIONS/CI env vars and always ignored there) if you " +
                "deliberately want to skip instead.");
}
