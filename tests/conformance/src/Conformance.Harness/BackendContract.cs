namespace Conformance.Harness;

/// <summary>
/// The neutral, language-agnostic contract every backend-under-test must satisfy to run against
/// the conformance fakes — the facts ANY backend implementation (Python today, the future .NET
/// backend once S2 exists per issue #7) needs to know, independent of how a specific launcher
/// wires them in (env vars for Python; appsettings/env for .NET; etc). Per-launcher extras (e.g.
/// <see cref="PythonBackendOptions"/>) layer their own language-specific knobs on top of this.
/// See tests/conformance/README.md's "BackendContract" table for the full HOST/PORT/`/health`
/// shape and shared-file list too.
/// </summary>
public sealed record BackendContract(
    Uri RealtimeBaseUri,
    Uri SearchBaseUri,
    int Port,
    string Deployment,
    string Voice,
    string SearchIndex,
    string StoreTimeZone)
{
    public const string DefaultDeployment = "gpt-realtime-2.1-conformance";
    public const string DefaultVoice = "marin";
    public const string DefaultSearchIndex = "menu-index";
    public const string DefaultStoreTimeZone = "America/Chicago";

    /// <summary>Every backend under test binds to loopback only — the fakes and the suite never need to be reachable off-box.</summary>
    public const string Host = "127.0.0.1";

    /// <summary>
    /// The fixed `AZURE_OPENAI_EASTUS2_API_KEY`-equivalent value every backend must send as the
    /// upstream realtime `api-key` header under key auth. Exposed here (not per-launcher) so
    /// <c>ConformanceFixture</c> can configure <see cref="Conformance.Fakes.FakeRealtimeUpstreamServer.ExpectedApiKey"/>
    /// to the exact same value instead of duplicating the literal, regardless of which launcher is active.
    /// </summary>
    public const string OpenAiApiKey = "conformance-test-openai-key";

    /// <summary>The fixed Azure AI Search API key value, analogous to <see cref="OpenAiApiKey"/>.</summary>
    public const string SearchApiKey = "conformance-test-search-key";

    public const string SearchSemanticConfiguration = "menuSemanticConfig";
    public const string SearchIdentifierField = "id";
    public const string SearchContentField = "description";
    public const string SearchEmbeddingField = "embedding";
    public const string SearchTitleField = "name";
    public const bool SearchUseVectorQuery = true;
    public const string SearchSemanticRanker = "standard";

    public static BackendContract ForPort(Uri realtimeBaseUri, Uri searchBaseUri, int port, string? deployment = null) => new(
        RealtimeBaseUri: realtimeBaseUri,
        SearchBaseUri: searchBaseUri,
        Port: port,
        Deployment: deployment ?? DefaultDeployment,
        Voice: DefaultVoice,
        SearchIndex: DefaultSearchIndex,
        StoreTimeZone: DefaultStoreTimeZone);
}
