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
    /// delegation) but additionally honours a missing-browser-channel skip.
    /// </summary>
    public async Task RunAsync(Func<Task> body)
    {
        if (_browserSkipReason is not null)
        {
            Assert.Skip(_browserSkipReason);
            return;
        }
        await _inner.RunAsync(body).ConfigureAwait(false);
    }
}

[CollectionDefinition(Name)]
public sealed class BrowserConformanceCollection : ICollectionFixture<BrowserConformanceFixture>
{
    public const string Name = "ConformanceBrowser";
}
