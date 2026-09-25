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
    /// <summary>
    /// Real production timing and wall-clock time (every timer/clock override left unset) --
    /// used by most scenarios. `CONFORMANCE_TEST_HOOKS` is still set to unlock the handful of
    /// test-only *behaviours* (not timers) gated on it -- currently just the Python backend's
    /// `response.create` client-allow-list entry (swigerb/SonicAIDriveThru#31 review round 2,
    /// "S1"): the real frontend never sends it, so it's production-gated behind this flag, and
    /// this suite's scenarios rely on sending it as a same-effect stand-in for a server-VAD
    /// triggered turn. Setting ONLY this flag, with none of the `CONFORMANCE_*_SECONDS`/
    /// `CONFORMANCE_FIXED_NOW` overrides below, has no effect on any timer or clock read --
    /// `conformance_hooks.now()`/`seconds()` only change behaviour when the *specific* override
    /// variable is ALSO set (app/backend/conformance_hooks.py's own module docstring).
    /// </summary>
    public static BackendProfile Default { get; } = new("Default", new Dictionary<string, string>
    {
        ["CONFORMANCE_TEST_HOOKS"] = "1",
    });

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
    /// actually assert (nudge timing and rate-limit delays are untouched). PR #54 review: also
    /// raised CONFORMANCE_FIRST_FRAME_TIMEOUT_SECONDS from 0.5s to 2s (production's own default,
    /// per README's config table) -- the 0.5s value left ResumeHandshakeTests'
    /// WellUnderFirstFrameTimeout bound (400ms) only ~100ms of margin under the same load, which
    /// is exactly the kind of thin-margin flake this profile already exists to eliminate.
    ///
    /// PR #52 review ("reconcile ResumeTimers with ResumeMargin"): <see cref="ResumeMargin"/>
    /// below addresses the same root cause (session-setup scheduling slowness racing a short
    /// idle/grace budget) for a different, non-overlapping set of scenarios
    /// (<c>ResumeRehydrationClientVisibilityTests</c>, <c>WholeSessionLeakTests</c>' resume case)
    /// that don't exercise first-frame-timeout or rate-limit-retry timing at all. This profile
    /// is kept separate rather than folded into <see cref="ResumeMargin"/> because its consumers
    /// *do* need FIRST_FRAME_TIMEOUT and the two RATE_LIMIT_RETRY_DELAY vars held short (they're
    /// asserted on directly) -- adopting ResumeMargin's narrower env-var set (idle/grace/nudge/
    /// sweep only, everything else at production defaults) here would silently put those
    /// assertions back on multi-second production timers. Conversely, widening ResumeMargin to
    /// this profile's full var set would give its own consumers timers they never asked for and
    /// don't need, without fixing anything for them.
    /// </summary>
    public static BackendProfile ResumeTimers { get; } = new("ResumeTimers", new Dictionary<string, string>
    {
        ["CONFORMANCE_TEST_HOOKS"] = "1",
        ["CONFORMANCE_IDLE_TIMEOUT_SECONDS"] = "8",
        ["CONFORMANCE_GRACE_SECONDS"] = "8",
        ["CONFORMANCE_NUDGE_AFTER_SECONDS"] = "1",
        ["CONFORMANCE_FIRST_FRAME_TIMEOUT_SECONDS"] = "2",
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
    ///
    /// See <see cref="ResumeTimers"/>'s own doc comment for why that (older, #10/#26) profile
    /// stays separate rather than being unified with this one.
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

    /// <summary>
    /// CONFORMANCE_TEST_HOOKS left completely unset -- the exact shape of a real deployment (PR
    /// #49 review round 2 follow-up, "G1"). Every other profile above sets
    /// CONFORMANCE_TEST_HOOKS=1 so the suite's many scenarios can keep using a browser-sent
    /// `response.create` as a same-effect stand-in for a server-VAD-triggered turn -- which is
    /// exactly why none of them can prove the S1 gate (`rtmt.py`'s
    /// `conformance_hooks.hooks_enabled_now()` check in `_filter_client_to_server`) actually does
    /// anything: they all run against a backend where it's unconditionally true. This profile
    /// gets its own dedicated collection/process (see
    /// tests/conformance/tests/Conformance.Tests/BackendProfileFixtures.cs's
    /// HooksOffConformanceFixture) specifically so
    /// Scenarios/Security/ResponseCreateHooksGateTests.cs can black-box prove a browser-sent
    /// `response.create` is dropped -- never reaching the fake upstream -- when hooks are off,
    /// the same as a genuine production deployment.
    /// </summary>
    public static BackendProfile HooksOff { get; } = new("HooksOff", new Dictionary<string, string>());
}
