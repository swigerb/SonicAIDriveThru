namespace Conformance.Fakes;

/// <summary>
/// One accepted upstream WebSocket connection to <see cref="FakeRealtimeUpstreamServer"/>, with
/// its own frame log and connect-time metadata. Before this existed, the fake kept a single
/// server-wide <see cref="FrameLog"/> shared by every connection it had ever accepted, so a
/// "first frame on this socket" assertion in one test could be satisfied by a frame recorded on
/// a *different* test's still-open (or already-closed) connection.
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

    internal FakeRealtimeConnection(string? apiKeyHeader, string? modelQueryParam)
    {
        ApiKeyHeader = apiKeyHeader;
        ModelQueryParam = modelQueryParam;
    }

    internal void MarkClosed() => IsClosed = true;
}
