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
        // Presence, not a pinned value -- self.voice_choice on rtmt.py's RTMiddleTier is a single
        // process-wide attribute shared by every connection in this test run (see
        // extension.set_voice handling at rtmt.py ~1252, which updates it unconditionally even
        // when the update itself is deferred), so comparing against BackendContract.DefaultVoice
        // here would be order-dependent on whichever test last picked a voice.
        Assert.False(string.IsNullOrWhiteSpace(audio.GetProperty("output").GetProperty("voice").GetString()),
            "Expected session.audio.output.voice to be present on the bootstrap.");

        var input = audio.GetProperty("input");
        Assert.True(input.TryGetProperty("turn_detection", out _), "Expected session.audio.input.turn_detection on the bootstrap.");
        Assert.True(input.TryGetProperty("transcription", out _), "Expected session.audio.input.transcription (GA's renamed input_audio_transcription) on the bootstrap.");
    });
}
