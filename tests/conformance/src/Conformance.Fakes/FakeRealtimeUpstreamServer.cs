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
    private readonly Lock _rejectionGate = new();
    private readonly Queue<int> _pendingHandshakeRejections = new();

    public Uri BaseUri { get; private set; } = new("http://127.0.0.1:0");

    /// <summary>
    /// Queues an HTTP status (e.g. 401 for a bad api-key, 429 for rate-limited before a session
    /// even starts) that the *next* upgrade attempt is rejected with instead of being accepted as
    /// a WebSocket connection. FIFO across multiple calls; consumed one-per-attempt. This has to
    /// live at the server level (not on <see cref="FakeRealtimeConnection"/>) because there is no
    /// connection object yet at the point a handshake is rejected.
    /// </summary>
    public void RejectNextConnectionWith(int httpStatusCode)
    {
        lock (_rejectionGate)
        {
            _pendingHandshakeRejections.Enqueue(httpStatusCode);
        }
    }

    private readonly Lock _suppressionGate = new();
    private int _pendingSessionUpdatedSuppressions;

    /// <summary>
    /// Arms a one-shot suppression: the next `session.update` accepted on any connection is
    /// validated and merged into that connection's effective session as normal, but no
    /// `session.updated` response is sent back. FIFO across multiple calls, like
    /// <see cref="RejectNextConnectionWith"/>. Used to exercise a backend's session-configured
    /// fallback timeout (e.g. CONFORMANCE_GREETING_TIMEOUT_SECONDS) deterministically — armed
    /// before the connection is even created, so there is no race with the connection's own
    /// bootstrap `session.update` arriving first.
    /// </summary>
    public void SuppressNextSessionUpdatedResponse()
    {
        lock (_suppressionGate)
        {
            _pendingSessionUpdatedSuppressions++;
        }
    }

    private bool TryConsumeSessionUpdatedSuppression()
    {
        lock (_suppressionGate)
        {
            if (_pendingSessionUpdatedSuppressions > 0)
            {
                _pendingSessionUpdatedSuppressions--;
                return true;
            }
            return false;
        }
    }

    /// <summary>Number of accepted upstream connections whose socket loop hasn't exited yet.</summary>
    public int OpenConnectionCount => _connections.OpenCount;

    /// <summary>
    /// When true, the handshake is refused with 401 (matching GA's real behaviour) unless the
    /// `api-key` header is present and, if <see cref="ExpectedApiKey"/> is set, matches it.
    /// Defaults to false so the many direct-connect scripting tests that don't care about auth
    /// (and never set the header) keep working unchanged — opt in per test/fixture instead of
    /// forcing every caller to authenticate. <c>ConformanceFixture</c> turns this on for the
    /// shared instance the real Python backend connects through, since rtmt.py always sends
    /// `api-key` for key auth (see BackendContract.OpenAiApiKey), so real end-to-end
    /// scenarios exercise this path with zero risk of a false failure.
    /// </summary>
    public bool RequireApiKey { get; set; }

    /// <summary>The exact `api-key` value to require when <see cref="RequireApiKey"/> is true. Null means "any non-empty value is accepted".</summary>
    public string? ExpectedApiKey { get; set; }

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

    public async Task StartAsync(CancellationToken cancellationToken = default, int? fixedPort = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{fixedPort?.ToString() ?? "0"}");
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

        int? rejection;
        lock (_rejectionGate)
        {
            rejection = _pendingHandshakeRejections.Count > 0 ? _pendingHandshakeRejections.Dequeue() : null;
        }
        if (rejection is not null)
        {
            // Do not call AcceptWebSocketAsync -- setting the status code on an unaccepted
            // upgrade request makes Kestrel return a plain HTTP error instead of completing the
            // 101 handshake, exactly like a real gateway rejecting a bad api-key or applying
            // rate-limiting before the session even starts.
            context.Response.StatusCode = rejection.Value;
            return;
        }

        if (RequireApiKey)
        {
            var apiKey = context.Request.Headers["api-key"].ToString();
            var keyMissing = string.IsNullOrEmpty(apiKey);
            var keyMismatched = !keyMissing && ExpectedApiKey is not null &&
                !string.Equals(apiKey, ExpectedApiKey, StringComparison.Ordinal);
            if (keyMissing || keyMismatched)
            {
                // Live-confirmed shape (2026-09-24): a missing Authorization header fails the
                // handshake itself with HTTP 401 -- no JSON error frame, same as the rejection
                // queue above.
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }

        var deployment = context.Request.Query["model"].ToString();

        var connection = _connections.Add(context.Request.Headers["api-key"], context.Request.Query["model"]);
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        connection.AttachSocket(socket);
        var ct = context.RequestAborted;

        // Frame handling runs off the receive loop (non-blocking receive loop, item 8) so a
        // slow-streaming response.create reply, a VAD-default echo, and the next incoming client
        // frame can all be in flight concurrently -- writes are serialized by
        // FakeRealtimeConnection.SendAsync's own lock, not by this loop. Outstanding handler
        // tasks are tracked and drained before the connection is marked closed so "no open
        // connections" can't go true while a handler is still writing to the (already-closing)
        // socket.
        var outstanding = new List<Task>();
        var outstandingGate = new Lock();

        void TrackHandler(Task task)
        {
            lock (outstandingGate)
            {
                outstanding.RemoveAll(t => t.IsCompleted);
                outstanding.Add(task);
            }
        }

        try
        {
            await connection.SendAsync(BuildSessionCreated(), ct).ConfigureAwait(false);

            while (socket.State == WebSocketState.Open)
            {
                var received = await WebSocketJson.ReceiveJsonAsync(socket, ct).ConfigureAwait(false);
                if (received is null)
                {
                    break;
                }

                var frame = connection.ReceivedFrames.Add(received.Value);
                TrackHandler(HandleFrameAsync(connection, frame, deployment, ct));
            }

            List<Task> toAwait;
            lock (outstandingGate)
            {
                toAwait = [.. outstanding];
            }
            await Task.WhenAll(toAwait).ConfigureAwait(false);

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                // CloseReceived means the client already sent its close frame (observed via the null
                // return from ReceiveJsonAsync above) — we still owe it the server-side close frame
                // to complete the handshake cleanly, otherwise the client sees an abrupt disconnect.
                await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _connections.NotifyClosed(connection);
        }
    }

    private async Task HandleFrameAsync(FakeRealtimeConnection connection, RecordedFrame frame, string deployment, CancellationToken ct)
    {
        // GA rejects any top-level client event `type` it doesn't recognise (item 9 of PR #22's
        // review) — checked before dispatch so an unrecognised type never reaches the switch
        // below or the rule-based triggers, exactly like the real service refusing to act on it
        // at all. Catches a leaked internal frame type (e.g. a stray `extension.*` frame)
        // forwarded upstream unchanged by mistake.
        var typeCheck = GaSessionValidator.ValidateClientEventType(frame.Type);
        if (!typeCheck.IsAccepted)
        {
            await SendValidationErrorAsync(connection, typeCheck, TryGetString(frame.Json, "event_id"), ct).ConfigureAwait(false);
            return;
        }

        switch (frame.Type)
        {
            case "session.update":
                await HandleSessionUpdateAsync(connection, frame, deployment, ct).ConfigureAwait(false);
                break;
            case "response.create":
                if (connection.Script.AutoRespond)
                {
                    await RespondAsync(connection, ct).ConfigureAwait(false);
                }
                break;
        }

        // Rule-based triggers (VAD-like defaults plus anything a test added via Script.On) run
        // for every frame type, independent of — and in addition to — the two built-in handlers
        // above, since real GA acknowledges input-buffer/conversation-item frames regardless of
        // whether a response is also in flight.
        foreach (var rule in connection.Script.Rules)
        {
            if (rule.Predicate(frame))
            {
                await rule.Handler(connection, frame, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task SendValidationErrorAsync(
        FakeRealtimeConnection connection, SessionUpdateValidationResult result, string? eventId, CancellationToken ct)
    {
        var error = new JsonObject
        {
            ["type"] = "error",
            ["event_id"] = FakeRealtimeConnection.NewEventId(),
            ["error"] = new JsonObject
            {
                ["type"] = "invalid_request_error",
                ["code"] = result.Code,
                ["message"] = result.Message,
                ["param"] = result.Param,
                ["event_id"] = result.EchoEventId ? eventId : null,
            },
        };
        await connection.SendAsync(error, ct).ConfigureAwait(false);
    }

    private async Task HandleSessionUpdateAsync(FakeRealtimeConnection connection, RecordedFrame frame, string deployment, CancellationToken ct)
    {
        var state = connection.SessionState;
        var result = GaSessionValidator.Validate(frame.Json, state, deployment);
        var eventId = TryGetString(frame.Json, "event_id");

        if (!result.IsAccepted)
        {
            await SendValidationErrorAsync(connection, result, eventId, ct).ConfigureAwait(false);
            return;
        }

        if (frame.Json.TryGetProperty("session", out var session))
        {
            var voice = TryGetVoice(session);
            if (voice is not null)
            {
                state.CurrentVoice = voice;
            }

            // PR #22 review item 10: session.updated must echo the full effective session (GA's
            // session.update is a partial patch, so this accumulates rather than replaces).
            state.MergeSessionUpdate(session);
        }

        // id/object/model are server-assigned and always reflect this connection, regardless of
        // whatever the client's session.update body happened to include for them.
        var effective = JsonNode.Parse(state.EffectiveSession.ToJsonString())!.AsObject();
        effective["id"] = "sess_fake";
        effective["object"] = "realtime.session";
        effective["model"] = deployment;

        var updated = new JsonObject
        {
            ["type"] = "session.updated",
            ["event_id"] = FakeRealtimeConnection.NewEventId(),
            ["session"] = effective,
        };

        if (TryConsumeSessionUpdatedSuppression())
        {
            // Deliberately swallowed: the session state above is still merged/validated as
            // normal, we just never send the acknowledgement, simulating an upstream that never
            // confirms session configuration so a backend's fallback timeout path can be tested.
            return;
        }

        await connection.SendAsync(updated, ct).ConfigureAwait(false);
    }

    private async Task RespondAsync(FakeRealtimeConnection connection, CancellationToken ct)
    {
        var state = connection.SessionState;
        var script = connection.Script.QueuedResponses.TryDequeue(out var scripted)
            ? scripted
            : ResponseScript.Default;
        var responseId = $"resp_{Guid.NewGuid():N}";

        await connection.SendAsync(new JsonObject
        {
            ["type"] = "response.created",
            ["event_id"] = FakeRealtimeConnection.NewEventId(),
            ["response"] = new JsonObject { ["id"] = responseId, ["status"] = "in_progress" },
        }, ct).ConfigureAwait(false);

        // Accumulated into the final response.done's `output[]`, exactly like GA: every item this
        // response produced, each carrying its own completed `status`.
        var output = new JsonArray();
        var outputIndex = 0;

        // An open (in_progress) assistant "message" item that consecutive AudioDeltaEvents are
        // collected into — GA groups audio deltas under one output item + one content part, it
        // does not open a fresh item per delta.
        string? audioItemId = null;
        var audioContentIndex = 0;

        async Task CloseOpenAudioItemAsync()
        {
            if (audioItemId is null)
            {
                return;
            }

            await connection.SendAsync(new JsonObject
            {
                ["type"] = "response.output_audio.done",
                ["event_id"] = FakeRealtimeConnection.NewEventId(),
                ["response_id"] = responseId,
                ["item_id"] = audioItemId,
                ["output_index"] = outputIndex,
                ["content_index"] = audioContentIndex,
            }, ct).ConfigureAwait(false);
            await connection.SendAsync(new JsonObject
            {
                ["type"] = "response.content_part.done",
                ["event_id"] = FakeRealtimeConnection.NewEventId(),
                ["response_id"] = responseId,
                ["item_id"] = audioItemId,
                ["output_index"] = outputIndex,
                ["content_index"] = audioContentIndex,
                ["part"] = new JsonObject { ["type"] = "audio", ["transcript"] = "" },
            }, ct).ConfigureAwait(false);

            var completedItem = new JsonObject
            {
                ["id"] = audioItemId,
                ["type"] = "message",
                ["status"] = "completed",
                ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "audio", ["transcript"] = "" }),
            };
            await connection.SendAsync(new JsonObject
            {
                ["type"] = "response.output_item.done",
                ["event_id"] = FakeRealtimeConnection.NewEventId(),
                ["response_id"] = responseId,
                ["output_index"] = outputIndex,
                ["item"] = completedItem.DeepClone(),
            }, ct).ConfigureAwait(false);

            output.Add(completedItem.DeepClone());
            outputIndex++;
            audioItemId = null;
            audioContentIndex = 0;
        }

        foreach (var evt in script.Events)
        {
            switch (evt)
            {
                case AudioDeltaEvent audio:
                    if (audio.Pace is { } pace)
                    {
                        // Scripted pacing for barge-in scenarios: a real delay (not a
                        // synchronization sleep) between deltas so a test can send a
                        // response.cancel / new input_audio_buffer.append while a response is
                        // still streaming. Uses the connection's own TimeProvider so a future
                        // fake clock can make this deterministic without touching call sites.
                        await Task.Delay(pace, connection.TimeProvider, ct).ConfigureAwait(false);
                    }

                    if (audioItemId is null)
                    {
                        audioItemId = $"item_{Guid.NewGuid():N}";
                        var openItem = new JsonObject
                        {
                            ["id"] = audioItemId,
                            ["type"] = "message",
                            ["status"] = "in_progress",
                            ["role"] = "assistant",
                            ["content"] = new JsonArray(),
                        };
                        await connection.SendAsync(new JsonObject
                        {
                            ["type"] = "response.output_item.added",
                            ["event_id"] = FakeRealtimeConnection.NewEventId(),
                            ["response_id"] = responseId,
                            ["output_index"] = outputIndex,
                            ["item"] = openItem.DeepClone(),
                        }, ct).ConfigureAwait(false);
                        await connection.SendAsync(new JsonObject
                        {
                            ["type"] = "conversation.item.added",
                            ["event_id"] = FakeRealtimeConnection.NewEventId(),
                            ["previous_item_id"] = state.LastConversationItemId,
                            ["item"] = openItem.DeepClone(),
                        }, ct).ConfigureAwait(false);
                        state.LastConversationItemId = audioItemId;
                        await connection.SendAsync(new JsonObject
                        {
                            ["type"] = "response.content_part.added",
                            ["event_id"] = FakeRealtimeConnection.NewEventId(),
                            ["response_id"] = responseId,
                            ["item_id"] = audioItemId,
                            ["output_index"] = outputIndex,
                            ["content_index"] = audioContentIndex,
                            ["part"] = new JsonObject { ["type"] = "audio", ["transcript"] = "" },
                        }, ct).ConfigureAwait(false);
                    }

                    await connection.SendAsync(new JsonObject
                    {
                        ["type"] = "response.output_audio.delta",
                        ["event_id"] = FakeRealtimeConnection.NewEventId(),
                        ["response_id"] = responseId,
                        ["item_id"] = audioItemId,
                        ["output_index"] = outputIndex,
                        ["content_index"] = audioContentIndex,
                        ["delta"] = audio.Base64Delta,
                    }, ct).ConfigureAwait(false);
                    // GA rejects session.update's `voice` field once any assistant audio has been
                    // sent on the session (cannot_update_voice) — this is the one and only place
                    // the fake actually emits audio, so it is the one and only place that must
                    // flip the flag GaSessionValidator checks.
                    state.AssistantAudioSeen = true;
                    break;

                case FunctionCallEvent call:
                    // A function call is its own item — close out any open audio item first so
                    // output ordering matches a real turn (assistant says something, then calls
                    // a tool, rather than interleaving).
                    await CloseOpenAudioItemAsync().ConfigureAwait(false);

                    var callItemId = $"item_{Guid.NewGuid():N}";
                    var openCallItem = new JsonObject
                    {
                        ["id"] = callItemId,
                        ["type"] = "function_call",
                        ["status"] = "in_progress",
                        ["name"] = call.Name,
                        ["call_id"] = call.CallId,
                        ["arguments"] = "",
                    };
                    await connection.SendAsync(new JsonObject
                    {
                        ["type"] = "response.output_item.added",
                        ["event_id"] = FakeRealtimeConnection.NewEventId(),
                        ["response_id"] = responseId,
                        ["output_index"] = outputIndex,
                        ["item"] = openCallItem.DeepClone(),
                    }, ct).ConfigureAwait(false);
                    // rtmt.py reads the top-level `previous_item_id` off this exact event type
                    // (conversation.item.created | conversation.item.added) to remember what to
                    // stitch extension.middle_tier_tool_response's own previous_item_id to.
                    await connection.SendAsync(new JsonObject
                    {
                        ["type"] = "conversation.item.added",
                        ["event_id"] = FakeRealtimeConnection.NewEventId(),
                        ["previous_item_id"] = state.LastConversationItemId,
                        ["item"] = openCallItem.DeepClone(),
                    }, ct).ConfigureAwait(false);
                    state.LastConversationItemId = callItemId;

                    await connection.SendAsync(new JsonObject
                    {
                        ["type"] = "response.function_call_arguments.done",
                        ["event_id"] = FakeRealtimeConnection.NewEventId(),
                        ["response_id"] = responseId,
                        ["item_id"] = callItemId,
                        ["output_index"] = outputIndex,
                        ["call_id"] = call.CallId,
                        ["name"] = call.Name,
                        ["arguments"] = call.ArgumentsJson,
                    }, ct).ConfigureAwait(false);

                    var completedCallItem = new JsonObject
                    {
                        ["id"] = callItemId,
                        ["type"] = "function_call",
                        ["status"] = "completed",
                        ["name"] = call.Name,
                        ["call_id"] = call.CallId,
                        ["arguments"] = call.ArgumentsJson,
                    };
                    // rtmt.py's response.output_item.done handler is what actually invokes the
                    // backend tool and sends conversation.item.create(function_call_output)
                    // upstream — this frame is the trigger for item 3's tool-execution scenario.
                    await connection.SendAsync(new JsonObject
                    {
                        ["type"] = "response.output_item.done",
                        ["event_id"] = FakeRealtimeConnection.NewEventId(),
                        ["response_id"] = responseId,
                        ["output_index"] = outputIndex,
                        ["item"] = completedCallItem.DeepClone(),
                    }, ct).ConfigureAwait(false);

                    output.Add(completedCallItem.DeepClone());
                    outputIndex++;
                    break;

                case DoneEvent done:
                    await CloseOpenAudioItemAsync().ConfigureAwait(false);

                    var responseBody = new JsonObject
                    {
                        ["id"] = responseId,
                        ["status"] = done.Status,
                        ["output"] = output.DeepClone(),
                        ["usage"] = BuildUsage(output.Count),
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
                    await connection.SendAsync(new JsonObject
                    {
                        ["type"] = "response.done",
                        ["event_id"] = FakeRealtimeConnection.NewEventId(),
                        ["response"] = responseBody,
                    }, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>A minimal but GA-shaped usage object — real token counts are meaningless from a
    /// fake, but the backend's context-window tracking only reads `output[]`, so this exists
    /// purely so consumers that expect the `usage` key (as GA always sends it) don't have to
    /// special-case the fake.</summary>
    private static JsonObject BuildUsage(int outputItemCount)
    {
        var outputTokens = 16 * Math.Max(outputItemCount, 1);
        const int inputTokens = 32;
        return new JsonObject
        {
            ["total_tokens"] = inputTokens + outputTokens,
            ["input_tokens"] = inputTokens,
            ["output_tokens"] = outputTokens,
            ["input_token_details"] = new JsonObject
            {
                ["text_tokens"] = inputTokens,
                ["audio_tokens"] = 0,
                ["cached_tokens"] = 0,
            },
            ["output_token_details"] = new JsonObject
            {
                ["text_tokens"] = 0,
                ["audio_tokens"] = outputTokens,
            },
        };
    }

    private static JsonObject BuildSessionCreated() => new()
    {
        ["type"] = "session.created",
        ["event_id"] = FakeRealtimeConnection.NewEventId(),
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
