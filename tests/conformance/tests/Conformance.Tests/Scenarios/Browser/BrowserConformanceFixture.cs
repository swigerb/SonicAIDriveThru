using Conformance.Fakes;
using Conformance.Harness;
using Microsoft.Playwright;
using Xunit;

namespace Conformance.Tests.Scenarios.Browser;

/// <summary>
/// Issue #26: backs the Category=Browser suite. Wraps (rather than inherits — see below) a
/// BrowserTimers-profile <see cref="ConformanceFixture"/> (see
/// <see cref="BackendProfiles.BrowserTimers"/> for why this isn't ShortTimers) so idle/nudge
/// scenarios still finish in seconds without racing real page-load/click/mic-warm-up latency,
/// plus one shared headless <see cref="IBrowser"/> launched against whichever supported
/// channel (msedge, then chrome) is actually installed on this machine — never downloaded, per
/// <see cref="BrowserChannelPolicy"/>. A genuinely browser-less developer machine gets a clean
/// xUnit skip; CI (which always ships one of these channels) fails loudly instead if it's ever
/// missing, exactly like <see cref="DotnetPlaceholderPolicy"/> handles the S2 .NET-backend
/// placeholder.
///
/// Deliberately wraps <see cref="ConformanceFixture"/> with a private field instead of
/// inheriting from it: <see cref="ConformanceFixture.DisposeAsync"/> is a plain (non-virtual)
/// public method that happens to satisfy <see cref="IAsyncLifetime"/> implicitly, so a derived
/// class cannot safely override just the browser-teardown half of disposal without either
/// silently failing to run the base class's own cleanup (leaking the backend process and fake
/// servers) or relying on an easy-to-get-wrong explicit-reimplementation corner of the language.
/// Composition avoids that risk entirely: this type owns its own <see cref="IAsyncLifetime"/>
/// implementation outright and simply forwards to the inner fixture.
/// </summary>
public sealed class BrowserConformanceFixture : IAsyncLifetime
{
    private sealed class BrowserTimersBackendFixture : ConformanceFixture
    {
        protected override BackendProfile Profile => BackendProfiles.BrowserTimers;
    }

    private readonly ConformanceFixture _inner = new BrowserTimersBackendFixture();
    private string? _browserSkipReason;

    public FakeRealtimeUpstreamServer Realtime => _inner.Realtime;
    public IBackendUnderTest? Backend => _inner.Backend;

    public string? BrowserChannel { get; private set; }
    public IPlaywright? Playwright { get; private set; }
    public IBrowser? Browser { get; private set; }

    public async ValueTask InitializeAsync()
    {
        await _inner.InitializeAsync().ConfigureAwait(false);
        if (_inner.SkipReason is not null)
        {
            return; // The backend itself is skipping (e.g. the S2 dotnet placeholder) — nothing more to do.
        }

        BrowserChannel = BrowserChannelPolicy.DetectInstalledChannel();
        if (BrowserChannelPolicy.ShouldSkip(BrowserChannel, CiEnvironment.IsCi))
        {
            _browserSkipReason = BrowserChannelPolicy.BuildMessage(BrowserChannel, CiEnvironment.IsCi);
            return;
        }
        if (BrowserChannel is null)
        {
            // Not skip-eligible (this looks like CI) — fail every test in the collection loudly,
            // exactly like DotnetPlaceholderPolicy's CI branch, rather than skip silently.
            throw new InvalidOperationException(BrowserChannelPolicy.BuildMessage(BrowserChannel, CiEnvironment.IsCi));
        }

        Playwright = await Microsoft.Playwright.Playwright.CreateAsync().ConfigureAwait(false);
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = BrowserChannel,
            Headless = true,
            Args = ["--use-fake-ui-for-media-stream", "--use-fake-device-for-media-stream"],
        }).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.CloseAsync().ConfigureAwait(false);
        }
        Playwright?.Dispose();
        await _inner.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Wraps a scenario body exactly like <see cref="ConformanceFixture.RunAsync"/> (whose
    /// diagnostics-on-failure and no-leaked-connections checks this still gets, via
    /// delegation) but additionally honours a missing-browser-channel skip, and re-verifies (never
    /// blindly swallows) a "new backend error(s) above the baseline" failure that turns out to be
    /// composed entirely of <see cref="IsBenignProactorTeardownIncident"/> matches (see that
    /// method's own doc comment for the root cause and evidence).
    ///
    /// This test category is the only one in the suite that drives a *real* browser (Playwright's
    /// Chromium) against a live `/realtime` WebSocket, and both a genuine page reload
    /// (<c>page.ReloadAsync()</c>, itself part of what several of these scenarios legitimately
    /// need to exercise -- e.g. "reload restores the session") and ordinary Playwright
    /// <c>IBrowserContext</c>/page teardown at the end of a test sever that live socket the same
    /// abrupt way a real browser tab closing or navigating away would (WebSocket close code 1001,
    /// "going away"), not the clean, explicit code-1000 close every other scenario's plain .NET
    /// <c>RealtimeBrowserClient</c> uses. On Windows only, that abrupt severance can race CPython's
    /// ProactorEventLoop teardown path and log one benign, well-known
    /// <c>ConnectionResetError: [WinError 10054]</c> that has nothing to do with this scenario's
    /// own assertions or with rtmt.py's own application logic -- see
    /// <see cref="IsBenignProactorTeardownIncident"/>. Swallowing that specific, positively
    /// re-verified failure here (rather than loosening <see cref="ConformanceFixture.RunAsync"/>'s
    /// shared, zero-tolerance check, or granting every Browser scenario a blind numeric error
    /// allowance that could just as easily mask two unrelated real bugs) keeps every other
    /// scenario in the suite -- Browser or not -- exactly as strict as before.
    /// </summary>
    public async Task RunAsync(Func<Task> body)
    {
        if (_browserSkipReason is not null)
        {
            Assert.Skip(_browserSkipReason);
            return;
        }

        var filteredBefore = Backend?.UnhandledErrorCount(IsBenignProactorTeardownIncident) ?? 0;
        try
        {
            await _inner.RunAsync(body).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (
            Backend is not null &&
            ex.Message.Contains("new backend error(s) above the baseline", StringComparison.Ordinal) &&
            Backend.UnhandledErrorCount(IsBenignProactorTeardownIncident) == filteredBefore)
        {
            // _inner.RunAsync's own unfiltered check just failed on *something* new, but
            // re-counting with the benign-aware filter shows the filtered count never moved at
            // all -- every "new" incident it saw was positively identified, by exact content, as
            // this one known-benign signature and nothing else. A genuine new backend defect
            // always moves the filtered count too and is never swallowed here.
        }
    }

    /// <summary>
    /// True for an unhandled-error incident that is exactly CPython's documented, still-open (as
    /// of the 3.12 runtime this suite pins), Windows-only ProactorEventLoop teardown race:
    /// <c>Lib/asyncio/proactor_events.py</c>'s <c>_call_connection_lost</c> calls
    /// <c>self._sock.shutdown(socket.SHUT_RDWR)</c> from its <c>finally:</c> block with no
    /// exception handling around it at all (confirmed by reading that source directly against
    /// the pinned runtime); if the peer has *already* reset the TCP connection by the time that
    /// scheduled callback runs -- which a real browser abruptly severing a live WebSocket
    /// (context/page teardown, or a genuine <c>page.ReloadAsync()</c>, both close code 1001) can
    /// easily race -- <c>shutdown()</c> raises <c>ConnectionResetError: [WinError 10054]</c>. That
    /// raise happens inside asyncio's own default unhandled-callback-exception handler, entirely
    /// outside rtmt.py's <c>_forward_messages</c> (whose own <c>except ConnectionResetError:
    /// pass</c> only guards its own coroutine body, never event-loop machinery scheduled
    /// separately from it). rtmt.py could theoretically intercept this -- installing a custom
    /// <c>loop.set_exception_handler(...)</c> (asyncio's documented hook for exactly this "handler
    /// called for an unhandled exception in a callback" case), or switching Windows off the
    /// default Proactor event-loop policy in favour of the Selector one -- but both would mean
    /// reaching into asyncio's event-loop plumbing to paper over an upstream-acknowledged CPython
    /// bug that has nothing to do with anything rtmt.py itself gets wrong, purely to keep a test
    /// harness quiet; we choose not to make that change to production code for that reason, and
    /// filter the known-benign symptom here instead. The equivalent Linux/macOS
    /// <c>SelectorEventLoop</c> transport-close path has no such call at all, so this exact race
    /// cannot manifest there -- see https://github.com/python/cpython/issues/83413 (still open).
    ///
    /// Because of asyncio's specific "Exception in callback" log shape (as opposed to a plain
    /// <c>logger.exception(...)</c>), this single Python-side event is actually logged as *two*
    /// separate <see cref="CapturedProcessOutput.CountUnhandledErrors()"/> incidents -- a
    /// one-line <c>ERROR:asyncio:Exception in callback ...</c> header (its very next line,
    /// <c>handle: &lt;Handle ...&gt;</c>, is not indented, so it doesn't chain onto the header as
    /// a continuation) immediately followed by a second incident starting at
    /// <c>Traceback (most recent call last):</c>. Both are matched here, individually, by exact
    /// content -- this method is invoked once per incident by
    /// <see cref="CapturedProcessOutput.CountUnhandledErrors(Func{IReadOnlyList{string}, bool})"/>.
    ///
    /// CI run 36142470529: the Linux leg of the main <c>conformance</c> job ran this suite's own
    /// unit tests (<see cref="CapturedProcessOutputTests"/>) -- not the flake itself, which only
    /// the Windows-only <c>conformance-browser</c> job can ever hit -- and a unit test that fed
    /// this exact benign-shaped stderr straight into <see cref="IsBenignProactorTeardownIncident(IReadOnlyList{string})"/>
    /// got <c>false</c> back on that Linux runner (correctly -- there is no ProactorEventLoop
    /// there), so the two incidents were never discarded and the test's own fixed expectation of
    /// "0 after filtering" failed. That the *gate* is OS-conditional is correct and deliberate
    /// (PR #54 review); the bug was testing the gated method with a fixed OS assumption instead of
    /// testing the content-shape logic and the gate as two separately-testable things. Split
    /// accordingly: <see cref="MatchesProactorTeardownIncident"/> below is the pure, OS-independent
    /// content-shape check (safe for a unit test to call unconditionally on any runner), and this
    /// method -- still the one <see cref="RunAsync"/> above actually filters with -- is just that
    /// check gated by the real OS. The <c>isWindows</c>-overload lets a unit test also exercise
    /// *this* gated method's OS branch explicitly on every runner, rather than only being able to
    /// observe one branch of it depending on which OS happens to run the test.
    /// </summary>
    internal static bool IsBenignProactorTeardownIncident(IReadOnlyList<string> incidentLines) =>
        IsBenignProactorTeardownIncident(incidentLines, OperatingSystem.IsWindows());

    /// <summary>
    /// Same as <see cref="IsBenignProactorTeardownIncident(IReadOnlyList{string})"/>, but with the
    /// "are we on Windows" fact passed in explicitly instead of read from the real OS -- an
    /// injectable seam so a unit test can prove both the true and the false branch of the gate on
    /// a single runner, rather than only ever observing whichever branch its own OS happens to
    /// take. Production code always goes through the zero-arg overload above.
    /// </summary>
    internal static bool IsBenignProactorTeardownIncident(
        IReadOnlyList<string> incidentLines, bool isWindows) =>
        // PR #54 review: this signature is Windows-only by construction (ProactorEventLoop only
        // runs there), but gate explicitly rather than relying on the content check alone -- a
        // non-Windows runner can then never match this filter, full stop, even if some future
        // incident's text happened to coincidentally contain the same substrings.
        isWindows && MatchesProactorTeardownIncident(incidentLines);

    /// <summary>
    /// The content-shape half of <see cref="IsBenignProactorTeardownIncident(IReadOnlyList{string})"/>
    /// only -- deliberately OS-independent so a unit test can assert this exact stderr shape is
    /// recognised on every runner (Linux included), leaving only the "and are we on Windows"
    /// question to the gate above. Never called directly by production code; <see cref="RunAsync"/>
    /// always goes through the gated <see cref="IsBenignProactorTeardownIncident(IReadOnlyList{string})"/>.
    /// </summary>
    internal static bool MatchesProactorTeardownIncident(IReadOnlyList<string> incidentLines)
    {
        if (incidentLines is [
                "ERROR:asyncio:Exception in callback _ProactorBasePipeTransport._call_connection_lost(None)",
            ])
        {
            return true;
        }

        return incidentLines.Count > 0 &&
            incidentLines[0] is "Traceback (most recent call last):" &&
            incidentLines[^1].StartsWith(
                "ConnectionResetError: [WinError 10054]", StringComparison.Ordinal) &&
            incidentLines.Any(line =>
                line.Contains("proactor_events.py", StringComparison.Ordinal) &&
                line.Contains("_call_connection_lost", StringComparison.Ordinal));
    }
}

[CollectionDefinition(Name)]
public sealed class BrowserConformanceCollection : ICollectionFixture<BrowserConformanceFixture>
{
    public const string Name = "ConformanceBrowser";
}
