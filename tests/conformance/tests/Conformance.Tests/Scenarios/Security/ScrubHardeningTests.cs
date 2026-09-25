using System.Text.Json;
using System.Text.Json.Nodes;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Security;

/// <summary>
/// swigerb/SonicAIDriveThru#29 (part 1, "scrub completeness"): the "hide max tokens" step in
/// `RTMiddleTier._scrub_session_for_client` (since replaced by the allow-listed
/// `_client_session_echo` under swigerb/SonicAIDriveThru#45 -- an allow-list can't miss a field
/// like this in the first place, since it never copies anything not explicitly named) set only
/// the *legacy* key
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

    /// <summary>
    /// swigerb/SonicAIDriveThru#45: `_GA_SESSION_TOP_LEVEL` (rtmt.py's `_to_ga_session` allow-list
    /// of legitimate GA session top-level keys) includes `prompt`, `tracing`, `include` and
    /// `truncation` -- newer GA session fields with no dedicated deny-list entry in the old
    /// `_scrub_session_for_client`, so each would have reached the browser completely unscrubbed
    /// the moment GA started echoing them back, exactly the gap `_client_session_echo`'s
    /// allow-list closes structurally rather than needing a deny-list update per new GA field.
    /// Sets all four upstream via the browser's own `session.update` (accepted because they're
    /// real GA top-level keys `_to_ga_session` doesn't drop) so the fake's `session.updated` echo
    /// genuinely carries them, then asserts the *browser*-bound copy is exactly
    /// <c>{type, event_id, session:{id, object, audio:{output:{voice}}}}</c> and nothing else --
    /// black-box proof of the exact contract documented in
    /// tests/conformance/README.md's "Session-echo allow-list" section.
    /// </summary>
    [Fact]
    public Task Session_updated_relays_only_the_allow_listed_shape_even_with_every_ga_top_level_key_set() =>
        fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        await browser.SendAsync(new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["prompt"] = new JsonObject { ["id"] = "pmpt_secret" },
                ["tracing"] = "auto",
                ["include"] = new JsonArray("item.input_audio_transcription.logprobs"),
                ["truncation"] = "auto",
            },
        }, ct);

        var updated = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > 0 && f.Type == "session.updated", FrameTimeout, ct);
        Assert.True(updated is not null, "Expected the browser's own session.update to produce a session.updated echo.");

        var session = updated!.Json.GetProperty("session");
        var topLevelKeys = new HashSet<string>(session.EnumerateObject().Select(p => p.Name), StringComparer.Ordinal);
        Assert.Equal(new HashSet<string>(["id", "object", "audio"], StringComparer.Ordinal), topLevelKeys);

        var audioKeys = new HashSet<string>(
            session.GetProperty("audio").EnumerateObject().Select(p => p.Name), StringComparer.Ordinal);
        Assert.Equal(new HashSet<string>(["output"], StringComparer.Ordinal), audioKeys);

        var outputKeys = new HashSet<string>(
            session.GetProperty("audio").GetProperty("output").EnumerateObject().Select(p => p.Name), StringComparer.Ordinal);
        Assert.Equal(new HashSet<string>(["voice"], StringComparer.Ordinal), outputKeys);
    });
}
