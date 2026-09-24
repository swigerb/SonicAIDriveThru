using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// #8: extends SmokeSessionBootstrapTests.cs's bootstrap coverage (which already asserts
/// `tool_choice`/`tools.length` on the bootstrap frame and `session.audio` *presence* on the
/// browser-forwarded frame) with the remaining GA-shape fields rtmt.py's `_to_ga_session` /
/// `_build_session` translate — see app/backend/tests/test_session_bootstrap.py: `session.type`,
/// non-empty `session.instructions`, and (on the bootstrap frame specifically, before any browser
/// interaction) `session.audio.output.voice` and `session.audio.input.turn_detection`/
/// `transcription` (GA's renamed `input_audio_transcription`). A new file, not an edit to the
/// existing one, per this stream's own scope.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class SessionBootstrapGaShapeTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Bootstrap_session_update_carries_the_full_ga_shape() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        var bootstrap = await connection!.ReceivedFrames.WaitForAsync(f => f.Sequence == 0, FrameTimeout, ct);
        Assert.True(bootstrap is not null, "Bootstrap session.update never arrived.");

        var session = bootstrap!.Json.GetProperty("session");
        Assert.Equal("realtime", session.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(session.GetProperty("instructions").GetString()));

        var audio = session.GetProperty("audio");
        // Pinned to the exact default -- this collection's backend process never runs
        // extension.set_voice (see VoicePickerConformanceFixture's docs: that scenario now runs
        // on its own dedicated collection/backend process specifically so this pin stays valid).
        Assert.Equal(BackendContract.DefaultVoice, audio.GetProperty("output").GetProperty("voice").GetString());

        var input = audio.GetProperty("input");
        Assert.True(input.TryGetProperty("turn_detection", out _), "Expected session.audio.input.turn_detection on the bootstrap.");
        Assert.True(input.TryGetProperty("transcription", out _), "Expected session.audio.input.transcription (GA's renamed input_audio_transcription) on the bootstrap.");
    });
}
