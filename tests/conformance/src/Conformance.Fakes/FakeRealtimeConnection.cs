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

    private readonly List<Exception> _handlerFaults = [];
    private readonly Lock _handlerFaultsGate = new();

    /// <summary>
    /// Records an exception thrown by a frame handler (a built-in dispatch case or a
    /// <see cref="RealtimeScript"/> rule) running off the non-blocking receive loop. These used to
    /// be silently lost — nothing awaited the per-frame handler task until the connection closed,
    /// and even then only the first exception from <c>Task.WhenAll</c> would surface, deep inside
    /// Kestrel's request pipeline where no test would ever see it (PR #22 review item N3).
    /// </summary>
    internal void RecordHandlerFault(Exception exception)
    {
        lock (_handlerFaultsGate)
        {
            _handlerFaults.Add(exception);
        }
    }

    /// <summary>Snapshot of every handler fault recorded so far and clears them — see
    /// <see cref="FakeRealtimeUpstreamServer.AssertNoHandlerFaults"/>, which drains this once per
    /// scenario so a fault from one test can never silently fail (or silently pass) another.</summary>
    internal IReadOnlyList<Exception> DrainHandlerFaults()
    {
        lock (_handlerFaultsGate)
        {
            if (_handlerFaults.Count == 0)
            {
                return [];
            }

            var drained = _handlerFaults.ToArray();
            _handlerFaults.Clear();
            return drained;
        }
    }

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
    /// are constructed via <see cref="ConnectionRegistry.Create"/> but not published (visible to
    /// <see cref="ConnectionRegistry.WaitForNextAsync"/>) until *after* this runs — see
    /// <see cref="ConnectionRegistry.Publish"/> and <see cref="FakeRealtimeUpstreamServer"/>'s
    /// connection handler for why that ordering matters (a test's own immediate
    /// <c>SendAsync</c> after <c>WaitForNextConnectionAsync</c> would otherwise race the socket
    /// attach and throw or silently no-op).
    /// </summary>
    internal void AttachSocket(WebSocket socket) => _socket = socket;

    /// <summary>
    /// Sends one JSON frame on this connection's socket. A raw <see cref="WebSocket"/> does not
    /// support concurrent <c>SendAsync</c> calls from multiple callers, and now that frame
    /// handling runs off the receive loop (a non-blocking receive loop, per item 8) more than one
    /// handler/response stream can legitimately want to write to the same socket at once — this
    /// serializes them behind a send lock instead of corrupting the wire.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The socket hasn't been attached yet (this connection was returned by
    /// <see cref="ConnectionRegistry.WaitForNextAsync"/> before <see cref="AttachSocket"/> ran —
    /// no longer possible since PR #22 review item N2, but kept as a defensive throw rather than a
    /// silent no-op), or it is no longer in the <see cref="WebSocketState.Open"/> state (already
    /// closing/closed). Silently swallowing a send here used to hide real bugs — a handler racing
    /// a closed connection would just look like "no response ever arrived" instead of a clear
    /// failure pointing at the actual cause.
    /// </exception>
    public async Task SendAsync(object payload, CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_socket is null)
            {
                throw new InvalidOperationException(
                    $"FakeRealtimeConnection {Id}: cannot send — the socket has not been attached yet " +
                    "(AttachSocket hasn't run). This connection should not have been observable yet; " +
                    "see ConnectionRegistry.Publish.");
            }

            if (_socket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException(
                    $"FakeRealtimeConnection {Id}: cannot send — the socket is in state " +
                    $"'{_socket.State}', not Open. The connection is already closing or closed.");
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
