using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Starts the two fakes and one backend-under-test once per test run and shares them across all
/// tests in the "Conformance" collection — starting a fresh Python process per test would make
/// the suite too slow to be useful as a fast feedback loop. Individual tests must not depend on
/// each other's socket state (each opens its own <see cref="RealtimeBrowserClient"/> connection).
/// </summary>
public class ConformanceFixture : IAsyncLifetime
{
    /// <summary>
    /// The env-var profile the Python backend is launched with. Default profile (hooks
    /// disabled) — derived fixtures override this to opt into <see cref="BackendProfiles.ShortTimers"/>
    /// or <see cref="BackendProfiles.FixedClock"/> on their own dedicated collection.
    /// </summary>
    protected virtual BackendProfile Profile => BackendProfiles.Default;

    public FakeRealtimeUpstreamServer Realtime { get; } = new();
    public FakeSearchServer Search { get; private set; } = null!;

    /// <summary>Non-null once startup succeeds. Null (with <see cref="SkipReason"/> set) only when
    /// CONFORMANCE_BACKEND=dotnet and <see cref="DotnetPlaceholderPolicy.ShouldSkip"/> allows a skip
    /// (PR #22 review item 15); otherwise a dotnet placeholder run fails <see cref="InitializeAsync"/>
    /// outright instead of reaching this point.</summary>
    public IBackendUnderTest? Backend { get; private set; }

    /// <summary>Set only when CONFORMANCE_BACKEND=dotnet and CONFORMANCE_ALLOW_SKIP=1 outside CI
    /// explicitly opted in (PR #22 review item 15, S2 placeholder for issue #7). Tests must check
    /// this first.</summary>
    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        // External mode (CONFORMANCE_BACKEND_URL) requires the two fakes to bind to fixed,
        // known-in-advance ports -- see ExternalModePortPolicy's own docs for why -- and fails
        // fast with a clear message here (before either fake even starts) if they're missing
        // (PR #22 review item 16).
        var (realtimePort, searchPort) = ExternalModePortPolicy.Resolve(
            Environment.GetEnvironmentVariable("CONFORMANCE_BACKEND_URL"),
            Environment.GetEnvironmentVariable(ExternalModePortPolicy.RealtimePortEnvVar),
            Environment.GetEnvironmentVariable(ExternalModePortPolicy.SearchPortEnvVar));

        // Real end-to-end scenarios always go through the Python backend, which always sends
        // the `api-key` header under key auth (rtmt.py) — so requiring it here exercises PR #22
        // review item 9's "401 on bad/missing api-key" fidelity check on every real scenario for
        // free, with no risk of a false failure (see BackendContract.OpenAiApiKey).
        Realtime.RequireApiKey = true;
        Realtime.ExpectedApiKey = BackendContract.OpenAiApiKey;
        await Realtime.StartAsync(fixedPort: realtimePort).ConfigureAwait(false);

        var repoRoot = RepoPaths.FindRepoRoot();
        Search = new FakeSearchServer(RepoPaths.MenuItemsJsonPath(repoRoot));
        await Search.StartAsync(fixedPort: searchPort).ConfigureAwait(false);

        var port = NetworkUtils.GetFreeTcpPort();
        try
        {
            Backend = await BackendLauncherFactory.StartAsync(
                Realtime.BaseUri, Search.BaseUri, port, extraEnvironment: Profile.ExtraEnvironment)
                .ConfigureAwait(false);
        }
        catch (ConformanceBackendNotImplementedException ex)
        {
            SkipReason = ex.Message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Backend is not null)
        {
            await Backend.DisposeAsync().ConfigureAwait(false);
        }
        if (Search is not null)
        {
            await Search.DisposeAsync().ConfigureAwait(false);
        }
        await Realtime.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Wraps a scenario body so any failure carries the backend's captured stdout/stderr in the
    /// exception message — xUnit displays inner-exception text on failure without needing
    /// ITestOutputHelper plumbing through every scenario.
    /// </summary>
    public async Task RunAsync(Func<Task> body)
    {
        if (SkipReason is not null)
        {
            Assert.Skip(SkipReason);
            return;
        }

        try
        {
            await body().ConfigureAwait(false);
        }
        catch (Exception ex) when (Backend is not null)
        {
            throw new InvalidOperationException(
                $"{ex.Message}\n\n--- backend stdout/stderr ---\n{Backend.DumpDiagnostics()}", ex);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ConformanceCollection : ICollectionFixture<ConformanceFixture>
{
    public const string Name = "Conformance";
}
