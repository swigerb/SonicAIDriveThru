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
    /// Hooks enabled for real-browser scenarios: real page navigation, a real ARIA-role button
    /// click, real getUserMedia/AudioWorklet warm-up, and the fake device's first audio chunk all
    /// need genuine wall-clock headroom that ShortTimers' 1-second idle budget can't provide --
    /// session_manager.py's idle clock is driven solely by *guest* activity (touch_activity, only
    /// called for detected speech/transcripts, never by a bare resume) and never resets across a
    /// resume, so it must stay comfortably longer than the slowest realistic pre-first-activity
    /// gap this suite can hit. Mirrors scripts/e2e_order_resume.py's own approach of leaving
    /// idle/grace at safe, generous values and only shortening nudge_after_seconds (there, by
    /// mutating the in-process SessionManager directly; here, via the same env var ShortTimers
    /// uses) so the nudge scenario still finishes in seconds.
    /// </summary>
    public static BackendProfile BrowserTimers { get; } = new("BrowserTimers", new Dictionary<string, string>
    {
        ["CONFORMANCE_TEST_HOOKS"] = "1",
        ["CONFORMANCE_IDLE_TIMEOUT_SECONDS"] = "10",
        ["CONFORMANCE_GRACE_SECONDS"] = "10",
        ["CONFORMANCE_NUDGE_AFTER_SECONDS"] = "2",
        ["CONFORMANCE_FIRST_FRAME_TIMEOUT_SECONDS"] = "3",
        ["CONFORMANCE_GREETING_TIMEOUT_SECONDS"] = "5",
        ["CONFORMANCE_RATE_LIMIT_RETRY_DELAY_SECONDS"] = "0.2",
        ["CONFORMANCE_RATE_LIMIT_SECOND_RETRY_DELAY_SECONDS"] = "0.4",
        ["CONFORMANCE_SWEEP_INTERVAL_SECONDS"] = "0.2",
    });

    /// <summary>
    /// Hooks enabled with generous idle/grace/nudge budgets (like <see cref="BrowserTimers"/>) but
    /// for a specific plain-WebSocket reason: app/backend/audio_pipeline.py's EchoSuppressor
    /// treats every fresh (non-resumed) connection's greeting as real AI speech -- AutoRespond's
    /// default script always answers the greeting's own response.create with an audio delta, so
    /// echo.on_audio_done() always fires and starts a `greeting_in_progress`-doubled cooldown
    /// (config.yaml's audio.echo_cooldown_seconds=1.5, doubled to 3.0s) during which
    /// rtmt.py's from_client_to_server loop silently `continue`s past *every*
    /// input_audio_buffer.append -- it never reaches the fake upstream at all. This is a genuine
    /// anti-echo production safety feature, not a bug, but it means any scenario proving a real
    /// guest-speech signal is observably acted upon (e.g. RateLimitRecovery cancelling a pending
    /// retry) must wait out that cooldown before sending its synthetic append, and ShortTimers'
    /// 1-second idle budget / 1-second nudge would fire (closing the socket, or injecting an
    /// unrelated nudge response.create) long before a ~3s wait could complete. There is no
    /// CONFORMANCE_* hook for echo_cooldown_seconds itself (unlike the timers below), so the test
    /// must actually wait out the fixed 3.0s window in real wall-clock time.
    /// </summary>
    public static BackendProfile RateLimitTimers { get; } = new("RateLimitTimers", new Dictionary<string, string>
    {
        ["CONFORMANCE_TEST_HOOKS"] = "1",
        ["CONFORMANCE_IDLE_TIMEOUT_SECONDS"] = "10",
        ["CONFORMANCE_GRACE_SECONDS"] = "10",
        ["CONFORMANCE_NUDGE_AFTER_SECONDS"] = "10",
        ["CONFORMANCE_FIRST_FRAME_TIMEOUT_SECONDS"] = "3",
        ["CONFORMANCE_GREETING_TIMEOUT_SECONDS"] = "5",
        ["CONFORMANCE_RATE_LIMIT_RETRY_DELAY_SECONDS"] = "0.2",
        ["CONFORMANCE_RATE_LIMIT_SECOND_RETRY_DELAY_SECONDS"] = "0.4",
        ["CONFORMANCE_SWEEP_INTERVAL_SECONDS"] = "0.2",
    });

    /// <summary>
    /// Hooks enabled like <see cref="ShortTimers"/> (same short first-frame-timeout, nudge, and
    /// rate-limit-delay values) but with an 8-second idle/grace budget instead of ShortTimers'
    /// 1-second one. The resume-handshake scenarios (Scenarios/Sessions/ResumeHandshakeTests.cs,
    /// ResumeRehydrationAndNudgeTests.cs) aren't testing idle behaviour at all -- that's
    /// IdleTimeoutTests' and RateLimitRecoveryTests' job -- but several of them are several
    /// WebSocket round-trips deep (send resume, await rejection, await a fresh re-announce, then
    /// prove the socket *doesn't* close in the next 500ms) before their final assertion, and
    /// none of those round-trip frames are guest activity that would reset
    /// session_manager.py's idle clock. Under normal load that easily finishes inside ShortTimers'
    /// 1-second budget with room to spare, but under this suite's own heavy concurrent load
    /// (many collections' Python backends and, in the same run, several real Playwright browser
    /// processes) it can occasionally lose that race to a *genuine* idle-close that has nothing
    /// to do with the behaviour under test -- observed directly on this dev box. Widening the
    /// budget here removes that coincidental race without changing what any of these scenarios
    /// actually assert (first-frame-timeout, nudge timing and rate-limit delays are untouched).
    /// </summary>
    public static BackendProfile ResumeTimers { get; } = new("ResumeTimers", new Dictionary<string, string>
    {
        ["CONFORMANCE_TEST_HOOKS"] = "1",
        ["CONFORMANCE_IDLE_TIMEOUT_SECONDS"] = "8",
        ["CONFORMANCE_GRACE_SECONDS"] = "8",
        ["CONFORMANCE_NUDGE_AFTER_SECONDS"] = "1",
        ["CONFORMANCE_FIRST_FRAME_TIMEOUT_SECONDS"] = "0.5",
        ["CONFORMANCE_GREETING_TIMEOUT_SECONDS"] = "1",
        ["CONFORMANCE_RATE_LIMIT_RETRY_DELAY_SECONDS"] = "0.2",
        ["CONFORMANCE_RATE_LIMIT_SECOND_RETRY_DELAY_SECONDS"] = "0.4",
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
