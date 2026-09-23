using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Conformance.Fakes;

/// <summary>
/// A Kestrel-hosted fake of the Azure OpenAI GA realtime endpoint
/// (`/openai/v1/realtime?model=`), reproducing just enough of the GA contract for black-box
/// conformance testing: `session.created`/`session.updated` lifecycle, the GA `session.update`
/// validation rules (unknown top-level keys, `cannot_update_voice`, `reasoning` rejection on
/// "1.5"-style deployments), and a scriptable `response.create` -> `response.*` reply sequence.
/// Records every frame it receives so tests can assert exact wire ordering.
/// </summary>
public sealed class FakeRealtimeUpstreamServer : IAsyncDisposable
{
    private WebApplication? _app;
    private readonly ConnectionRegistry _connections = new();

    /// <summary>Mutated by tests to control what happens when the backend creates a response.</summary>
    public RealtimeScript Script { get; } = new();

    public Uri BaseUri { get; private set; } = new("http://127.0.0.1:0");

    /// <summary>Number of accepted upstream connections whose socket loop hasn't exited yet.</summary>
    public int OpenConnectionCount => _connections.OpenCount;

    /// <summary>
    /// Waits for the next upstream connection accepted after this call — not one already open —
    /// so tests can assert on a specific connection's own <see cref="FakeRealtimeConnection.ReceivedFrames"/>
    /// instead of a server-wide log that every past and future connection shares.
    /// </summary>
    public Task<FakeRealtimeConnection?> WaitForNextConnectionAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        _connections.WaitForNextAsync(timeout, cancellationToken);

    /// <summary>
    /// Waits until no accepted connection still has an open socket loop. Tests should call this
    /// at the start of a scenario and fail loudly on false — a still-open connection means a
    /// previous test leaked one, which is exactly what let "first frame" assertions pass on the
    /// wrong test's frame before per-connection identity existed.
    /// </summary>
    public Task<bool> WaitForNoOpenConnectionsAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        _connections.WaitForNoneOpenAsync(timeout, cancellationToken);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/", () => Results.Ok());
        app.Map("/openai/v1/realtime", HandleConnectionAsync);

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;
        BaseUri = new Uri(app.Urls.First());
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleConnectionAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var connection = _connections.Add(context.Request.Headers["api-key"], context.Request.Query["model"]);
        var deployment = context.Request.Query["model"].ToString();

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var state = new RealtimeSessionState();
        var ct = context.RequestAborted;

        try
        {
            await WebSocketJson.SendAsync(socket, BuildSessionCreated(), ct).ConfigureAwait(false);

            while (socket.State == WebSocketState.Open)
            {
                var received = await WebSocketJson.ReceiveJsonAsync(socket, ct).ConfigureAwait(false);
                if (received is null)
                {
                    break;
                }

                var frame = connection.ReceivedFrames.Add(received.Value);
                await HandleFrameAsync(socket, frame, state, deployment, ct).ConfigureAwait(false);
            }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                // CloseReceived means the client already sent its close frame (observed via the null
                // return from ReceiveJsonAsync above) — we still owe it the server-side close frame
                // to complete the handshake cleanly, otherwise the client sees an abrupt disconnect.
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    // Client may have already torn down the connection — best-effort close.
                }
            }
        }
        finally
        {
            _connections.NotifyClosed(connection);
        }
    }

    private async Task HandleFrameAsync(
        WebSocket socket, RecordedFrame frame, RealtimeSessionState state, string deployment, CancellationToken ct)
    {
        switch (frame.Type)
        {
            case "session.update":
                await HandleSessionUpdateAsync(socket, frame, state, deployment, ct).ConfigureAwait(false);
                break;
            case "response.create":
                if (Script.AutoRespond)
                {
                    await RespondAsync(socket, state, ct).ConfigureAwait(false);
                }
                break;
        }
    }

    private async Task HandleSessionUpdateAsync(
        WebSocket socket, RecordedFrame frame, RealtimeSessionState state, string deployment, CancellationToken ct)
    {
        var result = GaSessionValidator.Validate(frame.Json, state, deployment);
        var eventId = TryGetString(frame.Json, "event_id");

        if (!result.IsAccepted)
        {
            var error = new JsonObject
            {
                ["type"] = "error",
                ["event_id"] = $"evt_{Guid.NewGuid():N}",
                ["error"] = new JsonObject
                {
                    ["type"] = "invalid_request_error",
                    ["code"] = result.Code,
                    ["message"] = result.Message,
                    ["param"] = result.Param,
                    ["event_id"] = result.EchoEventId ? eventId : null,
                },
            };
            await WebSocketJson.SendAsync(socket, error, ct).ConfigureAwait(false);
            return;
        }

        if (frame.Json.TryGetProperty("session", out var session))
        {
            var voice = TryGetVoice(session);
            if (voice is not null)
            {
                state.CurrentVoice = voice;
            }
        }

        var updated = new JsonObject
        {
            ["type"] = "session.updated",
            ["event_id"] = $"evt_{Guid.NewGuid():N}",
            ["session"] = new JsonObject
            {
                ["id"] = "sess_fake",
                ["object"] = "realtime.session",
                ["model"] = deployment,
            },
        };
        await WebSocketJson.SendAsync(socket, updated, ct).ConfigureAwait(false);
    }

    private async Task RespondAsync(WebSocket socket, RealtimeSessionState state, CancellationToken ct)
    {
        var script = Script.QueuedResponses.Count > 0 ? Script.QueuedResponses.Dequeue() : ResponseScript.Default;
        var responseId = $"resp_{Guid.NewGuid():N}";

        await WebSocketJson.SendAsync(socket, new JsonObject
        {
            ["type"] = "response.created",
            ["response"] = new JsonObject { ["id"] = responseId, ["status"] = "in_progress" },
        }, ct).ConfigureAwait(false);

        foreach (var evt in script.Events)
        {
            switch (evt)
            {
                case AudioDeltaEvent audio:
                    await WebSocketJson.SendAsync(socket, new JsonObject
                    {
                        ["type"] = "response.output_audio.delta",
                        ["response_id"] = responseId,
                        ["delta"] = audio.Base64Delta,
                    }, ct).ConfigureAwait(false);
                    // GA rejects session.update's `voice` field once any assistant audio has been
                    // sent on the session (cannot_update_voice) — this is the one and only place
                    // the fake actually emits audio, so it is the one and only place that must
                    // flip the flag GaSessionValidator checks.
                    state.AssistantAudioSeen = true;
                    break;

                case FunctionCallEvent call:
                    await WebSocketJson.SendAsync(socket, new JsonObject
                    {
                        ["type"] = "response.function_call_arguments.done",
                        ["response_id"] = responseId,
                        ["call_id"] = call.CallId,
                        ["name"] = call.Name,
                        ["arguments"] = call.ArgumentsJson,
                    }, ct).ConfigureAwait(false);
                    break;

                case DoneEvent done:
                    var responseBody = new JsonObject
                    {
                        ["id"] = responseId,
                        ["status"] = done.Status,
                    };
                    if (done.ErrorCode is not null)
                    {
                        responseBody["status_details"] = new JsonObject
                        {
                            ["type"] = done.Status,
                            ["error"] = new JsonObject
                            {
                                ["code"] = done.ErrorCode,
                                ["message"] = done.ErrorMessage,
                            },
                        };
                    }
                    await WebSocketJson.SendAsync(socket, new JsonObject
                    {
                        ["type"] = "response.done",
                        ["response"] = responseBody,
                    }, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    private static JsonObject BuildSessionCreated() => new()
    {
        ["type"] = "session.created",
        ["event_id"] = $"evt_{Guid.NewGuid():N}",
        ["session"] = new JsonObject
        {
            ["id"] = "sess_fake",
            ["object"] = "realtime.session",
        },
    };

    private static string? TryGetString(JsonElement obj, string property) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? TryGetVoice(JsonElement session) =>
        session.TryGetProperty("audio", out var audio) &&
        audio.ValueKind == JsonValueKind.Object &&
        audio.TryGetProperty("output", out var output) &&
        output.ValueKind == JsonValueKind.Object &&
        output.TryGetProperty("voice", out var voice) &&
        voice.ValueKind == JsonValueKind.String
            ? voice.GetString()
            : null;
}
