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

    /// <summary>Every frame the backend has sent down to this client, in arrival order.</summary>
    public FrameLog ReceivedFrames { get; } = new();

    /// <summary>The close status the backend sent, or null if the socket is still open.</summary>
    public WebSocketCloseStatus? CloseStatus => _socket.CloseStatus;

    /// <summary>The close reason text the backend sent, or null if the socket is still open.</summary>
    public string? CloseStatusDescription => _socket.CloseStatusDescription;

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
    /// Awaits the reader loop observing the socket close (server Close frame or the connection
    /// dropping), bounded by <paramref name="timeout"/> instead of a fixed sleep, so tests can
    /// assert on <see cref="CloseStatus"/> deterministically.
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
            while (_socket.State == WebSocketState.Open)
            {
                var frame = await WebSocketJson.ReceiveJsonAsync(_socket, cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    return;
                }
                ReceivedFrames.Add(frame.Value);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on disposal.
        }
        catch (WebSocketException)
        {
            // Socket torn down from under the reader — expected on disposal/backend close.
        }
        finally
        {
            _closed.TrySetResult();
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
