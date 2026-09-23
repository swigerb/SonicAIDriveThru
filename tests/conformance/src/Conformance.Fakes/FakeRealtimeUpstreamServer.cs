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

    /// <summary>Every frame received from the backend, in arrival order.</summary>
    public FrameLog ReceivedFrames { get; } = new();

    /// <summary>Mutated by tests to control what happens when the backend creates a response.</summary>
    public RealtimeScript Script { get; } = new();

    public Uri BaseUri { get; private set; } = new("http://127.0.0.1:0");

    /// <summary>The last `api-key` header observed on a connect request, for auth-plumbing assertions.</summary>
    public string? LastApiKeyHeader { get; private set; }

    /// <summary>The last `model` query-string value observed on a connect request.</summary>
    public string? LastModelQueryParam { get; private set; }

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

        LastApiKeyHeader = context.Request.Headers["api-key"];
        LastModelQueryParam = context.Request.Query["model"];
        var deployment = context.Request.Query["model"].ToString();

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var state = new RealtimeSessionState();
        var ct = context.RequestAborted;

        await WebSocketJson.SendAsync(socket, BuildSessionCreated(), ct).ConfigureAwait(false);

        while (socket.State == WebSocketState.Open)
        {
            var received = await WebSocketJson.ReceiveJsonAsync(socket, ct).ConfigureAwait(false);
            if (received is null)
            {
                break;
            }

            var frame = ReceivedFrames.Add(received.Value);
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
                    await RespondAsync(socket, ct).ConfigureAwait(false);
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

    private async Task RespondAsync(WebSocket socket, CancellationToken ct)
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
