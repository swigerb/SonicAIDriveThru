using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// #8 (PR #42 review item 9): app/backend/config.yaml pins `connection.ws_heartbeat_seconds: 15.0`
/// on rtmt.py's own `web.WebSocketResponse(heartbeat=..., ...)` -- the socket facing the browser,
/// where aiohttp is the SERVER parsing incoming client frames. The exact regression this proves
/// survived (aio-libs/aiohttp#13274, also referenced by <see cref="WebSocketCompressionTests"/>)
/// is a buggy aiohttp frame parser choking on a PONG frame immediately followed, in the SAME
/// read/TCP segment, by a further data frame.
///
/// .NET's <see cref="System.Net.WebSockets.ClientWebSocket"/> cannot reproduce this: it never
/// exposes a way to send an unsolicited Pong, and even if it could, it writes each frame in its
/// own <c>Stream.Write</c> call, so the two frames would never land in the server's read buffer
/// together. This test instead speaks raw WebSocket frames over a bare <see cref="TcpClient"/> --
/// doing the HTTP Upgrade handshake by hand, then writing a hand-built Pong frame concatenated
/// with a hand-built Text frame (the browser's own startSession() session.update) into a SINGLE
/// byte buffer and a SINGLE stream write -- so the two frames are guaranteed to arrive in the same
/// TCP segment / server-side read, which is the actual trigger condition for the bug. This
/// replaces the previous version of this test, which could only observe the real, config-driven
/// 15-second heartbeat black-box (a genuine ~17s real-time wait); constructing the frames directly
/// reproduces the specific byte sequence that matters in a fraction of a second.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class HeartbeatPongSurvivalTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Data_frame_sent_immediately_after_a_raw_pong_frame_in_the_same_write_is_still_processed() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        var backendUri = fixture.Backend!.BaseUri;

        using var http = new HttpClient();
        using var tokenResponse = await http.GetAsync(new Uri(backendUri, "/api/auth/session"), ct);
        tokenResponse.EnsureSuccessStatusCode();
        using var tokenDoc = JsonDocument.Parse(await tokenResponse.Content.ReadAsStreamAsync(ct));
        var token = tokenDoc.RootElement.GetProperty("token").GetString();

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(backendUri.Host, backendUri.Port, ct);
        await using var stream = tcp.GetStream();

        var keyBytes = new byte[16];
        Random.Shared.NextBytes(keyBytes);
        var secWebSocketKey = Convert.ToBase64String(keyBytes);
        var origin = $"{backendUri.Scheme}://{backendUri.Authority}";
        var request =
            $"GET /realtime?token={Uri.EscapeDataString(token ?? "")} HTTP/1.1\r\n" +
            $"Host: {backendUri.Host}:{backendUri.Port}\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Key: {secWebSocketKey}\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            $"Origin: {origin}\r\n" +
            "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);

        var handshakeResponse = await ReadHttpHeadersAsync(stream, ct);
        Assert.Contains("101", handshakeResponse.Split("\r\n", 2)[0], StringComparison.Ordinal);

        // The browser's own startSession() session.update (see
        // RealtimeBrowserClient.SendStartSessionAsync) -- a real data frame that should trigger a
        // full greeting round trip if the connection is still healthy after the PONG.
        var startSessionJson = """{"type":"session.update","session":{"turn_detection":{"type":"server_vad","threshold":0.7,"prefix_padding_ms":300,"silence_duration_ms":500},"input_audio_transcription":{"model":"whisper-1"}}}""";

        var pongFrame = BuildMaskedClientFrame(opcode: 0xA, payload: []); // unsolicited Pong, empty payload
        var textFrame = BuildMaskedClientFrame(opcode: 0x1, payload: Encoding.UTF8.GetBytes(startSessionJson));

        // The whole point: both frames in ONE write, so they land in the server's read buffer
        // together -- the exact byte-adjacency #13274 was about, reproduced deterministically
        // instead of waited for.
        var combined = new byte[pongFrame.Length + textFrame.Length];
        Buffer.BlockCopy(pongFrame, 0, combined, 0, pongFrame.Length);
        Buffer.BlockCopy(textFrame, 0, combined, pongFrame.Length, textFrame.Length);
        await stream.WriteAsync(combined, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(FrameTimeout);
        var sawRoundTripToken = false;
        try
        {
            while (!sawRoundTripToken)
            {
                var (opcode, payload) = await ReadServerFrameAsync(stream, timeoutCts.Token);
                if (opcode == 0x8) // Close
                {
                    Assert.Fail("Server closed the connection instead of processing the frame after the PONG.");
                }
                if (opcode != 0x1) // only Text frames carry JSON application data
                {
                    continue;
                }
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "extension.round_trip_token")
                {
                    sawRoundTripToken = true;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Assert.Fail($"Never observed extension.round_trip_token within {FrameTimeout} after the PONG-then-data write -- " +
                "the connection did not survive the PONG-then-data sequence, or stopped forwarding frames.");
        }

        Assert.True(sawRoundTripToken);
    });

    /// <summary>Reads raw HTTP response header bytes up to (and including) the blank-line terminator.</summary>
    private static async Task<string> ReadHttpHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var single = new byte[1];
        while (!sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(single.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                throw new IOException("Connection closed before the WebSocket handshake completed.");
            }
            sb.Append((char)single[0]);
        }
        return sb.ToString();
    }

    /// <summary>Builds a single masked (client-to-server, RFC 6455 requires masking) WebSocket frame.</summary>
    private static byte[] BuildMaskedClientFrame(byte opcode, byte[] payload)
    {
        if (payload.Length > ushort.MaxValue)
        {
            throw new NotSupportedException("This minimal test helper doesn't support 64-bit extended frame lengths.");
        }

        var mask = new byte[4];
        Random.Shared.NextBytes(mask);
        var masked = new byte[payload.Length];
        for (var i = 0; i < payload.Length; i++)
        {
            masked[i] = (byte)(payload[i] ^ mask[i % 4]);
        }

        using var ms = new MemoryStream();
        ms.WriteByte((byte)(0x80 | opcode)); // FIN=1, opcode
        if (payload.Length < 126)
        {
            ms.WriteByte((byte)(0x80 | payload.Length)); // MASK=1, length
        }
        else
        {
            ms.WriteByte(0x80 | 126);
            ms.WriteByte((byte)(payload.Length >> 8));
            ms.WriteByte((byte)(payload.Length & 0xFF));
        }
        ms.Write(mask, 0, 4);
        ms.Write(masked, 0, masked.Length);
        return ms.ToArray();
    }

    /// <summary>Reads one raw (server-to-client, unmasked per RFC 6455) WebSocket frame.</summary>
    private static async Task<(byte Opcode, byte[] Payload)> ReadServerFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[2];
        await ReadExactAsync(stream, header, cancellationToken);
        var opcode = (byte)(header[0] & 0x0F);
        var masked = (header[1] & 0x80) != 0;
        long length = header[1] & 0x7F;
        if (length == 126)
        {
            var ext = new byte[2];
            await ReadExactAsync(stream, ext, cancellationToken);
            length = (ext[0] << 8) | ext[1];
        }
        else if (length == 127)
        {
            var ext = new byte[8];
            await ReadExactAsync(stream, ext, cancellationToken);
            length = 0;
            foreach (var b in ext)
            {
                length = (length << 8) | b;
            }
        }

        byte[]? maskKey = null;
        if (masked)
        {
            maskKey = new byte[4];
            await ReadExactAsync(stream, maskKey, cancellationToken);
        }

        var payload = new byte[length];
        await ReadExactAsync(stream, payload, cancellationToken);
        if (masked)
        {
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] ^= maskKey![i % 4];
            }
        }
        return (opcode, payload);
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new IOException("Connection closed while reading a WebSocket frame.");
            }
            offset += read;
        }
    }
}
