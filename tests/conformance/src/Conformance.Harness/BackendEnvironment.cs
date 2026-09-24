using System.Security.Cryptography;

namespace Conformance.Harness;

/// <summary>
/// Python-launcher-specific extras layered on top of the neutral <see cref="BackendContract"/> —
/// knobs that only make sense for *this* launcher (env var overrides for CONFORMANCE_TEST_HOOKS).
/// A future .NET launcher (S2) would have its own equivalent options type instead of reusing this
/// one, while both share the same <see cref="BackendContract"/>.
/// </summary>
public sealed class PythonBackendOptions
{
    /// <summary>
    /// Extra environment variables layered on top of the defaults — used to enable
    /// CONFORMANCE_TEST_HOOKS=1 plus its overrides (fixed clock, short timers) for scenarios
    /// that need them. Empty by default so most scenarios run against real production timing.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExtraEnvironment { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>
/// Builds the exact environment variable set `app/backend/app.py` needs to start against the
/// fakes, tracing every env var it reads (see app.py / order_state.py / tools.py / rtmt.py).
/// Always sets RUNNING_IN_PRODUCTION=true so the backend never calls `load_dotenv()` and picks
/// up a developer's local `.env` — every value the process needs is set explicitly here instead,
/// which keeps the harness deterministic regardless of what's on a given machine.
/// </summary>
public static class BackendEnvironment
{
    public static Dictionary<string, string> Build(BackendContract contract, PythonBackendOptions options)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ── Neutral BackendContract: any backend implementation needs these. PR #22 review
            // item N8 moved RUNNING_IN_PRODUCTION / LOG_LEVEL / APP_SESSION_SECRET /
            // RATE_LIMIT_RECOVERY_ENABLED into this group — none of their *names* are
            // Python-specific (unlike PYTHONUNBUFFERED/PYTHONUTF8 below), and a future .NET
            // backend under test would equally need "run as if production" behaviour, an
            // explicit log level, its own session-token secret, and rate-limit recovery enabled
            // to match the scenarios this suite drives; only the concrete launcher wiring for a
            // different language would differ, not whether these are needed at all. ──
            ["HOST"] = BackendContract.Host,
            ["PORT"] = contract.Port.ToString(),

            // Key auth (never DefaultAzureCredential/AzureDeveloperCliCredential in CI).
            ["AZURE_OPENAI_EASTUS2_API_KEY"] = BackendContract.OpenAiApiKey,
            ["AZURE_SEARCH_API_KEY"] = BackendContract.SearchApiKey,

            // Point straight at the fakes.
            ["AZURE_OPENAI_EASTUS2_ENDPOINT"] = contract.RealtimeBaseUri.ToString().TrimEnd('/'),
            ["AZURE_OPENAI_REALTIME_DEPLOYMENT"] = contract.Deployment,
            ["AZURE_OPENAI_REALTIME_VOICE_CHOICE"] = contract.Voice,
            ["AZURE_SEARCH_ENDPOINT"] = contract.SearchBaseUri.ToString().TrimEnd('/'),
            ["AZURE_SEARCH_INDEX"] = contract.SearchIndex,
            ["AZURE_SEARCH_SEMANTIC_CONFIGURATION"] = BackendContract.SearchSemanticConfiguration,
            ["AZURE_SEARCH_IDENTIFIER_FIELD"] = BackendContract.SearchIdentifierField,
            ["AZURE_SEARCH_CONTENT_FIELD"] = BackendContract.SearchContentField,
            ["AZURE_SEARCH_EMBEDDING_FIELD"] = BackendContract.SearchEmbeddingField,
            ["AZURE_SEARCH_TITLE_FIELD"] = BackendContract.SearchTitleField,
            ["AZURE_SEARCH_USE_VECTOR_QUERY"] = BackendContract.SearchUseVectorQuery ? "true" : "false",
            ["AZURE_SEARCH_SEMANTIC_RANKER"] = BackendContract.SearchSemanticRanker,
            ["STORE_TIMEZONE"] = contract.StoreTimeZone,
            ["RUNNING_IN_PRODUCTION"] = "true",
            ["LOG_LEVEL"] = "INFO",
            // Single-process HMAC secret; random per launch is fine since only this process
            // ever needs to validate tokens it issued itself.
            ["APP_SESSION_SECRET"] = RandomSecret(),
            ["RATE_LIMIT_RECOVERY_ENABLED"] = "true",

            // ── Python-launcher-specific extras: quirks of this particular process (the
            // CPython interpreter's own env vars), not part of the neutral contract a future
            // .NET launcher would also need to satisfy. ──
            ["PYTHONUNBUFFERED"] = "1",
            ["PYTHONUTF8"] = "1",
        };

        foreach (var (key, value) in options.ExtraEnvironment)
        {
            env[key] = value;
        }

        return env;
    }

    private static string RandomSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}
