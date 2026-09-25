using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Conformance.Fakes;

namespace Conformance.Harness;

/// <summary>
/// A plain <see cref="ClientWebSocket"/> client that sends exactly what
/// app/frontend/src/hooks/useRealtime.tsx sends: fetches a session token from
/// /api/auth/session, connects to /realtime?token=..., and issues the same
/// `session.update` (turn_detection + input_audio_transcription) the real frontend sends on
/// `startSession()`. Sends an `Origin` header on the handshake like a real browser would — by
/// default the backend's own HTTP origin, since app/backend/static serves the built frontend
/// same-origin in production (no separate dev-server origin to fake). Permessage-deflate is
/// still opt-in per connection via <paramref name="offerDeflate"/> — real browsers always offer
/// it, but the harness defaults to off so tests that don't care about compression aren't coupled
/// to it.
/// </summary>
public sealed class RealtimeBrowserClient : IAsyncDisposable
{
    /// <summary>
    /// #28 N10: bounds how long <see cref="DisposeAsync"/> waits for the reader loop to settle
    /// on its own (peer answering the graceful close it just sent, or the connection otherwise
    /// dropping) before falling back to cancelling its in-flight receive -- see
    /// <see cref="DisposeAsync"/>'s doc comment for why that fallback still exists.
    /// </summary>
    private static readonly TimeSpan DisposeGracePeriod = TimeSpan.FromSeconds(2);

    /// <remarks>
    /// Typed as the abstract <see cref="WebSocket"/> base class, not <see cref="ClientWebSocket"/>,
    /// specifically so <see cref="CreateForTesting"/> (PR #52 CI follow-up round 4) can wrap an
    /// arbitrary fake in place of a real connection -- see that factory's doc comment for why.
    /// </remarks>
    private readonly WebSocket _socket;
    private readonly CancellationTokenSource _readerCts = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// #28 N28 (corrected per PR #54 review F4): <see cref="ClientWebSocket"/>'s underlying
    /// <c>ManagedWebSocket</c> already serializes concurrent <c>SendAsync</c> calls internally (an
    /// internal send lock queues them so no caller sees an exception or corrupted frame from a
    /// second concurrent send) -- so <see cref="_sendLock"/> is not preventing an otherwise-thrown
    /// error. Its actual job is ordering whole *logical* messages: without it, two callers racing
    /// to send (e.g. <see cref="KeepAlive.RunAsync"/>'s background loop and a test's own explicit
    /// send on the same client) could each still complete their own frame intact, but the two
    /// messages could land on the wire interleaved in whichever order the runtime happened to
    /// service them, not the order the callers issued them in -- which matters to a test asserting
    /// on frame sequence. Every send this class exposes (<see cref="SendAsync(JsonNode,
    /// CancellationToken)"/> and <see cref="SendRawTextAsync"/>) goes through this lock so
    /// concurrent callers get their whole messages ordered without each needing to know why.
    /// </summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Task? _readerTask;
    private WebSocketCloseStatus? _observedCloseStatus;
    private string? _observedCloseStatusDescription;

    /// <summary>
    /// Captured once, eagerly, right after a real <see cref="ClientWebSocket"/> connects (see
    /// <see cref="ConnectAsync"/>) -- <c>ClientWebSocket.HttpResponseHeaders</c> is a
    /// <see cref="ClientWebSocket"/>-only member, unavailable once <see cref="_socket"/> is typed
    /// as the base <see cref="WebSocket"/> class, so this is read out while the concrete type is
    /// still known rather than read lazily off <see cref="_socket"/> every time
    /// <see cref="NegotiatedExtensions"/> is queried.
    /// </summary>
    private string? _negotiatedExtensions;

    private RealtimeBrowserClient(WebSocket socket)
    {
        _socket = socket;
    }

    /// <summary>Every frame the backend has sent down to this client, in arrival order.</summary>
    public FrameLog ReceivedFrames { get; } = new();

    /// <summary>
    /// The close status the backend sent, or null if the reader loop hasn't observed a Close
    /// frame yet. Captured directly from the <see cref="WebSocketReceiveResult"/> the reader loop
    /// saw, not read live off <c>ClientWebSocket.CloseStatus</c> — the latter was observed to still
    /// be null on Linux immediately after <see cref="WaitForCloseAsync"/> completed (CI run
    /// 35936172326), even though the very same receive had already produced the Close frame that
    /// unblocked it. That's a platform-dependent difference in when the underlying WebSocket
    /// implementation updates its own properties versus when it hands back the result of the
    /// receive call that observed the frame; reading from the result itself sidesteps it entirely.
    /// </summary>
    public WebSocketCloseStatus? CloseStatus => _observedCloseStatus;

    /// <summary>The close reason text the backend sent, or null if no Close frame was observed yet.</summary>
    public string? CloseStatusDescription => _observedCloseStatusDescription;

    /// <summary>
    /// #28 N10: the underlying socket's own <see cref="WebSocketState"/> — exposed so a test can
    /// tell a graceful close (<see cref="CloseAsync"/>, or plain disposal's now-graceful default)
    /// apart from an abrupt one (<see cref="AbortAsync"/>, which drives this straight to
    /// <see cref="WebSocketState.Aborted"/>) without needing the peer to answer at all.
    /// </summary>
    public WebSocketState SocketState => _socket.State;

    public static async Task<RealtimeBrowserClient> ConnectAsync(
        Uri backendBaseUri, bool offerDeflate = false, string? origin = null, CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient();
        var tokenResponse = await http.GetFromJsonAsyncSafe(new Uri(backendBaseUri, "/api/auth/session"), cancellationToken)
            .ConfigureAwait(false);
        var token = tokenResponse.GetProperty("token").GetString();

        var clientSocket = new ClientWebSocket();
        // Without this, ClientWebSocket.HttpResponseHeaders is always null regardless of what the
        // server actually negotiated, which silently made NegotiatedExtensions always null too —
        // the deflate test could never fail no matter what the backend did.
        clientSocket.Options.CollectHttpResponseDetails = true;
        if (offerDeflate)
        {
            clientSocket.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
        }

        // Real browsers always send Origin on a WebSocket handshake, even same-origin. Default to
        // the backend's own HTTP origin, matching how app/backend/static is actually served.
        clientSocket.Options.SetRequestHeader("Origin", origin ?? $"{backendBaseUri.Scheme}://{backendBaseUri.Authority}");

        // Built manually rather than via UriBuilder: UriBuilder.Scheme silently resets Port to
        // the new scheme's default port when the current port matches the old scheme's default,
        // which would corrupt the dynamically-allocated backend port used throughout the suite.
        var wsUri = new Uri($"ws://{backendBaseUri.Host}:{backendBaseUri.Port}/realtime?token={Uri.EscapeDataString(token ?? "")}");
        await clientSocket.ConnectAsync(wsUri, cancellationToken).ConfigureAwait(false);

        var client = new RealtimeBrowserClient(clientSocket)
        {
            _negotiatedExtensions = clientSocket.HttpResponseHeaders?.TryGetValue("Sec-WebSocket-Extensions", out var values) == true
                ? string.Join(", ", values)
                : null,
        };
        client._readerTask = client.PumpReceivedFramesAsync(client._readerCts.Token);
        return client;
    }

    /// <summary>
    /// PR #52 CI follow-up round 4 (swigerb/SonicAIDriveThru#28): test-only seam replacing round
    /// 3's thread-pool-starvation technique (see <c>Conformance.Tests</c>' fault-handling tests
    /// for the full history of why). Wraps an arbitrary <see cref="WebSocket"/> -- typically a
    /// fake whose <see cref="WebSocket.CloseOutputAsync"/> is rigged to throw a specific fault --
    /// directly, bypassing the real HTTP/WS handshake <see cref="ConnectAsync"/> performs and
    /// never starting the background reader loop (<c>_readerTask</c> stays <c>null</c>, which
    /// <see cref="DisposeAsync"/>'s own cleanup already tolerates). This lets a test pin exactly
    /// what <see cref="CloseAsync"/>/<see cref="DisposeAsync"/> do when the underlying socket
    /// faults in a specific way, deterministically and instantly -- no real socket, no real
    /// thread, no timing, so no platform-dependent behaviour to reproduce or accidentally
    /// destabilize. <c>internal</c>, exposed to <c>Conformance.Tests</c> via
    /// <c>InternalsVisibleTo</c> (see <c>AssemblyInfo.cs</c>).
    /// </summary>
    internal static RealtimeBrowserClient CreateForTesting(WebSocket socket) => new(socket);

    /// <summary>
    /// #28 flake hunt: <see cref="CreateForTesting"/> deliberately never starts the background
    /// reader loop (see that factory's doc comment), so a test that specifically wants to exercise
    /// <see cref="AbortAsync"/>'s await of its reader task -- not just its early-return when no
    /// reader is running at all -- needs a way to start one against the fake socket first. Starts
    /// the same loop <see cref="ConnectAsync"/> would have, with no real handshake and no real
    /// timing: whatever the fake socket's <see cref="WebSocket.ReceiveAsync"/> does (including
    /// throwing synchronously) happens on this loop's very first iteration, deterministically.
    /// </summary>
    internal void StartReaderLoopForTesting() => _readerTask = PumpReceivedFramesAsync(_readerCts.Token);

    /// <summary>The exact `session.update` useRealtime.tsx's startSession() sends.</summary>
    public Task SendStartSessionAsync(bool enableInputAudioTranscription = true, CancellationToken cancellationToken = default)
    {
        var session = new JsonObject
        {
            ["turn_detection"] = new JsonObject
            {
                ["type"] = "server_vad",
                ["threshold"] = 0.7,
                ["prefix_padding_ms"] = 300,
                ["silence_duration_ms"] = 500,
            },
        };
        if (enableInputAudioTranscription)
        {
            session["input_audio_transcription"] = new JsonObject { ["model"] = "whisper-1" };
        }

        var command = new JsonObject { ["type"] = "session.update", ["session"] = session };
        return SendAsync(command, cancellationToken);
    }

    /// <summary>The exact `input_audio_buffer.append` useRealtime.tsx's addUserAudio() sends.</summary>
    public Task SendInputAudioAppendAsync(string base64Audio, CancellationToken cancellationToken = default) =>
        SendAsync(new JsonObject { ["type"] = "input_audio_buffer.append", ["audio"] = base64Audio }, cancellationToken);

    /// <summary>The exact `input_audio_buffer.clear` useRealtime.tsx sends on interrupt/reset.</summary>
    public Task SendInputAudioClearAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new JsonObject { ["type"] = "input_audio_buffer.clear" }, cancellationToken);

    /// <summary>The exact `response.cancel` useRealtime.tsx's cancelResponse() sends (barge-in).</summary>
    public Task SendResponseCancelAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new JsonObject { ["type"] = "response.cancel" }, cancellationToken);

    /// <summary>The `extension.set_verbose_logging` useRealtime.tsx sends.</summary>
    public Task SendExtensionSetVerboseLoggingAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendAsync(new JsonObject { ["type"] = "extension.set_verbose_logging", ["enabled"] = enabled }, cancellationToken);

    /// <summary>The `extension.set_log_to_file` useRealtime.tsx sends.</summary>
    public Task SendExtensionSetLogToFileAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendAsync(new JsonObject { ["type"] = "extension.set_log_to_file", ["enabled"] = enabled }, cancellationToken);

    /// <summary>The `extension.set_voice` useRealtime.tsx sends.</summary>
    public Task SendExtensionSetVoiceAsync(string voice, CancellationToken cancellationToken = default) =>
        SendAsync(new JsonObject { ["type"] = "extension.set_voice", ["voice"] = voice }, cancellationToken);

    /// <summary>The `extension.end_session` useRealtime.tsx sends when the caller hangs up.</summary>
    public Task SendExtensionEndSessionAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new JsonObject { ["type"] = "extension.end_session" }, cancellationToken);

    /// <summary>The `extension.resume` useRealtime.tsx sends when reconnecting after a drop.</summary>
    public Task SendExtensionResumeAsync(string resumeId, CancellationToken cancellationToken = default) =>
        SendAsync(new JsonObject { ["type"] = "extension.resume", ["resume_id"] = resumeId }, cancellationToken);

    /// <summary>
    /// `response.create` with no body — not sent by useRealtime.tsx itself (the backend drives
    /// response creation server-side after VAD), but useful for tests that need to nudge the
    /// model directly without simulating audio/VAD timing.
    /// </summary>
    public Task SendResponseCreateAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new JsonObject { ["type"] = "response.create" }, cancellationToken);

    /// <summary>
    /// Sends one JSON frame. Serialized via <see cref="_sendLock"/> (#28 N28) so concurrent callers
    /// -- e.g. a <see cref="KeepAlive"/> loop and the test's own explicit sends on the same client
    /// -- get their whole messages ordered on the wire instead of interleaved (see
    /// <see cref="_sendLock"/>'s doc comment for why ordering, not corruption, is what this guards).
    /// </summary>
    public async Task SendAsync(JsonNode command, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToElement(command);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WebSocketJson.SendAsync(_socket, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Sends the given bytes exactly as-is, over a Text frame, bypassing all JSON
    /// (de)serialisation entirely. Used only by black-box bypass/malformed-frame tests
    /// (swigerb/SonicAIDriveThru#31 review round 2: M1's duplicate-`type`-key and nested-`type`-
    /// substring reproductions, S2's malformed-frame-doesn't-crash-the-socket reproductions) that
    /// need to construct frames no <see cref="JsonNode"/> tree could represent (e.g. a duplicate
    /// top-level `type` key, or a non-JSON payload) -- something the real frontend never sends,
    /// but a compromised/malicious same-origin script could. Routed through the same
    /// <see cref="_sendLock"/> as <see cref="SendAsync(JsonNode, CancellationToken)"/> (#54 review
    /// F4) so a raw send races no differently than a JSON one.
    /// </summary>
    public async Task SendRawTextAsync(string rawText, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(rawText);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>The negotiated `Sec-WebSocket-Extensions` response header, or null if none was granted.</summary>
    public string? NegotiatedExtensions => _negotiatedExtensions;

    /// <summary>
    /// Initiates a graceful WebSocket close by sending a Close frame. Uses
    /// <see cref="WebSocket.CloseOutputAsync"/> rather than <see cref="WebSocket.CloseAsync"/>
    /// deliberately: <c>CloseAsync</c> performs its own internal receive loop to await the
    /// server's answering Close frame, which races with the background reader loop's own
    /// in-flight <c>ReceiveAsync</c> and throws "there is already one outstanding read call".
    /// The server's answering Close frame is instead observed by that same reader loop, which
    /// signals <see cref="WaitForCloseAsync"/> once it arrives.
    ///
    /// #28 N10: for a scenario that specifically wants the backend to observe an abrupt,
    /// no-close-frame disconnect instead (a browser crash or network drop), use
    /// <see cref="AbortAsync"/>; plain disposal (<see cref="DisposeAsync"/>) now defaults to the
    /// same graceful behaviour as this method.
    ///
    /// PR #52 CI follow-up (swigerb/SonicAIDriveThru#28 N10 aftermath, CI runs 36085091969):
    /// the state check just above is inherently racy against the background reader loop, which
    /// can observe the peer aborting/closing the connection concurrently with this call, on a
    /// loaded runner -- the two are never synchronized with each other. A caller of this method
    /// only asked for a *best-effort* close, the same contract <see cref="DisposeAsync"/> already
    /// documents for its own equivalent attempt; it did not ask for proof the handshake completed
    /// (that is what <see cref="WaitForCloseAsync"/> plus asserting on <see cref="CloseStatus"/>
    /// is for). So a <see cref="WebSocketException"/> here -- the socket having raced its way out
    /// of <c>Open</c>/<c>CloseReceived</c> between the check and the send, or the peer's TCP
    /// connection simply vanishing before this send lands -- is treated the same way: the socket
    /// is (or is about to be) closed either way, which is exactly what this method was trying to
    /// bring about.
    ///
    /// PR #52 CI follow-up round 2 (full-suite run pushed after commit 3c03ab4): under full-suite
    /// load, the <c>Explicit_close_does_not_throw_when_the_peer_aborts_first</c> self-test in
    /// <c>Conformance.Tests</c> showed <c>WebSocketException</c> alone is not enough. <c>ManagedWebSocket</c> surfaces a
    /// peer TCP reset encountered *during* <c>CloseOutputAsync</c>'s send as an
    /// <see cref="OperationCanceledException"/> wrapping an <see cref="IOException"/>/
    /// <see cref="System.Net.Sockets.SocketException"/> -- not a <c>WebSocketException</c> --
    /// whenever the fault happens while the receive side has already completed (i.e. exactly the
    /// same "reader loop wins the race" condition documented above, just surfacing as a different
    /// .NET exception type than the check-then-await TOCTOU on <c>_socket.State</c> alone would
    /// suggest). Since this is the caller's own <paramref name="cancellationToken"/> that was
    /// passed to <c>CloseOutputAsync</c>, the <c>when</c> guard below only swallows this when that
    /// token was *not* the thing requesting cancellation -- a genuine caller-requested cancel
    /// still propagates as before.
    /// </summary>
    public async Task CloseAsync(
        WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure,
        string? statusDescription = "test done",
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseOutputAsync(status, statusDescription, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
            // Best-effort close -- see the doc comment above.
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Best-effort close -- see the doc comment above (round 2): the peer reset the
            // connection out from under the send, surfaced as OperationCanceledException rather
            // than WebSocketException. Not a caller-requested cancel (the guard above already
            // ruled that out), so treat it the same as the WebSocketException case.
        }
    }


    /// <summary>
    /// #28 N10: simulates a client disappearing with no WebSocket-level close handshake at all —
    /// a browser crash or a network drop — rather than <see cref="CloseAsync"/>'s graceful Close
    /// frame. <see cref="WebSocket.Abort"/> transitions the socket straight to
    /// <see cref="WebSocketState.Aborted"/> and cancels its in-flight receive, which is exactly
    /// the abrupt-disconnect signature the backend would observe from a real dropped connection.
    /// Call this explicitly when a scenario's own subject matter *is* that abrupt-drop behaviour;
    /// every other scenario should prefer <see cref="CloseAsync"/> or plain disposal, both of
    /// which now default to a graceful close (see <see cref="DisposeAsync"/>'s doc comment for why
    /// that distinction used to not exist).
    /// </summary>
    public async ValueTask AbortAsync()
    {
        _socket.Abort();

        if (_readerTask is not null)
        {
            try
            {
                await _readerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: Abort() above cancels the reader loop's in-flight ReceiveAsync.
            }
            catch (WebSocketException)
            {
                // Expected: the reader loop's in-flight ReceiveAsync observes the now-aborted
                // socket as a fault rather than a cancellation, depending on exactly where it was
                // in its own receive when Abort() ran.
            }
            catch (ObjectDisposedException)
            {
                // #28 flake hunt (post-#54-merge, run 16/25 on ResumeRehydrationAndNudgeTests):
                // belt and braces alongside WebSocketJson.ReceiveJsonOrCloseAsync's own catch for
                // this same shape -- see that method's doc comment for the full three-shape
                // explanation. This call is the one place that *knows* it just aborted the socket
                // on purpose, so it tolerates its own reader task reporting exactly that outcome,
                // regardless of which layer inside the reader loop the exception happened to
                // surface at.
            }
        }
    }

    /// <summary>
    /// Awaits the reader loop observing the socket close (server Close frame or the connection
    /// dropping), bounded by <paramref name="timeout"/> instead of a fixed sleep, so tests can
    /// assert on <see cref="CloseStatus"/> deterministically: by the time this returns, the reader
    /// loop has already captured <see cref="CloseStatus"/>/<see cref="CloseStatusDescription"/>
    /// from the Close frame's <see cref="WebSocketReceiveResult"/> (see <see cref="PumpReceivedFramesAsync"/>),
    /// so there is nothing left to race against afterward.
    /// </summary>
    public async Task WaitForCloseAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await _closed.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Socket did not close within {timeout}.");
        }
    }

    private async Task PumpReceivedFramesAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Deliberately not "while (_socket.State == WebSocketState.Open)": that was the actual
            // root cause of the close race (CI run 35936172326, and reproduced locally against the
            // real Python backend with a 50-connection stress loop — 1/50 iterations). CloseAsync
            // sends the local close frame via CloseOutputAsync, which flips the socket's own State
            // from Open to CloseSent as soon as it completes — on the caller's task, concurrently
            // with this loop. If that flip lands between this loop's iterations (rather than while a
            // receive is already in flight), the while-condition below would go false and the loop
            // would return *without ever calling ReceiveAsync again* — meaning it would never see the
            // peer's answering Close frame at all, leaving CloseStatus null forever even though the
            // peer had already answered. CloseSent is a perfectly valid state to keep receiving from
            // (that's the entire point of the state: "our close is out, the peer's answering close
            // may still arrive"), so the loop must keep receiving through it.
            while (_socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                var received = await WebSocketJson.ReceiveJsonOrCloseAsync(_socket, cancellationToken).ConfigureAwait(false);
                if (received.IsClose)
                {
                    // Capture from the result itself, before anything else can observe or race
                    // with the socket's own (platform-dependent-timed) CloseStatus property.
                    _observedCloseStatus = received.CloseStatus;
                    _observedCloseStatusDescription = received.CloseStatusDescription;
                    return;
                }
                if (received.Json is null)
                {
                    // Either an empty read or a WebSocketException that ReceiveJsonOrCloseAsync
                    // swallowed internally (its documented "safe null" contract, shared by other
                    // callers that don't care about close status) — so the exception never reaches
                    // this method's own catch block below. Observed in a 50-connection stress run
                    // against the real Python backend (1/50 iterations): the peer had actually
                    // already answered the close (aiohttp's autoclose) before its TCP connection
                    // tore down, so the socket had recorded a close status despite the receive
                    // faulting instead of returning a clean Close-type result. Best-effort recover
                    // it here rather than leaving CloseStatus permanently null.
                    TryCaptureCloseFromSettledSocket();
                    return;
                }
                ReceivedFrames.Add(received.Json.Value);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on disposal.
        }
        catch (WebSocketException)
        {
            // Belt-and-braces: covers a WebSocketException thrown by something other than the
            // ReceiveAsync call ReceiveJsonOrCloseAsync already guards (e.g. cancellation racing
            // with a receive in a way that surfaces here instead of as OperationCanceledException).
            TryCaptureCloseFromSettledSocket();
        }
        finally
        {
            _closed.TrySetResult();
        }
    }

    /// <summary>
    /// Best-effort fallback used when the reader loop didn't get a clean Close-type
    /// <see cref="WebSocketReceiveResult"/> to capture status from directly (see
    /// <see cref="PumpReceivedFramesAsync"/>): once the socket has actually settled to
    /// <see cref="WebSocketState.Closed"/> or <see cref="WebSocketState.CloseReceived"/>, the
    /// runtime may still have recorded the peer's close status even though the receive that
    /// observed it faulted instead of returning cleanly. Reading it here — inside the reader's own
    /// task, before <c>_closed</c> is signalled — means any caller of <see cref="WaitForCloseAsync"/>
    /// only ever sees the settled value, never an in-between one.
    /// </summary>
    private void TryCaptureCloseFromSettledSocket()
    {
        if (_socket.State is WebSocketState.Closed or WebSocketState.CloseReceived)
        {
            _observedCloseStatus = _socket.CloseStatus;
            _observedCloseStatusDescription = _socket.CloseStatusDescription;
        }
    }

    /// <summary>
    /// #28 N10: this used to cancel the reader loop's in-flight <c>ReceiveAsync</c> *before*
    /// attempting any close, which per <see cref="ClientWebSocket"/>'s own documented semantics
    /// transitions the socket straight to <see cref="WebSocketState.Aborted"/> — so every
    /// disposed client produced the same abrupt-drop (1006-equivalent) signature the backend
    /// would see from a user's laptop losing network, whether or not a given scenario's own
    /// subject matter had anything to do with that. The default is now deliberate: attempt the
    /// same graceful Close frame <see cref="CloseAsync"/> would (best-effort — a scenario that
    /// already called <see cref="CloseAsync"/> itself finds the socket no longer <c>Open</c> here
    /// and this is a no-op), then give the reader loop up to <see cref="DisposeGracePeriod"/> to
    /// settle on its own — cancelling its in-flight receive still aborts the socket the same as
    /// before, so that only happens as a last-resort fallback if the peer hasn't answered within
    /// the grace period, not on every disposal. A scenario whose own subject matter genuinely is
    /// the abrupt-drop behaviour should call <see cref="AbortAsync"/> instead of relying on plain
    /// disposal to produce it as a side effect.
    ///
    /// PR #52 CI follow-up round 2: same peer-reset-surfaces-as-OperationCanceledException hole
    /// as <see cref="CloseAsync"/> -- see that method's doc comment for the full explanation. The
    /// <c>CloseOutputAsync</c> call here always passes <see cref="CancellationToken.None"/>,
    /// which can never itself request cancellation, so catching <see cref="OperationCanceledException"/>
    /// unconditionally is safe: it can only mean the underlying send faulted, never a caller cancel.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
            // Best-effort close.
        }
        catch (OperationCanceledException)
        {
            // Best-effort close (round 2) -- see the doc comment above: CancellationToken.None
            // above can never be the source of this, so it can only be the peer resetting the
            // connection out from under the send.
        }

        if (_readerTask is not null)
        {
            var settledOnItsOwn = await Task.WhenAny(_readerTask, Task.Delay(DisposeGracePeriod)).ConfigureAwait(false)
                == _readerTask;
            if (!settledOnItsOwn)
            {
                await _readerCts.CancelAsync().ConfigureAwait(false);
            }

            // Drain the reader loop before touching the socket ourselves: WebSocket only allows
            // one outstanding ReceiveAsync at a time, and (in the fallback case above) the reader
            // loop's cancellation needs a moment to actually unblock its in-flight receive. Racing
            // a close call against it throws "there is already one outstanding read call for this
            // WebSocket instance."
            try
            {
                await _readerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // #28 N15: expected -- _readerCts.CancelAsync() above is what unblocks the
                // reader loop's in-flight ReceiveAsync in the first place, so its task settling
                // with this exception is disposal doing exactly what it asked for, not a fault.
            }
        }

        _socket.Dispose();
        _readerCts.Dispose();
        _sendLock.Dispose();
    }
}

internal static class HttpClientJsonExtensions
{
    public static async Task<JsonElement> GetFromJsonAsyncSafe(this HttpClient http, Uri uri, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.Clone();
    }
}
