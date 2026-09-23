namespace Conformance.Harness;

/// <summary>
/// Pure heuristic (PR #22 review item 17) for telling a TCP port-bind race — <see cref="NetworkUtils.GetFreeTcpPort"/>
/// picked a port that something else grabbed before the Python backend's own bind could claim it
/// — apart from a real backend crash. Only the former is worth retrying with a fresh port;
/// retrying the latter would just hide a real bug behind a slow, flaky-looking pass on the Nth
/// attempt, so both an early-exit *and* a matching bind-failure message are required.
/// </summary>
public static class PortRaceDetection
{
    /// <summary>
    /// How soon after starting the process must exit for an early exit to even be considered a
    /// possible port race. A real backend crash after code has had a chance to run (import
    /// errors, config validation, a missing env var) also exits "early" in absolute terms but is
    /// not a port race and must not be retried into a false pass. Widened to 20s (not, say, 5s)
    /// because the real Python startup path does non-fatal reachability probes against the
    /// upstream Azure OpenAI/Search endpoints (each with its own ~2s timeout) *before* attempting
    /// its own socket bind — a real port-race exit can therefore legitimately take several
    /// seconds to surface, confirmed empirically against the real backend while validating this
    /// heuristic (PR #22 review item 17).
    /// </summary>
    public static readonly TimeSpan RaceDetectionWindow = TimeSpan.FromSeconds(20);

    /// <summary>
    /// True only when the process exited within <see cref="RaceDetectionWindow"/> of starting AND
    /// its captured stdout/stderr matches a known TCP bind-failure signature from Python's
    /// <c>asyncio</c>/<c>aiohttp</c> stack (Linux <c>errno 98</c>, Windows <c>WinError
    /// 10048</c>/<c>10013</c>).
    /// </summary>
    public static bool ShouldRetry(TimeSpan elapsedSinceStart, string capturedOutput) =>
        elapsedSinceStart < RaceDetectionWindow && LooksLikePortBindFailure(capturedOutput);

    internal static bool LooksLikePortBindFailure(string capturedOutput)
    {
        if (string.IsNullOrEmpty(capturedOutput))
        {
            return false;
        }

        ReadOnlySpan<string> signatures =
        [
            "address already in use",
            "[errno 98]",
            "winerror 10048",
            "winerror 10013",
            "only one usage of each socket address",
        ];

        foreach (var signature in signatures)
        {
            if (capturedOutput.Contains(signature, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
