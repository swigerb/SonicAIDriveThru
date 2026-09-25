namespace Conformance.Harness;

/// <summary>
/// Which backend language is currently under test, per `CONFORMANCE_BACKEND` (defaults to
/// `python`) -- the single source of truth scenarios use to decide whether a "known Python bug"
/// `[Fact(Skip = ...)]` should still apply (PR #38 review should-fix 3). Mirrors
/// <see cref="BackendLauncherFactory"/>'s own env-var normalisation (trim + case-insensitive
/// compare against exactly "dotnet") so this can never drift from what the harness actually
/// launched.
///
/// Also correct in external mode (`CONFORMANCE_BACKEND_URL`):
/// <see cref="BackendLauncherFactory.StartAsync"/> only reads `CONFORMANCE_BACKEND_URL` to decide
/// *how* to reach the backend (skip the launch step entirely, return an
/// <c>ExternalBackend</c> immediately) -- it never stops honouring `CONFORMANCE_BACKEND` as the
/// separate "which language is actually running at that URL" signal an operator sets alongside
/// it. `OrderScenarioHelpers.MoneyTolerance` already relies on this same interpretation ("an
/// external URL pointed at a Python instance"); this type gives every other scenario file the
/// identical check without each one re-deriving it from the raw environment variable.
///
/// Deliberately permissive rather than validating: any value other than an exact "dotnet" (the
/// default "python", an unset variable, or a typo) is treated as Python, so a misspelled
/// `CONFORMANCE_BACKEND` can never silently *stop* a "known Python bug" scenario from running --
/// only a deliberate, exact opt-in to "dotnet" turns these skips off. (An unrecognised value would
/// separately fail loudly at backend-launch time via `BackendLauncherFactory`'s own validation in
/// non-external mode; this type does not duplicate that validation.)
/// </summary>
public static class BackendUnderTest
{
    public static bool IsPython =>
        !string.Equals(
            (Environment.GetEnvironmentVariable("CONFORMANCE_BACKEND") ?? "python").Trim(),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);
}
