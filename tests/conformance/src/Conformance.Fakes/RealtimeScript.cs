namespace Conformance.Fakes;

/// <summary>
/// Per-connection scripting surface for <see cref="FakeRealtimeConnection"/>. Tests mutate this
/// before/while a connection is open to control what the fake does when the backend commits an
/// input buffer / creates a response, without needing a new fake per scenario. Lives on the
/// connection (not the server) so each accepted socket gets a fresh, independent script — a
/// previous test's queued responses or custom rules can never leak into the next connection.
/// </summary>
public sealed class RealtimeScript
{
    /// <summary>
    /// When true (default), a `response.create` from the backend is answered automatically with
    /// one queued <see cref="ResponseScript"/> (or a default "one short audio delta then done"
    /// script if the queue is empty). Set false to answer manually from the test.
    /// </summary>
    public bool AutoRespond { get; set; } = true;

    /// <summary>Scripts consumed in FIFO order, one per `response.create` seen from the backend.</summary>
    public Queue<ResponseScript> QueuedResponses { get; } = new();

    public void Enqueue(ResponseScript script) => QueuedResponses.Enqueue(script);

    /// <summary>
    /// Rule-based triggers: every received frame is checked against every rule in registration
    /// order, and every matching rule's handler runs (fire-and-forget relative to the receive
    /// loop, but serialized against other sends via <see cref="FakeRealtimeConnection.SendAsync"/>'s
    /// send lock). Two VAD-like defaults are pre-registered by <see cref="WithVadDefaults"/> so
    /// most scenarios never need to touch this directly; tests that want different (or no) VAD
    /// behaviour can call <c>Rules.Clear()</c> and add their own.
    /// </summary>
    public List<RealtimeScriptRule> Rules { get; } = [];

    public void On(Func<RecordedFrame, bool> predicate, Func<FakeRealtimeConnection, RecordedFrame, CancellationToken, Task> handler) =>
        Rules.Add(new RealtimeScriptRule(predicate, handler));

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
                await connection.SendAsync(new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "conversation.item.added",
                    ["event_id"] = FakeRealtimeConnection.NewEventId(),
                    ["previous_item_id"] = connection.SessionState.LastConversationItemId,
                    ["item"] = System.Text.Json.Nodes.JsonNode.Parse(item.GetRawText()),
                }, ct).ConfigureAwait(false);
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

    public static ResponseScript RateLimited(string hint = "Rate limit reached. Please try again in 2s.") => new([
        new DoneEvent(Status: "failed", ErrorCode: "rate_limit_exceeded", ErrorMessage: hint),
    ]);

    public static ResponseScript Failed(string message = "The model failed to generate a response.") => new([
        new DoneEvent(Status: "failed", ErrorCode: "server_error", ErrorMessage: message),
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

public sealed record DoneEvent(string Status = "completed", string? ErrorCode = null, string? ErrorMessage = null) : ResponseEvent;
