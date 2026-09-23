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
/// `startSession()`. Not a browser — no Origin header, no permessage-deflate offer by default —
/// but byte-for-byte identical on the messages that matter for conformance.
/// </summary>
public sealed class RealtimeBrowserClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _readerCts = new();
    private Task? _readerTask;

    /// <summary>Every frame the backend has sent down to this client, in arrival order.</summary>
    public FrameLog ReceivedFrames { get; } = new();

    public static async Task<RealtimeBrowserClient> ConnectAsync(
        Uri backendBaseUri, bool offerDeflate = false, CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient();
        var tokenResponse = await http.GetFromJsonAsyncSafe(new Uri(backendBaseUri, "/api/auth/session"), cancellationToken)
            .ConfigureAwait(false);
        var token = tokenResponse.GetProperty("token").GetString();

        var client = new RealtimeBrowserClient();
        if (offerDeflate)
        {
            client._socket.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
        }

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

    public Task SendAsync(JsonNode command, CancellationToken cancellationToken = default) =>
        WebSocketJson.SendAsync(_socket, JsonSerializer.SerializeToElement(command), cancellationToken);

    /// <summary>The negotiated `Sec-WebSocket-Extensions` response header, or null if none was granted.</summary>
    public string? NegotiatedExtensions =>
        _socket.HttpResponseHeaders?.TryGetValue("Sec-WebSocket-Extensions", out var values) == true
            ? string.Join(", ", values)
            : null;

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
    }

    public async ValueTask DisposeAsync()
    {
        await _readerCts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
            // Best-effort close.
        }

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
