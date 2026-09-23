using System.Text.Json;

namespace Conformance.Fakes;

/// <summary>
/// Mirrors the Azure OpenAI GA realtime `session.update` validation rules exercised by the
/// Python reference fake in app/backend/tests/test_session_bootstrap.py (FakeGARealtime), so
/// that a backend under test sees the same acceptance/rejection behaviour as the real service.
/// </summary>
public static class GaSessionValidator
{
    /// <summary>
    /// The canonical GA top-level `session` keys, mirroring `_GA_SESSION_TOP_LEVEL` in
    /// app/backend/rtmt.py. The backend's own `_to_ga_session()` translation is expected to
    /// strip anything outside this set before sending — this fake re-checks that stripping as
    /// a regression catcher, exactly like the Python reference fake does.
    /// </summary>
    public static readonly IReadOnlySet<string> GaSessionTopLevelKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "type", "model", "instructions", "tools", "tool_choice", "max_output_tokens",
        "output_modalities", "audio", "tracing", "include", "prompt", "truncation",
        "reasoning", "parallel_tool_calls",
    };

    public static SessionUpdateValidationResult Validate(
        JsonElement sessionUpdateFrame,
        RealtimeSessionState state,
        string deploymentModel)
    {
        if (!sessionUpdateFrame.TryGetProperty("session", out var session) ||
            session.ValueKind != JsonValueKind.Object)
        {
            return SessionUpdateValidationResult.Accepted;
        }

        var unknown = session.EnumerateObject()
            .Select(p => p.Name)
            .Where(name => !GaSessionTopLevelKeys.Contains(name))
            .ToArray();
        if (unknown.Length > 0)
        {
            return SessionUpdateValidationResult.Rejected(
                code: "invalid_request_error",
                param: $"session.{unknown[0]}",
                message: $"Unknown parameter: 'session.{unknown[0]}'.",
                echoEventId: true);
        }

        // gpt-realtime-1.5-style deployments reject `reasoning` (and `parallel_tool_calls`),
        // sometimes without echoing event_id/param — mirrors the Python fake's two rejection
        // shapes so the backend's _SessionUpdateGuard correlation-by-order path is exercised too.
        if (deploymentModel.Contains("1.5", StringComparison.Ordinal) &&
            session.TryGetProperty("reasoning", out _))
        {
            return SessionUpdateValidationResult.Rejected(
                code: "invalid_value",
                param: null,
                message: "Unsupported option for this model: 'session.reasoning'.",
                echoEventId: false);
        }

        if (session.TryGetProperty("audio", out var audio) &&
            audio.ValueKind == JsonValueKind.Object &&
            audio.TryGetProperty("output", out var output) &&
            output.ValueKind == JsonValueKind.Object &&
            output.TryGetProperty("voice", out var voiceProp) &&
            voiceProp.ValueKind == JsonValueKind.String)
        {
            var requestedVoice = voiceProp.GetString();
            if (state.AssistantAudioSeen &&
                !string.IsNullOrEmpty(state.CurrentVoice) &&
                !string.Equals(requestedVoice, state.CurrentVoice, StringComparison.Ordinal))
            {
                return SessionUpdateValidationResult.Rejected(
                    code: "cannot_update_voice",
                    param: "session.audio.output.voice",
                    message: "Cannot update voice after assistant audio has been sent.",
                    echoEventId: true);
            }
        }

        return SessionUpdateValidationResult.Accepted;
    }
}

public sealed record SessionUpdateValidationResult(bool IsAccepted, string? Code, string? Param, string? Message, bool EchoEventId)
{
    public static SessionUpdateValidationResult Accepted { get; } = new(true, null, null, null, true);

    public static SessionUpdateValidationResult Rejected(string code, string? param, string message, bool echoEventId) =>
        new(false, code, param, message, echoEventId);
}

/// <summary>Per-connection state the validator and response scripting need: current voice lock,
/// whether assistant audio has gone out yet, and the last conversation item id (for GA's
/// `previous_item_id` chaining across responses on the same connection).</summary>
public sealed class RealtimeSessionState
{
    public string? CurrentVoice { get; set; }
    public bool AssistantAudioSeen { get; set; }
    public string? LastConversationItemId { get; set; }
}
