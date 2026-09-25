using System.Net.WebSockets;
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
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _readerCts = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _readerTask;
    private WebSocketCloseStatus? _observedCloseStatus;
    private string? _observedCloseStatusDescription;

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

    public static async Task<RealtimeBrowserClient> ConnectAsync(
        Uri backendBaseUri, bool offerDeflate = false, string? origin = null, CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient();
        var tokenResponse = await http.GetFromJsonAsyncSafe(new Uri(backendBaseUri, "/api/auth/session"), cancellationToken)
            .ConfigureAwait(false);
        var token = tokenResponse.GetProperty("token").GetString();

        var client = new RealtimeBrowserClient();
        // Without this, ClientWebSocket.HttpResponseHeaders is always null regardless of what the
        // server actually negotiated, which silently made NegotiatedExtensions always null too —
        // the deflate test could never fail no matter what the backend did.
        client._socket.Options.CollectHttpResponseDetails = true;
        if (offerDeflate)
        {
            client._socket.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
        }

        // Real browsers always send Origin on a WebSocket handshake, even same-origin. Default to
        // the backend's own HTTP origin, matching how app/backend/static is actually served.
        client._socket.Options.SetRequestHeader("Origin", origin ?? $"{backendBaseUri.Scheme}://{backendBaseUri.Authority}");

        // Built manually rather than via UriBuilder: UriBuilder.Scheme silently resets Port to
        // the new scheme's default port when the current port matches the old scheme's default,
        // which would corrupt the dynamically-allocated backend port used throughout the suite.
        var wsUri = new Uri($"ws://{backendBaseUri.Host}:{backendBaseUri.Port}/realtime?token={Uri.EscapeDataString(token ?? "")}");
        await client._socket.ConnectAsync(wsUri, cancellationToken).ConfigureAwait(false);
        client._readerTask = client.PumpReceivedFramesAsync(client._readerCts.Token);
        return client;
    }

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

    public Task SendAsync(JsonNode command, CancellationToken cancellationToken = default) =>
        WebSocketJson.SendAsync(_socket, JsonSerializer.SerializeToElement(command), cancellationToken);

    /// <summary>The negotiated `Sec-WebSocket-Extensions` response header, or null if none was granted.</summary>
    public string? NegotiatedExtensions =>
        _socket.HttpResponseHeaders?.TryGetValue("Sec-WebSocket-Extensions", out var values) == true
            ? string.Join(", ", values)
            : null;

    /// <summary>
    /// Initiates a graceful WebSocket close by sending a Close frame. Uses
    /// <see cref="WebSocket.CloseOutputAsync"/> rather than <see cref="WebSocket.CloseAsync"/>
    /// deliberately: <c>CloseAsync</c> performs its own internal receive loop to await the
    /// server's answering Close frame, which races with the background reader loop's own
    /// in-flight <c>ReceiveAsync</c> and throws "there is already one outstanding read call".
    /// The server's answering Close frame is instead observed by that same reader loop, which
    /// signals <see cref="WaitForCloseAsync"/> once it arrives.
    /// </summary>
    public async Task CloseAsync(
        WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure,
        string? statusDescription = "test done",
        CancellationToken cancellationToken = default)
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await _socket.CloseOutputAsync(status, statusDescription, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Severs the connection abruptly -- no Close frame is ever sent, unlike <see cref="CloseAsync"/>.
    /// This is the real Wi-Fi-blip / dropped-connection path (PR #52 review "F1"): a genuinely
    /// different code path on both sides than a graceful close. <see cref="ClientWebSocket.Abort"/>
    /// tears the socket down locally (the .NET equivalent of the underlying TCP connection just
    /// vanishing from under a browser tab); the reader loop's in-flight receive faults with a
    /// <see cref="WebSocketException"/> rather than observing a Close-type
    /// <see cref="WebSocketReceiveResult"/>, so <see cref="CloseStatus"/> stays null afterward --
    /// correctly reflecting that no close code was ever negotiated. Callers should still await
    /// <see cref="WaitForCloseAsync"/> afterward to know the reader loop has settled before
    /// reconnecting, exactly as after <see cref="CloseAsync"/>.
    /// </summary>
    public void Abort() => _socket.Abort();

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

    public async ValueTask DisposeAsync()
    {
        await _readerCts.CancelAsync().ConfigureAwait(false);

        // Drain the reader loop before touching the socket ourselves: WebSocket only allows one
        // outstanding ReceiveAsync at a time, and the reader loop's cancellation needs a moment to
        // actually unblock its in-flight receive. Racing a close call against it throws
        // "there is already one outstanding read call for this WebSocket instance."
        if (_readerTask is not null)
        {
            try
            {
                await _readerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

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

        _socket.Dispose();
        _readerCts.Dispose();
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
