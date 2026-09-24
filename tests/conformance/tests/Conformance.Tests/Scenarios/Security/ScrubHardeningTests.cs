using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Security;

/// <summary>
/// swigerb/SonicAIDriveThru#29 (part 1, "scrub completeness"): the "hide max tokens" step in
/// `RTMiddleTier._scrub_session_for_client` set only the *legacy* key
/// `max_response_output_tokens`, but `_to_ga_session` renames that to `max_output_tokens` on the
/// way upstream, so GA's `session.updated` echo carried the real cap back under the *new* key
/// untouched. The same echo also carries `model` (the internal Azure deployment name -- always
/// present, since <see cref="FakeRealtimeUpstreamServer"/> stamps it onto the effective session
/// unconditionally, matching real GA) and `audio.input.transcription.model` (the transcription
/// deployment name, present because the conformance backend configures a transcription model by
/// default). Fixed by popping all three (plus `reasoning` / `parallel_tool_calls`, covered at the
/// Python unit level in `test_rtmt.py::ProcessMessageToClientTests` -- those two only appear on a
/// deployment name recognised as reasoning-capable, which isn't reliably black-box-triggerable
/// without a dedicated env profile, so they aren't asserted here).
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class ScrubHardeningTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Session_updated_never_carries_ga_only_secret_fields() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        await browser.SendStartSessionAsync(cancellationToken: ct);

        var updated = await browser.ReceivedFrames.WaitForAsync(f => f.Type == "session.updated", FrameTimeout, ct);
        Assert.True(updated is not null, "Expected the browser to receive a session.updated frame within the timeout.");

        var session = updated!.Json.GetProperty("session");

        Assert.False(session.TryGetProperty("max_output_tokens", out _),
            "The browser must never receive the GA max-token cap (max_output_tokens) via session.updated.");
        Assert.False(session.TryGetProperty("model", out _),
            "The browser must never receive the internal Azure deployment name (model) via session.updated.");

        var hasLeakedTranscriptionModel =
            session.TryGetProperty("audio", out var audio) &&
            audio.TryGetProperty("input", out var input) &&
            input.TryGetProperty("transcription", out var transcription) &&
            transcription.TryGetProperty("model", out _);
        Assert.False(hasLeakedTranscriptionModel,
            "The browser must never receive the transcription deployment name (audio.input.transcription.model) via session.updated.");
    });
}
