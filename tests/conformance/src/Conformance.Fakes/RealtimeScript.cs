using System.Collections.Concurrent;

namespace Conformance.Fakes;

/// <summary>
/// Per-connection scripting surface for <see cref="FakeRealtimeConnection"/>. Tests mutate this
/// before/while a connection is open to control what the fake does when the backend commits an
/// input buffer / creates a response, without needing a new fake per scenario. Lives on the
/// connection (not the server) so each accepted socket gets a fresh, independent script — a
/// previous test's queued responses or custom rules can never leak into the next connection.
///
/// Thread-safety (PR #22 review item N1): a test's own task and the connection's frame-dispatch
/// loop (<see cref="FakeRealtimeUpstreamServer.HandleFrameAsync"/>, running off the non-blocking
/// receive loop per item 8) can legitimately touch this object at the same time — e.g. a test
/// arms a new <see cref="On"/> rule right after sending a frame that's still being dispatched.
/// Before this, <c>Rules</c> was a plain unsynchronized <see cref="List{T}"/> and
/// <c>QueuedResponses</c> a plain unsynchronized <see cref="Queue{T}"/>; a concurrent
/// add/clear during enumeration could throw "Collection was modified" (or silently corrupt
/// FIFO order) with no test ever exercising that path before now.
/// </summary>
public sealed class RealtimeScript
{
    /// <summary>
    /// When true (default), a `response.create` from the backend is answered automatically with
    /// one queued <see cref="ResponseScript"/> (or a default "one short audio delta then done"
    /// script if the queue is empty). Set false to answer manually from the test.
    /// </summary>
    public bool AutoRespond { get; set; } = true;

    /// <summary>Scripts consumed in FIFO order, one per `response.create` seen from the backend.
    /// A <see cref="ConcurrentQueue{T}"/> so a test enqueueing a scripted response from one task
    /// never races <see cref="FakeRealtimeUpstreamServer"/>'s own dequeue in RespondAsync.</summary>
    public ConcurrentQueue<ResponseScript> QueuedResponses { get; } = new();

    public void Enqueue(ResponseScript script) => QueuedResponses.Enqueue(script);

    private readonly Lock _rulesGate = new();
    private readonly List<RealtimeScriptRule> _rules = [];

    /// <summary>
    /// Rule-based triggers: every received frame is checked against every rule in registration
    /// order, and every matching rule's handler runs (fire-and-forget relative to the receive
    /// loop, but serialized against other sends via <see cref="FakeRealtimeConnection.SendAsync"/>'s
    /// send lock). Two VAD-like defaults are pre-registered by <see cref="WithVadDefaults"/> so
    /// most scenarios never need to touch this directly; tests that want different (or no) VAD
    /// behaviour can call <see cref="ClearRules"/> and add their own.
    ///
    /// Reading this property takes a defensive snapshot under <see cref="_rulesGate"/> — the same
    /// lock <see cref="On"/> and <see cref="ClearRules"/> mutate under — so
    /// <c>FakeRealtimeUpstreamServer.HandleFrameAsync</c>'s <c>foreach (var rule in
    /// connection.Script.Rules)</c> always enumerates a stable copy, never the live list, no
    /// matter what else is mutating it concurrently.
    /// </summary>
    public IReadOnlyList<RealtimeScriptRule> Rules
    {
        get
        {
            lock (_rulesGate)
            {
                return _rules.ToArray();
            }
        }
    }

    public void On(Func<RecordedFrame, bool> predicate, Func<FakeRealtimeConnection, RecordedFrame, CancellationToken, Task> handler)
    {
        lock (_rulesGate)
        {
            _rules.Add(new RealtimeScriptRule(predicate, handler));
        }
    }

    /// <summary>Removes every currently-registered rule (e.g. to opt out of the VAD-like
    /// defaults). Thread-safe, same as <see cref="On"/> — replaces the old
    /// <c>Rules.Clear()</c> call pattern now that <see cref="Rules"/> returns a read-only
    /// snapshot rather than the live, mutable list.</summary>
    public void ClearRules()
    {
        lock (_rulesGate)
        {
            _rules.Clear();
        }
    }

    /// <summary>
    /// A fresh <see cref="RealtimeScript"/> with the two VAD-like default rules PR #22 review
    /// item 8 asks for, so a test driving a turn only has to send one
    /// `input_audio_buffer.append` (not simulate real silence-detection timing) and one
    /// `conversation.item.create` to get GA's usual acknowledgement frames back automatically.
    /// </summary>
    public static RealtimeScript WithVadDefaults()
    {
        var script = new RealtimeScript();

        // A real server-VAD turn is speech_started -> (silence) -> speech_stopped ->
        // committed -> (if input_audio_transcription is configured) transcription.completed.
        // Emulating real silence-duration timing would make every scenario that touches audio
        // input slow and non-deterministic for no black-box benefit, so one append triggers the
        // whole acknowledgement sequence immediately -- "VAD-like", not VAD-faithful.
        script.On(
            frame => frame.Type == "input_audio_buffer.append",
            async (connection, frame, ct) =>
            {
                var itemId = $"item_{Guid.NewGuid():N}";
                await connection.SendAsync(new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "input_audio_buffer.speech_started",
                    ["event_id"] = FakeRealtimeConnection.NewEventId(),
                    ["item_id"] = itemId,
                }, ct).ConfigureAwait(false);
                await connection.SendAsync(new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "input_audio_buffer.speech_stopped",
                    ["event_id"] = FakeRealtimeConnection.NewEventId(),
                    ["item_id"] = itemId,
                }, ct).ConfigureAwait(false);
                await connection.SendAsync(new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "input_audio_buffer.committed",
                    ["event_id"] = FakeRealtimeConnection.NewEventId(),
                    ["item_id"] = itemId,
                }, ct).ConfigureAwait(false);
                await connection.SendAsync(new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "conversation.item.input_audio_transcription.completed",
                    ["event_id"] = FakeRealtimeConnection.NewEventId(),
                    ["item_id"] = itemId,
                    ["transcript"] = "",
                }, ct).ConfigureAwait(false);
            });

        // GA acknowledges every client-created conversation item (e.g. the
        // function_call_output the backend sends after running a tool) with
        // conversation.item.added -- the fake previously just recorded these and never replied.
        script.On(
            frame => frame.Type == "conversation.item.create",
            async (connection, frame, ct) =>
            {
                if (!frame.Json.TryGetProperty("item", out var item))
                {
                    return;
                }
                var itemId = item.TryGetProperty("id", out var idProp) && idProp.ValueKind == System.Text.Json.JsonValueKind.String
                    ? idProp.GetString()
                    : null;

                // GA rejects a repeated item id within the same conversation with this exact
                // error shape (live-verified against gpt-realtime-2.1 / gpt-realtime-2.1-dz for
                // PR #30 review "G1") instead of the usual .added/.done acknowledgement below.
                if (itemId is not null && !connection.SessionState.SeenConversationItemIds.Add(itemId))
                {
                    await connection.SendAsync(new System.Text.Json.Nodes.JsonObject
                    {
                        ["type"] = "invalid_request_error",
                        ["code"] = "item_create_duplicate_item_id",
                        ["message"] = $"Error adding item: an item with id '{itemId}' already exists.",
                        ["param"] = null,
                        ["event_id"] = null,
                    }, ct).ConfigureAwait(false);
                    return;
                }

                // Both acknowledgement frames describe the same item's position in the
                // conversation, so both must carry the *same* previous_item_id -- captured once,
                // before LastConversationItemId advances to this item below (PR #30 review "G1"
                // part 2: previously LastConversationItemId was never updated for a client-created
                // item at all, so a subsequent server-generated item's own previous_item_id could
                // point at a stale predecessor instead of the client item that actually came last).
                var previousItemId = connection.SessionState.LastConversationItemId;
                var itemNode = System.Text.Json.Nodes.JsonNode.Parse(item.GetRawText());
                if (itemId is not null && itemNode is System.Text.Json.Nodes.JsonObject itemObject)
                {
                    // #28 N18: mirror the item content so a later conversation.item.retrieve has
                    // something real to answer with.
                    connection.SessionState.ConversationItemsById[itemId] = itemObject;
                }
                await connection.SendAsync(new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "conversation.item.added",
                    ["event_id"] = FakeRealtimeConnection.NewEventId(),
                    ["previous_item_id"] = previousItemId,
                    ["item"] = itemNode?.DeepClone(),
                }, ct).ConfigureAwait(false);
                // GA also emits conversation.item.done "when the item is finalized" -- a second,
                // separate event carrying the full item again. Rick's PR #30 review ("M1"/"M2")
                // found the backend had no filtering for this event at all, and that the fake's
                // silence on it was exactly why the existing conformance suite never caught the
                // leak: nothing exercised the wire behaviour .created/.added alone can't prove.
                await connection.SendAsync(new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "conversation.item.done",
                    ["event_id"] = FakeRealtimeConnection.NewEventId(),
                    ["previous_item_id"] = previousItemId,
                    ["item"] = itemNode?.DeepClone(),
                }, ct).ConfigureAwait(false);
                if (itemId is not null)
                {
                    connection.SessionState.LastConversationItemId = itemId;
                }
            });

        return script;
    }
}

/// <summary>One rule: fires <see cref="Handler"/> for every received frame matching <see cref="Predicate"/>.</summary>
public sealed record RealtimeScriptRule(
    Func<RecordedFrame, bool> Predicate,
    Func<FakeRealtimeConnection, RecordedFrame, CancellationToken, Task> Handler);

/// <summary>An ordered set of response.* events the fake should emit for one response.create.</summary>
public sealed record ResponseScript(IReadOnlyList<ResponseEvent> Events)
{
    public static ResponseScript Default { get; } = new([
        new AudioDeltaEvent("dGVzdC1hdWRpby1kZWx0YQ=="),
        new DoneEvent(),
    ]);

    // #28 N12: error.type values are not independently live-verified (GA's own reference only
    // documents error.type as freeform "string", with the top-level RealtimeError.type property's
    // examples -- "invalid_request_error", "server_error" -- as the closest guidance on the
    // convention). "invalid_request_error" for the rate-limit case follows the standard OpenAI
    // REST error taxonomy (a 429 is a client-side request problem, not a server fault); kept for
    // behavioural parity/best-available, same caveat as GaSessionValidator's other unverified
    // specifics.
    public static ResponseScript RateLimited(string hint = "Rate limit reached. Please try again in 2s.") => new([
        new DoneEvent(Status: "failed", ErrorCode: "rate_limit_exceeded", ErrorMessage: hint, ErrorType: "invalid_request_error"),
    ]);

    public static ResponseScript Failed(string message = "The model failed to generate a response.") => new([
        new DoneEvent(Status: "failed", ErrorCode: "server_error", ErrorMessage: message, ErrorType: "server_error"),
    ]);
}

public abstract record ResponseEvent;

/// <summary>
/// One `response.output_audio.delta`. <paramref name="Pace"/>, when set, is awaited (via the
/// connection's <see cref="TimeProvider"/> so tests can substitute a fake one) immediately before
/// this delta is sent — lets a scenario script a slow-arriving response stream to exercise
/// barge-in (a `response.cancel`/new `input_audio_buffer.append` arriving mid-stream) without a
/// real-time sleep baked into the fake itself.
/// </summary>
public sealed record AudioDeltaEvent(string Base64Delta, TimeSpan? Pace = null) : ResponseEvent;

public sealed record FunctionCallEvent(string Name, string ArgumentsJson, string CallId) : ResponseEvent;

/// <summary>
/// <paramref name="ErrorMessage"/> is a superset addition beyond GA's documented
/// `status_details.error` shape -- the reference schema (fetched 2026-09-24, same page cited by
/// <see cref="GaSessionValidator"/>) enumerates exactly two properties on that object, `code` and
/// `type` ("Error code, if any." / "The type of error."), no `message`. Sent anyway (#28 N12) as
/// a harmless extra field a real client would just ignore, since an existing scenario
/// (<c>Scripted_response_done_can_report_a_rate_limited_failure_with_a_hint</c>, issue #7) already
/// asserts on it for a human-readable retry hint. <paramref name="ErrorType"/> is the field GA
/// actually documents but the fake never sent until now -- see <see cref="ResponseScript"/>'s
/// factory methods for what's fabricated versus GA-shaped.
/// </summary>
public sealed record DoneEvent(string Status = "completed", string? ErrorCode = null, string? ErrorMessage = null, string? ErrorType = null) : ResponseEvent;
