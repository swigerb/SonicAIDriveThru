namespace Conformance.Harness;

/// <summary>
/// A named set of extra environment variables layered onto <see cref="BackendEnvironment.Build"/>
/// (via <see cref="PythonBackendOptions.ExtraEnvironment"/>), letting different test collections
/// launch the Python backend under a different test-hook configuration. See
/// tests/conformance/README.md's BackendContract section for the full list of CONFORMANCE_*
/// variables app/backend/conformance_hooks.py understands, and app/backend/conformance_hooks.py
/// itself for the authoritative gating (CONFORMANCE_TEST_HOOKS=1 required; everything here is
/// inert without it).
/// </summary>
public sealed record BackendProfile(string Name, IReadOnlyDictionary<string, string> ExtraEnvironment)
{
    public override string ToString() => Name;
}

/// <summary>
/// The backend profiles available to conformance fixtures. Each profile pairs with its own
/// dedicated xUnit collection (its own Python process) — see
/// tests/conformance/tests/Conformance.Tests/BackendProfileFixtures.cs — because
/// CONFORMANCE_TEST_HOOKS and its overrides are read once at Python module-import time and can't
/// be changed for an already-running backend process.
/// </summary>
public static class BackendProfiles
{
    /// <summary>Hooks disabled — real production timing and wall-clock time. Used by most scenarios.</summary>
    public static BackendProfile Default { get; } = new("Default", new Dictionary<string, string>());

    /// <summary>
    /// Hooks enabled with every timer shortened to about a second, so timer-driven scenarios
    /// (idle timeout, resume grace/nudge, first-frame resume timeout, greeting timeout, rate-limit
    /// retry delays, the session-resume sweep) can complete in seconds instead of minutes. Leaves
    /// CONFORMANCE_FIXED_NOW unset — wall-clock time still advances normally. Pair with
    /// <see cref="FixedClock"/>'s variables manually (via a custom
    /// <see cref="PythonBackendOptions.ExtraEnvironment"/> dictionary) if a scenario needs both
    /// short timers and a frozen clock at once.
    /// </summary>
    public static BackendProfile ShortTimers { get; } = new("ShortTimers", new Dictionary<string, string>
    {
        ["CONFORMANCE_TEST_HOOKS"] = "1",
        ["CONFORMANCE_IDLE_TIMEOUT_SECONDS"] = "1",
        ["CONFORMANCE_GRACE_SECONDS"] = "1",
        ["CONFORMANCE_NUDGE_AFTER_SECONDS"] = "1",
        ["CONFORMANCE_FIRST_FRAME_TIMEOUT_SECONDS"] = "0.5",
        ["CONFORMANCE_GREETING_TIMEOUT_SECONDS"] = "1",
        ["CONFORMANCE_RATE_LIMIT_RETRY_DELAY_SECONDS"] = "0.2",
        ["CONFORMANCE_RATE_LIMIT_SECOND_RETRY_DELAY_SECONDS"] = "0.4",
        ["CONFORMANCE_SWEEP_INTERVAL_SECONDS"] = "0.2",
    });

    /// <summary>
    /// PR #52 CI follow-up (swigerb/SonicAIDriveThru#28 N10 aftermath): <see cref="ShortTimers"/>'s
    /// equal 1s idle timeout and 1s grace both being genuinely tight enough to encounter under
    /// realistic wall-clock work is exactly the point for the scenarios that use it today (the
    /// idle-close and greeting-timeout tests *want* a ~1s window they can wait out). But
    /// <c>session_manager.py</c> computes a detached session's expiry as
    /// <c>min(detached_at + grace_seconds, last_activity + idle_timeout_seconds)</c> — the second
    /// term only leaves real headroom if whatever the scenario does *before* detaching (here:
    /// establishing the session, and for the whole-session-leak scenario also a voice change and a
    /// full tool round trip) reliably finishes in well under a second. On a loaded CI runner it
    /// doesn't: CI run 36085091969 logged "holding order for 0s" on detach, meaning
    /// <c>last_activity + 1s</c> had already elapsed (or nearly had) by the time the *first*
    /// connection's own pre-detach setup finished — so the resume that follows races an
    /// already-expired (or about-to-expire) grace window purely from ordinary scheduling
    /// slowness, not from anything the scenario is actually testing. This profile keeps
    /// <see cref="ShortTimers"/>'s ~1s nudge timer (the resume scenarios' own causal wait depends
    /// on that staying fast) but gives idle timeout and grace real margin above what session
    /// setup should ever take, even under load — a profile with headroom, not a sleep, per the
    /// investigation's own ask. Used by <c>ResumeRehydrationClientVisibilityTests</c> and
    /// <c>WholeSessionLeakTests</c>' resume scenario, both via their own dedicated collection so
    /// this doesn't touch <see cref="ShortTimers"/>'s existing ~1s guarantees for its other
    /// scenarios (the idle-close and greeting-timeout tests).
    /// </summary>
    public static BackendProfile ResumeMargin { get; } = new("ResumeMargin", new Dictionary<string, string>
    {
        ["CONFORMANCE_TEST_HOOKS"] = "1",
        ["CONFORMANCE_IDLE_TIMEOUT_SECONDS"] = "5",
        ["CONFORMANCE_GRACE_SECONDS"] = "5",
        ["CONFORMANCE_NUDGE_AFTER_SECONDS"] = "1",
        ["CONFORMANCE_SWEEP_INTERVAL_SECONDS"] = "0.2",
    });

    /// <summary>
    /// Hooks enabled with the clock frozen at <paramref name="instant"/>, for time-based business
    /// logic such as app/backend/order_state.py's happy-hour pricing. Timers are left at their
    /// production defaults.
    /// </summary>
    public static BackendProfile FixedClock(DateTimeOffset instant) => new(
        $"FixedClock({instant:O})",
        new Dictionary<string, string>
        {
            ["CONFORMANCE_TEST_HOOKS"] = "1",
            ["CONFORMANCE_FIXED_NOW"] = instant.ToString("O"),
        });
}
