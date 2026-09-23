using System.Security.Cryptography;

namespace Conformance.Harness;

/// <summary>Knobs a test can set before launching the Python backend under test.</summary>
public sealed class PythonBackendOptions
{
    public required Uri RealtimeBaseUri { get; init; }
    public required Uri SearchBaseUri { get; init; }
    public required int Port { get; init; }

    public string Deployment { get; init; } = "gpt-realtime-2.1-conformance";
    public string Voice { get; init; } = "marin";
    public string SearchIndex { get; init; } = "menu-index";
    public string StoreTimeZone { get; init; } = "America/Chicago";

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
    /// <summary>
    /// The fixed `AZURE_OPENAI_EASTUS2_API_KEY` value the Python backend sends as the `api-key`
    /// header on every upstream realtime connection (rtmt.py: `headers = {"api-key": self.key}`
    /// under key auth). Exposed so <c>ConformanceFixture</c> can configure
    /// <c>FakeRealtimeUpstreamServer.ExpectedApiKey</c> to the exact same value instead of
    /// duplicating the literal.
    /// </summary>
    public const string OpenAiApiKey = "conformance-test-openai-key";

    /// <summary>The fixed `AZURE_SEARCH_API_KEY` value, analogous to <see cref="OpenAiApiKey"/>.</summary>
    public const string SearchApiKey = "conformance-test-search-key";

    public static Dictionary<string, string> Build(PythonBackendOptions options)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RUNNING_IN_PRODUCTION"] = "true",
            ["HOST"] = "127.0.0.1",
            ["PORT"] = options.Port.ToString(),
            ["LOG_LEVEL"] = "INFO",
            ["PYTHONUNBUFFERED"] = "1",
            ["PYTHONUTF8"] = "1",

            // Key auth (never DefaultAzureCredential/AzureDeveloperCliCredential in CI).
            ["AZURE_OPENAI_EASTUS2_API_KEY"] = OpenAiApiKey,
            ["AZURE_SEARCH_API_KEY"] = SearchApiKey,

            // Point straight at the fakes.
            ["AZURE_OPENAI_EASTUS2_ENDPOINT"] = options.RealtimeBaseUri.ToString().TrimEnd('/'),
            ["AZURE_OPENAI_REALTIME_DEPLOYMENT"] = options.Deployment,
            ["AZURE_OPENAI_REALTIME_VOICE_CHOICE"] = options.Voice,
            ["AZURE_SEARCH_ENDPOINT"] = options.SearchBaseUri.ToString().TrimEnd('/'),
            ["AZURE_SEARCH_INDEX"] = options.SearchIndex,
            ["AZURE_SEARCH_SEMANTIC_CONFIGURATION"] = "menuSemanticConfig",
            ["AZURE_SEARCH_IDENTIFIER_FIELD"] = "id",
            ["AZURE_SEARCH_CONTENT_FIELD"] = "description",
            ["AZURE_SEARCH_EMBEDDING_FIELD"] = "embedding",
            ["AZURE_SEARCH_TITLE_FIELD"] = "name",
            ["AZURE_SEARCH_USE_VECTOR_QUERY"] = "true",
            ["AZURE_SEARCH_SEMANTIC_RANKER"] = "standard",

            // Single-process HMAC secret; random per launch is fine since only this process
            // ever needs to validate tokens it issued itself.
            ["APP_SESSION_SECRET"] = RandomSecret(),

            ["STORE_TIMEZONE"] = options.StoreTimeZone,
            ["RATE_LIMIT_RECOVERY_ENABLED"] = "true",
        };

        foreach (var (key, value) in options.ExtraEnvironment)
        {
            env[key] = value;
        }

        return env;
    }

    private static string RandomSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}
