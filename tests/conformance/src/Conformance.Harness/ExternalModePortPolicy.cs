namespace Conformance.Harness;

/// <summary>
/// Pure validation for the fixed fake ports required when running against an external,
/// already-started backend (CONFORMANCE_BACKEND_URL) -- PR #22 review item 16.
///
/// The two Kestrel fakes (FakeRealtimeUpstreamServer, FakeSearchServer) normally bind to
/// "http://127.0.0.1:0" and let the OS pick a free port every run. That's fine when the harness
/// itself launches and configures the backend process -- it just reads the chosen port back and
/// passes it into the child process's environment -- but it's fundamentally incompatible with
/// external mode: a backend process started *outside* the harness's control has no way to
/// discover a randomly-chosen port after the fact. External mode therefore requires the fake
/// ports to be fixed and known in advance via CONFORMANCE_FAKE_REALTIME_PORT /
/// CONFORMANCE_FAKE_SEARCH_PORT, so whoever starts the external backend can configure it to point
/// at the same two values -- and this fails loudly and explicitly, rather than silently running
/// against random ports the external backend can never match, when they are missing.
/// </summary>
public static class ExternalModePortPolicy
{
    public const string RealtimePortEnvVar = "CONFORMANCE_FAKE_REALTIME_PORT";
    public const string SearchPortEnvVar = "CONFORMANCE_FAKE_SEARCH_PORT";

    /// <summary>
    /// Resolves the fixed ports (if any) the two fakes should bind to, given the raw
    /// CONFORMANCE_BACKEND_URL / CONFORMANCE_FAKE_REALTIME_PORT / CONFORMANCE_FAKE_SEARCH_PORT
    /// environment values. In external mode (backendUrl non-empty) both port values must be
    /// present and valid TCP port numbers or this throws InvalidOperationException with an
    /// actionable message naming the missing/invalid variable; in non-external mode either or
    /// both may be omitted (a null result means "let the OS pick a free port", which is safe
    /// because the harness controls the launched backend's environment either way), but if
    /// supplied they are still honoured and validated the same way (useful for a developer who
    /// wants deterministic, inspectable ports while still using CONFORMANCE_BACKEND=python).
    /// </summary>
    public static (int? RealtimePort, int? SearchPort) Resolve(
        string? backendUrl, string? realtimePortRaw, string? searchPortRaw)
    {
        var isExternal = !string.IsNullOrWhiteSpace(backendUrl);
        var realtimePort = ParsePort(realtimePortRaw, RealtimePortEnvVar, isExternal);
        var searchPort = ParsePort(searchPortRaw, SearchPortEnvVar, isExternal);
        return (realtimePort, searchPort);
    }

    private static int? ParsePort(string? raw, string envVarName, bool isExternal)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (isExternal)
            {
                throw new InvalidOperationException(
                    $"CONFORMANCE_BACKEND_URL is set (external mode) but {envVarName} is not. " +
                    "The fakes must bind to fixed, known-in-advance ports in external mode so " +
                    "the already-running backend can be configured to point at them -- set both " +
                    $"{RealtimePortEnvVar} and {SearchPortEnvVar} to free TCP ports before " +
                    "starting the external backend, then run the suite with the same values set.");
            }
            return null;
        }

        if (!int.TryParse(raw.Trim(), out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"{envVarName}='{raw}' is not a valid TCP port number (expected an integer 1-65535).");
        }

        return port;
    }
}
