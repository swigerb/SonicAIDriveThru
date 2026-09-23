using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// The #7 acceptance smoke scenario: connect -> the bootstrap `session.update` (4 tools,
/// `tool_choice=auto`) is the first frame the fake sees on this connection's upstream socket ->
/// a browser `session.update` is forwarded -> the greeting `response.create` arrives only after
/// that. Uses a per-test baseline index into the shared <see cref="FrameLog"/> (rather than
/// absolute index 0) so it stays correct regardless of how many other tests in the suite have
/// already opened — and closed — their own upstream connections against the same fake.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class SmokeSessionBootstrapTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Bootstrap_session_update_with_four_tools_is_the_first_upstream_frame() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var baseline = fixture.Realtime.ReceivedFrames.Count;

        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);

        var bootstrap = await fixture.Realtime.ReceivedFrames.WaitForAsync(f => f.Sequence == baseline, FrameTimeout, ct);
        Assert.True(bootstrap is not null, $"Expected an upstream frame at index {baseline} (the bootstrap session.update) within {FrameTimeout}.");
        Assert.Equal("session.update", bootstrap!.Type);

        var session = bootstrap.Json.GetProperty("session");
        Assert.Equal("auto", session.GetProperty("tool_choice").GetString());
        Assert.Equal(4, session.GetProperty("tools").GetArrayLength());
    });

    [Fact]
    public Task Greeting_response_create_arrives_only_after_the_browser_session_update() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var baseline = fixture.Realtime.ReceivedFrames.Count;

        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);

        var bootstrap = await fixture.Realtime.ReceivedFrames.WaitForAsync(f => f.Sequence == baseline, FrameTimeout, ct);
        Assert.True(bootstrap is not null, "Bootstrap session.update never arrived.");

        await browser.SendStartSessionAsync(cancellationToken: ct);

        var browserUpdate = await fixture.Realtime.ReceivedFrames.WaitForAsync(
            f => f.Sequence > baseline && f.Type == "session.update", FrameTimeout, ct);
        Assert.True(browserUpdate is not null, "The browser's session.update was never forwarded upstream.");

        // GA nests turn_detection/input_audio_transcription under session.audio.input — confirms
        // _to_ga_session translated the browser's payload rather than passing something else.
        Assert.True(browserUpdate!.Json.GetProperty("session").TryGetProperty("audio", out _),
            "Expected the browser session.update to be GA-translated (session.audio.input.*).");

        var greeting = await fixture.Realtime.ReceivedFrames.WaitForAsync(
            f => f.Sequence > browserUpdate.Sequence && f.Type == "response.create", FrameTimeout, ct);
        Assert.True(greeting is not null, "The greeting response.create never arrived after the browser's session.update.");
        Assert.True(greeting!.Sequence > browserUpdate.Sequence);
    });
}
