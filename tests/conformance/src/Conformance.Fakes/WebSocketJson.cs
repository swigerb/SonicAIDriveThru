using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Conformance.Fakes;

/// <summary>Small send/receive helpers shared by the fakes and the browser test client.</summary>
public static class WebSocketJson
{
    private const int ReceiveBufferSize = 32 * 1024;

    public static async Task SendAsync(WebSocket socket, object payload, CancellationToken cancellationToken = default)
    {
        var json = payload is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(payload, payload.GetType());
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one full text message (concatenating fragments) and parses it as JSON.
    /// Returns null on a Close frame or cancellation-safe empty read.
    /// </summary>
    public static async Task<JsonElement?> ReceiveJsonAsync(WebSocket socket, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[ReceiveBufferSize];
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        if (stream.Length == 0)
        {
            return null;
        }

        stream.Position = 0;
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.Clone();
    }
}
