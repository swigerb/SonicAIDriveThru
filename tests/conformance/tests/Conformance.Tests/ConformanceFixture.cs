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
        // Non-Default profile collections must skip themselves in external mode, before either
        // fake is started, so they never race the Default collection (or each other) to bind the
        // same fixed fake ports, and never assume an external, already-running backend happens to
        // match a profile it was never configured for (PR #22 review item N6).
        SkipReason = ExternalModeProfilePolicy.ShouldSkip(
            Environment.GetEnvironmentVariable("CONFORMANCE_BACKEND_URL"),
            Profile.Name,
            BackendProfiles.Default.Name);
        if (SkipReason is not null)
        {
            return;
        }

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

    /// <summary>How long a scenario's connections get to finish closing before <see
    /// cref="RunAsync"/> gives up and fails with a clear message — matches the <c>FrameTimeout</c>
    /// convention used throughout the scenario tests themselves (PR #22 review item N3).</summary>
    private static readonly TimeSpan ScenarioTeardownTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Wraps a scenario body so any failure carries the backend's captured stdout/stderr in the
    /// exception message — xUnit displays inner-exception text on failure without needing
    /// ITestOutputHelper plumbing through every scenario. Equivalent to
    /// <c>RunAsync(body, expectedNewUnhandledErrors: 0)</c>.
    /// </summary>
    public Task RunAsync(Func<Task> body) => RunAsync(body, expectedNewUnhandledErrors: 0);

    /// <summary>
    /// Same as <see cref="RunAsync(Func{Task})"/>, but lets a scenario declare that its body is
    /// expected to cause exactly <paramref name="expectedNewUnhandledErrors"/> additional
    /// unhandled-error log lines relative to the baseline captured before it runs (PR #38 review
    /// item 2). A caught-and-reported application-level tool exception (see
    /// ToolErrorSessionSurvivesTests's README-documented "one ERROR" contract) is expected to log
    /// exactly one such line even in a correct implementation — without this overload, the
    /// zero-new-errors invariant below would itself block an otherwise-passing scenario from ever
    /// passing, which is exactly what PR #38's Rick review flagged: "the body passes and only the
    /// error count blocked it."
    /// </summary>
    public async Task RunAsync(Func<Task> body, int expectedNewUnhandledErrors)
    {
        if (SkipReason is not null)
        {
            Assert.Skip(SkipReason);
            return;
        }

        // Asserted BEFORE the scenario runs, not after: a leaked RejectNextConnectionWith or
        // SuppressSessionUpdatedOnNextConnection from a *previous* scenario would otherwise
        // misfire against *this* scenario's own connection attempt, and the resulting failure
        // would point at this scenario's assertions instead of the real, earlier cause (PR #22
        // review item N9). See FakeRealtimeUpstreamServer.AssertNoPendingOneShotSwitches.
        Realtime.AssertNoPendingOneShotSwitches();

        // Same reasoning as above, for FakeSearchServer.RejectSelectFieldOnce (PR #38 review
        // item 8) — a leaked one-shot search-rejection flag from a previous scenario must not be
        // allowed to silently misfire against this scenario's own search request instead.
        Search.AssertNoPendingOneShotSwitches();

        // Captured BEFORE the scenario runs, not after: only a handler fault recorded on a
        // connection accepted at or after this point belongs to *this* scenario. A connection an
        // earlier scenario accepted can still be asynchronously tearing down (e.g. its
        // RespondAsync loop reacting to that scenario's browser dropping mid-stream) when this
        // scenario starts — without this watermark, that connection's eventual, entirely expected
        // "socket already closing" outcome would otherwise get attributed to whichever scenario
        // happened to call AssertNoHandlerFaults first, not the one that actually caused it (PR
        // #22 review item N3).
        var connectionWatermark = Realtime.ConnectionWatermark;

        // Baseline captured BEFORE the scenario runs, not compared against zero: the backend
        // process is shared across every test in this collection (starting a fresh Python
        // process per test would make the suite too slow), so an earlier scenario's own
        // deliberately-triggered backend error (e.g. a handshake-rejection or malformed-frame
        // test) would otherwise permanently poison every later scenario's "zero errors" check
        // with a stale, unrelated count. Comparing to a per-scenario baseline delta instead
        // makes the invariant "this scenario introduced no new unhandled backend errors" --
        // which is what review item N5 actually wants -- immune to run order (PR #22 review
        // item N5).
        var baselineUnhandledErrors = Backend?.UnhandledErrorCount() ?? 0;

        try
        {
            await body().ConfigureAwait(false);

            // Let every connection this scenario touched actually finish closing before checking
            // for handler faults. A graceful drop (e.g. this scenario's own browser client
            // disposing at the end of its `await using` block) does not mean the *backend's*
            // upstream connection to the fake has finished tearing down yet — that happens
            // asynchronously, on the backend's own schedule, once it notices the browser
            // disconnected. Asserting faults immediately after the scenario body returns risked
            // missing a fault that hadn't been recorded yet (this scenario would wrongly pass) and
            // then discovering it later, misattributed to whichever *next* scenario happened to
            // call AssertNoHandlerFaults first (PR #22 review item N3).
            var settled = await Realtime.WaitForNoOpenConnectionsAsync(ScenarioTeardownTimeout).ConfigureAwait(false);
            if (!settled)
            {
                var stillOpen = string.Join(", ", Realtime.OpenConnectionIds);
                throw new InvalidOperationException(
                    $"{Realtime.OpenConnectionIds.Count} upstream connection(s) were still open " +
                    $"{ScenarioTeardownTimeout} after this scenario's body returned: [{stillOpen}]. " +
                    "A handler is still running (or a browser client this scenario opened was " +
                    "never closed) -- this must settle before handler faults can be checked " +
                    "reliably.");
            }

            // Surfaces any *genuine* handler fault recorded during the scenario (a throwing
            // script rule, or a bug in a built-in dispatch case) even when the scenario's own
            // assertions all happened to pass -- see FakeRealtimeUpstreamServer.AssertNoHandlerFaults
            // (item N3). Scoped to connections this scenario itself accepted (the watermark
            // above) so a fault from an earlier scenario's already-settled teardown can never
            // fail this one either.
            Realtime.AssertNoHandlerFaults(since: connectionWatermark);

            // Language-neutral, fixture-wide equivalent of "backend logged no (unexpected)
            // traceback" (item N5): a future C# backend under test reports the same
            // baseline-plus-delta contract without ever producing a Python-shaped traceback
            // string. Most scenarios pass expectedNewUnhandledErrors: 0 (via the single-arg
            // RunAsync overload); a scenario that deliberately provokes one caught-and-reported
            // tool exception passes 1 instead (PR #38 review item 2).
            if (Backend is not null)
            {
                Assert.Equal(baselineUnhandledErrors + expectedNewUnhandledErrors, Backend.UnhandledErrorCount());
            }
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
