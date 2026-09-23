using System.Net.WebSockets;

namespace Conformance.Fakes;

/// <summary>
/// One accepted upstream WebSocket connection to <see cref="FakeRealtimeUpstreamServer"/>, with
/// its own frame log, connect-time metadata, and now (PR #22 review item 8) its own scripting
/// surface and send handle. Before this existed, the fake kept a single server-wide
/// <see cref="FrameLog"/> and a single server-wide <see cref="RealtimeScript"/> shared by every
/// connection it had ever accepted, so a "first frame on this socket" assertion — or a queued
/// response script — in one test could be satisfied/consumed by a frame recorded on, or a script
/// meant for, a *different* test's still-open (or already-closed) connection.
/// </summary>
public sealed class FakeRealtimeConnection
{
    /// <summary>Unique per accepted connection — never reused, even across a closed/reopened socket.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Every frame received on this connection's socket, in arrival order.</summary>
    public FrameLog ReceivedFrames { get; } = new();

    /// <summary>The `api-key` header this connection was opened with.</summary>
    public string? ApiKeyHeader { get; }

    /// <summary>The `model` query-string value this connection was opened with.</summary>
    public string? ModelQueryParam { get; }

    /// <summary>True once the connection's socket loop has exited (client closed, or the server tore it down).</summary>
    public bool IsClosed { get; private set; }

    /// <summary>
    /// Mutated by tests to control what this connection does automatically (queued
    /// `response.create` replies, rule-based triggers for other client frames). Starts fresh with
    /// the VAD-like defaults on every new connection — never carries state from a previous test.
    /// </summary>
    public RealtimeScript Script { get; } = RealtimeScript.WithVadDefaults();

    /// <summary>Validator and response-scripting state private to this connection's session lifecycle
    /// (current voice lock, whether assistant audio has gone out yet, last conversation item id).</summary>
    public RealtimeSessionState SessionState { get; } = new();

    /// <summary>This connection's clock — real by default; a test may not currently override it
    /// (the connection is constructed server-side), but scripted event handlers read it so a
    /// future per-server-instance <see cref="TimeProvider"/> injection point is a one-line change.</summary>
    public TimeProvider TimeProvider { get; }

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private WebSocket? _socket;

    internal FakeRealtimeConnection(string? apiKeyHeader, string? modelQueryParam, TimeProvider? timeProvider = null)
    {
        ApiKeyHeader = apiKeyHeader;
        ModelQueryParam = modelQueryParam;
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Attaches the accepted socket once the WebSocket upgrade handshake completes. Connections
    /// are registered (and visible to <see cref="ConnectionRegistry.WaitForNextAsync"/>) *before*
    /// the handshake finishes accepting, so this is a separate step rather than a constructor
    /// parameter — see <see cref="FakeRealtimeUpstreamServer"/>'s connection handler for why that
    /// ordering matters (a test's own <c>ConnectAsync</c> can otherwise race the registration).
    /// </summary>
    internal void AttachSocket(WebSocket socket) => _socket = socket;

    /// <summary>
    /// Sends one JSON frame on this connection's socket. A raw <see cref="WebSocket"/> does not
    /// support concurrent <c>SendAsync</c> calls from multiple callers, and now that frame
    /// handling runs off the receive loop (a non-blocking receive loop, per item 8) more than one
    /// handler/response stream can legitimately want to write to the same socket at once — this
    /// serializes them behind a send lock instead of corrupting the wire.
    /// </summary>
    public async Task SendAsync(object payload, CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_socket is not { State: WebSocketState.Open })
            {
                return;
            }
            await WebSocketJson.SendAsync(_socket, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Server-initiated graceful close handshake.</summary>
    public async Task CloseAsync(
        WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        if (_socket is { State: WebSocketState.Open or WebSocketState.CloseReceived })
        {
            try
            {
                await _socket.CloseAsync(status, description ?? string.Empty, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // Client may have already torn down the connection -- best-effort close.
            }
        }
    }

    /// <summary>Immediately terminates the underlying socket without a close handshake, for
    /// scenarios that need to simulate a hard upstream drop rather than a graceful close.</summary>
    public void Abort() => _socket?.Abort();

    internal void MarkClosed() => IsClosed = true;

    internal static string NewEventId() => $"evt_{Guid.NewGuid():N}";
}
