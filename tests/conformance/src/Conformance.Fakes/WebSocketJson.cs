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
    public static async Task<JsonElement?> ReceiveJsonAsync(WebSocket socket, CancellationToken cancellationToken = default) =>
        (await ReceiveJsonOrCloseAsync(socket, cancellationToken).ConfigureAwait(false)).Json;

    /// <summary>
    /// Reads one full text message (concatenating fragments) and parses it as JSON, or observes a
    /// Close frame. Unlike <see cref="ReceiveJsonAsync"/>, a Close frame is distinguishable from a
    /// dropped/errored connection: <see cref="WebSocketReceiveResult.CloseStatus"/> and
    /// <see cref="WebSocketReceiveResult.CloseStatusDescription"/> are populated by the runtime as
    /// part of the very same <see cref="WebSocket.ReceiveAsync(ArraySegment{byte}, CancellationToken)"/>
    /// call that observed the Close frame, in the same result object — unlike the socket's own
    /// <see cref="WebSocket.CloseStatus"/>/<see cref="WebSocket.CloseStatusDescription"/> properties,
    /// which are set by the underlying WebSocket implementation on its own schedule and were observed
    /// to still read null on Linux immediately after this call returns (CI run 35936172326). Callers
    /// that need to assert on the close status/description should capture it from here, not from the
    /// socket, to avoid that platform-dependent race.
    /// </summary>
    public static async Task<WebSocketMessageOrClose> ReceiveJsonOrCloseAsync(
        WebSocket socket, CancellationToken cancellationToken = default)
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
                return WebSocketMessageOrClose.Dropped;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return WebSocketMessageOrClose.Close(result.CloseStatus, result.CloseStatusDescription);
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        if (stream.Length == 0)
        {
            return WebSocketMessageOrClose.Dropped;
        }

        stream.Position = 0;
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return WebSocketMessageOrClose.Message(document.RootElement.Clone());
    }
}

/// <summary>
/// The outcome of one <see cref="WebSocketJson.ReceiveJsonOrCloseAsync"/> call: a parsed JSON
/// message, an observed Close frame (with its status captured from the
/// <see cref="WebSocketReceiveResult"/> itself), or a dropped/errored connection that never
/// produced a Close frame at all.
/// </summary>
public readonly record struct WebSocketMessageOrClose
{
    public JsonElement? Json { get; private init; }
    public bool IsClose { get; private init; }
    public WebSocketCloseStatus? CloseStatus { get; private init; }
    public string? CloseStatusDescription { get; private init; }

    public static WebSocketMessageOrClose Message(JsonElement json) => new() { Json = json };

    public static WebSocketMessageOrClose Close(WebSocketCloseStatus? status, string? description) =>
        new() { IsClose = true, CloseStatus = status, CloseStatusDescription = description };

    public static readonly WebSocketMessageOrClose Dropped = new();
}
