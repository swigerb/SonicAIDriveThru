namespace Conformance.Fakes;

/// <summary>
/// Per-test scripting surface for <see cref="FakeRealtimeUpstreamServer"/>. Tests mutate this
/// before/while a connection is open to control what the fake does when the backend commits an
/// input buffer / creates a response, without needing a new fake per scenario.
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
}

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

public sealed record AudioDeltaEvent(string Base64Delta) : ResponseEvent;

public sealed record FunctionCallEvent(string Name, string ArgumentsJson, string CallId) : ResponseEvent;

public sealed record DoneEvent(string Status = "completed", string? ErrorCode = null, string? ErrorMessage = null) : ResponseEvent;

/// <summary>A raw top-level `error` frame, independent of any response lifecycle.</summary>
public sealed record RawErrorEvent(string Code, string? Param, string Message);
