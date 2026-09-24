using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
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
    private bool _suppressSessionUpdatedOnNextConnection;

    /// <summary>
    /// Arms a one-shot switch: the *next* connection accepted (not any connection already open)
    /// will have <see cref="FakeRealtimeConnection.SuppressAllSessionUpdated"/> set from the
    /// moment it's constructed, so every `session.update` it ever sends is validated/merged as
    /// normal but never acknowledged. Replaces the previous server-wide, exact-count
    /// <c>SuppressNextSessionUpdatedResponse</c> (PR #22 review item N5) — that required a test to
    /// predict exactly how many `session.update` frames a whole connection's lifecycle would emit
    /// (the bootstrap send plus the browser's forwarded one, in
    /// <c>GreetingTimeoutFallbackTests</c>'s case) and call it that many times; a per-connection
    /// switch instead suppresses *all* of them for one connection, with nothing left to predict.
    /// Consumed by <see cref="HandleConnectionAsync"/> right when the connection is created (before
    /// the socket is even accepted), like <see cref="RejectNextConnectionWith"/>.
    /// </summary>
    public void SuppressSessionUpdatedOnNextConnection()
    {
        lock (_suppressionGate)
        {
            _suppressSessionUpdatedOnNextConnection = true;
        }
    }

    private bool ConsumeSessionUpdatedSuppressionForNewConnection()
    {
        lock (_suppressionGate)
        {
            if (!_suppressSessionUpdatedOnNextConnection)
            {
                return false;
            }
            _suppressSessionUpdatedOnNextConnection = false;
            return true;
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

    /// <summary>
    /// Drains and asserts there are no recorded handler faults across every connection this server
    /// has ever accepted. A scripted rule (or a built-in dispatch case) throwing used to be
    /// silently lost — nothing surfaced it to the test that scripted it, so a scenario would just
    /// look like "no response ever arrived" and fail (or worse, hang) with no hint at the real
    /// cause. Tests call this once at the end of a scenario (via <see cref="ConformanceFixture.RunAsync"/>)
    /// so a throwing rule's exception is surfaced directly instead (PR #22 review item N3).
    /// Draining (not just reading) means a fault from one scenario can never leak into failing —
    /// or silently disappearing from — a later one sharing the same fixture.
    /// </summary>
    /// <exception cref="Exception">The single recorded fault, rethrown with its original stack
    /// trace preserved, if exactly one connection faulted exactly once.</exception>
    /// <exception cref="AggregateException">Every recorded fault, if more than one was recorded.</exception>
    public void AssertNoHandlerFaults()
    {
        List<Exception> faults = [];
        foreach (var connection in _connections.Snapshot())
        {
            faults.AddRange(connection.DrainHandlerFaults());
        }

        switch (faults.Count)
        {
            case 0:
                return;
            case 1:
                ExceptionDispatchInfo.Capture(faults[0]).Throw();
                break;
            default:
                throw new AggregateException(
                    $"{faults.Count} handler faults were recorded across this scenario's connections.", faults);
        }
    }

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

        var connection = _connections.Create(context.Request.Headers["api-key"], context.Request.Query["model"]);
        if (ConsumeSessionUpdatedSuppressionForNewConnection())
        {
            connection.SuppressAllSessionUpdated = true;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        connection.AttachSocket(socket);
        // Only publish (making the connection visible to WaitForNextConnectionAsync) once the
        // socket is attached -- otherwise a test racing the handshake could observe the
        // connection and call SendAsync on it before there's anything to send on (item N2).
        _connections.Publish(connection);
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

        // Observes (rather than lets propagate) any exception thrown by a per-frame handler task
        // -- these run off the receive loop with nothing else awaiting them until the connection
        // closes, so an unhandled fault used to either vanish entirely or surface once, deep
        // inside Task.WhenAll below, in a place no test could see it. Recording it on the
        // connection instead lets AssertNoHandlerFaults surface it clearly at the end of a
        // scenario (PR #22 review item N3).
        async Task ObserveHandlerFaultsAsync(Task handlerTask)
        {
            try
            {
                await handlerTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                connection.RecordHandlerFault(ex);
            }
        }

        void TrackHandler(Task task)
        {
            var observed = ObserveHandlerFaultsAsync(task);
            lock (outstandingGate)
            {
                outstanding.RemoveAll(t => t.IsCompleted);
                outstanding.Add(observed);
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
                // GA (Response Create Event): "Only one Response can write to the default
                // Conversation at a time" — a second response.create while one is still active is
                // rejected rather than queued or silently ignored (PR #22 review item N7). Exact
                // error code NOT independently live-verified; see README "Response cancel — GA
                // semantics and unverified error codes".
                if (connection.ActiveResponseId is not null)
                {
                    await SendValidationErrorAsync(
                        connection,
                        SessionUpdateValidationResult.Rejected(
                            "conversation_already_has_active_response",
                            null,
                            "Only one response can be active on the default conversation at a time.",
                            echoEventId: true),
                        TryGetString(frame.Json, "event_id"),
                        ct).ConfigureAwait(false);
                    break;
                }

                if (connection.Script.AutoRespond)
                {
                    await RespondAsync(connection, ct).ConfigureAwait(false);
                }
                break;
            case "response.cancel":
                await HandleResponseCancelAsync(connection, frame, ct).ConfigureAwait(false);
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

        if (connection.SuppressAllSessionUpdated)
        {
            // Deliberately swallowed: the session state above is still merged/validated as
            // normal, we just never send the acknowledgement, simulating an upstream that never
            // confirms session configuration so a backend's fallback timeout path can be tested.
            return;
        }

        await connection.SendAsync(updated, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// GA (Response Cancel Event): "Send this event to cancel an in-progress response. The server
    /// will respond with a `response.done` event with a status of `response.status=cancelled`. If
    /// there is no response to cancel, the server will respond with an error. It's safe to call
    /// `response.cancel` even if no response is in progress, an error will be returned the session
    /// will remain unaffected." An optional `response_id` targets a specific response; GA: "if not
    /// provided, will cancel an in-progress response in the default conversation" — this fake only
    /// ever has one response active on the default conversation at a time, so an explicit
    /// `response_id` that doesn't match it is treated the same as "nothing to cancel" (PR #22
    /// review item N7). Exact error code/message/param NOT independently live-verified; see README
    /// "Response cancel — GA semantics and unverified error codes".
    /// </summary>
    private static async Task HandleResponseCancelAsync(FakeRealtimeConnection connection, RecordedFrame frame, CancellationToken ct)
    {
        var requestedResponseId = TryGetString(frame.Json, "response_id");
        var activeResponseId = connection.ActiveResponseId;
        var targetsActiveResponse = activeResponseId is not null &&
            (requestedResponseId is null || string.Equals(requestedResponseId, activeResponseId, StringComparison.Ordinal));

        if (!targetsActiveResponse)
        {
            await SendValidationErrorAsync(
                connection,
                SessionUpdateValidationResult.Rejected(
                    "response_cancel_not_active",
                    "response_id",
                    "No active response to cancel.",
                    echoEventId: true),
                TryGetString(frame.Json, "event_id"),
                ct).ConfigureAwait(false);
            return;
        }

        // Interrupts RespondAsync's streaming loop for this response — see the cancellation
        // handling there for how it turns this into a response.done with status "cancelled".
        connection.ActiveResponseCancellation?.Cancel();
    }

    private async Task RespondAsync(FakeRealtimeConnection connection, CancellationToken ct)
    {
        var state = connection.SessionState;
        var script = connection.Script.QueuedResponses.TryDequeue(out var scripted)
            ? scripted
            : ResponseScript.Default;
        var responseId = $"resp_{Guid.NewGuid():N}";

        // Tracks this response as "active" for the lifetime of this method, so a concurrent
        // response.cancel (handled by HandleResponseCancelAsync off the same non-blocking receive
        // loop, item 8) has something to signal and a concurrent response.create has something to
        // reject (PR #22 review item N7). Linked to the connection's own ct so a socket-level
        // teardown still cancels this exactly as before; response.cancel additionally trips this
        // same source without affecting ct.
        using var responseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connection.ActiveResponseId = responseId;
        connection.ActiveResponseCancellation = responseCts;

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

        async Task CloseOpenAudioItemAsync(string itemStatus = "completed")
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
                ["status"] = itemStatus,
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

        var cancelled = false;
        try
        {
            foreach (var evt in script.Events)
            {
                // Checked between events (not just inside the paced Task.Delay above) so a
                // response.cancel accepted while processing a non-paced event (e.g. a
                // FunctionCallEvent, or an AudioDeltaEvent with no Pace set) is still observed
                // before the next event runs, rather than only at the next delay point (PR #22
                // review item N7).
                if (responseCts.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

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
                            // responseCts.Token (not ct) so an accepted response.cancel actually
                            // interrupts this wait instead of only being observed at the next event
                            // (PR #22 review item N7).
                            await Task.Delay(pace, connection.TimeProvider, responseCts.Token).ConfigureAwait(false);
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
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Only a response.cancel (which trips responseCts without touching the connection's
            // own ct) is swallowed here — a real socket-level teardown (ct itself cancelled)
            // still propagates normally, same as before this method tracked cancellation.
            cancelled = true;
        }
        finally
        {
            // Whatever happened above, this response is no longer active — a later
            // response.create must be allowed to start a new one, and a later response.cancel
            // for this (now finished) id must be told there's nothing to cancel.
            connection.ActiveResponseId = null;
            connection.ActiveResponseCancellation = null;
        }

        if (cancelled)
        {
            // GA (Response Cancel Event): "the server will respond with a response.done event
            // with a status of response.status=cancelled". Any item still open when the cancel
            // landed is closed as "incomplete" rather than "completed" — it stopped mid-stream,
            // it didn't finish (PR #22 review item N7).
            await CloseOpenAudioItemAsync(itemStatus: "incomplete").ConfigureAwait(false);

            await connection.SendAsync(new JsonObject
            {
                ["type"] = "response.done",
                ["event_id"] = FakeRealtimeConnection.NewEventId(),
                ["response"] = new JsonObject
                {
                    ["id"] = responseId,
                    ["status"] = "cancelled",
                    ["output"] = output.DeepClone(),
                    ["usage"] = BuildUsage(output.Count),
                },
            }, ct).ConfigureAwait(false);
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
