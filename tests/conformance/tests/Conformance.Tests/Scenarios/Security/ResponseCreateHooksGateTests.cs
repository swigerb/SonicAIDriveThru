using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Security;

/// <summary>
/// swigerb/SonicAIDriveThru#31, PR #49 review round 2 follow-up ("G1"): every scenario in
/// <see cref="ClientToServerAllowListTests"/> and <see cref="AllowListBypassHardeningTests"/>
/// (and every Python unit test) runs with <c>CONFORMANCE_TEST_HOOKS=1</c> set for the whole
/// process, so none of them can prove `rtmt.py`'s S1 gate --
/// <c>msg_type in _CLIENT_TEST_ONLY_TYPES and conformance_hooks.hooks_enabled_now()</c> in
/// <c>_filter_client_to_server</c> -- actually does anything: mutating it to unconditionally
/// allow <c>response.create</c> (deleting the <c>hooks_enabled_now()</c> half of the <c>or</c>)
/// left every existing Python AND C# test green.
///
/// This collection launches its OWN backend process under <see cref="BackendProfiles.HooksOff"/>
/// -- CONFORMANCE_TEST_HOOKS left completely unset, the exact shape of a real deployment -- so a
/// browser-sent <c>response.create</c> can be proven to never reach the fake upstream at all,
/// black-box, against the real backend.
///
/// The one wrinkle: the backend's OWN greeting logic also sends a <c>response.create</c>
/// upstream (`rtmt.py`'s `send_greeting_once`), entirely independent of the client allow-list --
/// it writes straight to the upstream socket, never passing through
/// `_process_message_to_server`/`_filter_client_to_server`. So "no response.create ever reaches
/// upstream" would be the WRONG assertion (it would also be false against a correctly-fixed
/// backend). Instead, this test captures the greeting's own response.create as an explicit
/// baseline sequence number, then asserts no FURTHER response.create frame ever arrives after the
/// browser sends its own -- isolating "the browser's frame specifically was dropped" from "the
/// server never legitimately sends this type at all".
///
/// Mutation: reverting the S1 gate to `allowed = msg_type in _CLIENT_ALLOWED_TYPES or (msg_type
/// in _CLIENT_TEST_ONLY_TYPES)` (dropping the `hooks_enabled_now()` check) makes this test's core
/// assertion fail, while every other test in the suite (which all run with hooks on) stays green
/// -- this is the ONLY place that mutation is caught.
/// </summary>
[Collection(HooksOffConformanceCollection.Name)]
public sealed class ResponseCreateHooksGateTests(HooksOffConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Browser_sent_response_create_never_reaches_upstream_when_hooks_are_disabled() =>
        fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, "No upstream connection was accepted for the browser socket.");

        // The browser's own session.update handshake (useRealtime.tsx's startSession()) is what
        // triggers the backend's auto-greeting once session_configured -- see rtmt.py's
        // send_greeting_once/"client-session.update" trigger -- exactly as it does under every
        // other (hooks-on) profile; the hooks gate has no bearing on this trigger path.
        await browser.SendStartSessionAsync(cancellationToken: ct);
        var sessionUpdate = await connection!.ReceivedFrames.WaitForAsync(
            f => f.Sequence > 0 && f.Type == "session.update", FrameTimeout, ct);
        Assert.True(sessionUpdate is not null, "The browser's own session.update must still reach the fake upstream.");

        // The server's OWN greeting response.create -- legitimate, unrelated to the client
        // allow-list gate under test. Captured as the baseline sequence number below.
        var serverGreeting = await connection.ReceivedFrames.WaitForAsync(
            f => f.Type == "response.create", FrameTimeout, ct);
        Assert.True(serverGreeting is not null,
            "Expected the backend's own greeting response.create to reach the fake upstream " +
            "(hooks-off must not disable the server's OWN greeting trigger -- only the client " +
            "allow-list entry is gated).");

        // Let the greeting's round trip fully finish before the browser sends its own
        // response.create -- same reasoning as ClientToServerAllowListTests' identical wait:
        // avoids racing the still-streaming greeting's echo-suppression cooldown.
        var greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");

        // Now the browser sends its OWN response.create -- exactly what a real frontend never
        // does (S1), and what this hooks-off backend must drop.
        await browser.SendResponseCreateAsync(ct);

        // Liveness proof: the socket must still accept and forward a subsequent legitimate frame,
        // proving the response.create above was dropped rather than having crashed the
        // connection handler or closed the socket.
        await browser.SendInputAudioClearAsync(ct);
        var clear = await connection.ReceivedFrames.WaitForAsync(
            f => f.Type == "input_audio_buffer.clear" && f.Sequence > serverGreeting!.Sequence, FrameTimeout, ct);
        Assert.True(clear is not null, "Expected input_audio_buffer.clear to still reach the fake upstream.");

        // The core assertion: no response.create frame arrives AFTER the server's own greeting
        // one -- the browser's attempt must never have reached upstream at all.
        foreach (var frame in connection.ReceivedFrames.Snapshot())
        {
            if (frame.Sequence > serverGreeting!.Sequence)
            {
                Assert.NotEqual("response.create", frame.Type);
            }
        }
    }, allowedNewBackendErrors: 0);

    [Fact]
    public Task Legitimate_frontend_traffic_still_flows_with_hooks_disabled() => fixture.RunAsync(async () =>
    {
        // Sanity companion to the test above: hooks-off must not break ordinary traffic. Mirrors
        // ClientToServerAllowListTests.Legitimate_frontend_traffic_still_flows (minus
        // response.create, already exercised via the greeting/round-trip in the sibling test).
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, "No upstream connection was accepted for the browser socket.");

        await browser.SendStartSessionAsync(cancellationToken: ct);
        var sessionUpdate = await connection!.ReceivedFrames.WaitForAsync(
            f => f.Sequence > 0 && f.Type == "session.update", FrameTimeout, ct);
        Assert.True(sessionUpdate is not null, "The browser's own session.update must still reach the fake upstream.");

        // Wait for the greeting's own round trip to fully finish before touching mic audio --
        // echo.on_audio_done() arms a cooldown window on top of ai_speaking=False (pre-existing,
        // unrelated to #31), so mic audio sent inside that window is silently dropped regardless
        // of the allow-list. See ClientToServerAllowListTests.Legitimate_frontend_traffic_still_flows
        // for identical reasoning/precedent.
        var greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");

        // response.cancel: the greeting response already completed, so the fake upstream
        // legitimately bounces this back with a "no active response" error (expected, hence
        // allowedNewBackendErrors: 1 below) -- sent purely to reset the post-greeting echo
        // cooldown via on_barge_in() so the mic audio sent next isn't suppressed by it.
        await browser.SendResponseCancelAsync(ct);
        var cancel = await connection.ReceivedFrames.WaitForAsync(f => f.Type == "response.cancel", FrameTimeout, ct);
        Assert.True(cancel is not null, "response.cancel must still reach the fake upstream.");

        const string micAudioMarker = "ZmFrZS1taWMtYXVkaW8tbWFya2VyLWJhc2U2NA==";
        await browser.SendInputAudioAppendAsync(micAudioMarker, ct);
        var audioAppend = await connection.ReceivedFrames.WaitForAsync(
            f => f.Type == "input_audio_buffer.append" &&
                 f.Json.TryGetProperty("audio", out var audio) && audio.GetString() == micAudioMarker,
            FrameTimeout, ct);
        Assert.True(audioAppend is not null, "Mic audio (input_audio_buffer.append) must still reach the fake upstream.");

        await browser.SendExtensionSetVoiceAsync("marin", ct);
        var voiceUpdate = await connection.ReceivedFrames.WaitForAsync(
            f => f.Type == "session.update" &&
                 f.Json.GetProperty("session").TryGetProperty("audio", out var audio2) &&
                 audio2.TryGetProperty("output", out var output) &&
                 output.TryGetProperty("voice", out var voice) &&
                 voice.GetString() == "marin",
            FrameTimeout, ct);
        Assert.True(voiceUpdate is not null, "extension.set_voice must still result in a voice session.update reaching the fake upstream.");
    }, allowedNewBackendErrors: 1);
}
