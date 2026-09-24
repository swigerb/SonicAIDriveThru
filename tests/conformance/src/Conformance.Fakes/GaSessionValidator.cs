using System.Text.Json;

namespace Conformance.Fakes;

/// <summary>
/// Mirrors the Azure OpenAI / OpenAI GA realtime `session.update` validation surface.
///
/// PR #22 review item 9 required this to be re-derived from the official spec, not from
/// `app/backend/rtmt.py` (the two are cross-checked for consistency below, but rtmt.py is not
/// the source). Primary source: the OpenAI Realtime API reference —
/// https://developers.openai.com/api/reference/resources/realtime (sections
/// "Realtime Session Create Request", "Session Update Event", "Realtime Client Event",
/// "Realtime Error" / "Realtime Error Event" — fetched 2026-09-24). Azure's own realtime
/// reference defers to this spec verbatim: "The Azure OpenAI Realtime API follows the OpenAI
/// Realtime API specification" —
/// https://learn.microsoft.com/en-us/azure/foundry/openai/realtime-audio-reference
/// (fetched 2026-09-24), with one documented Azure-specific deviation
/// (`audio.input.transcription.model` must be a deployment name) that doesn't affect the
/// checks below.
///
/// Four of the error shapes were additionally confirmed with a single authorized live probe
/// against the real service (Stage B item 9; subscription
/// 44847a42-6b69-4e6c-b7e5-ce7140469dd6, wss://cog-axgpampkq3yfa.openai.azure.com/openai/v1/
/// realtime?model=gpt-realtime-2.1, 2026-09-24, bearer via
/// `az account get-access-token --resource https://cognitiveservices.azure.com`; no secrets
/// recorded). Exact recorded JSON for all four is in tests/conformance/README.md under
/// "GA validation fidelity — live-probe evidence":
///   1. Unknown top-level session key -> `error.code = "unknown_parameter"`,
///      `error.param = "session.&lt;key&gt;"`, event_id echoed.
///   2. Missing `session.type` -> `error.code = "missing_required_parameter"`,
///      `error.param = "session.type"`, event_id echoed.
///   3. Unknown top-level client event `type` -> `error.code = "invalid_value"`,
///      `error.param = "type"`, message enumerates the exact supported values.
///   4. Missing Authorization header -> HTTP 401 at the WebSocket handshake (no JSON error
///      frame — the upgrade itself is refused).
/// NOT independently live-verified (documented honestly rather than guessed, per review):
/// the exact error code for `cannot_update_voice` and for `reasoning` rejected on a "1.5"
/// deployment. Both require session/model state the probe couldn't reach in one shot (an
/// established voice from prior assistant audio; a distinct "1.5"-named deployment we don't
/// have access to) — these two remain sourced from behavioural parity with the Python
/// reference fake (FakeGARealtime in app/backend/tests/test_session_bootstrap.py), which this
/// harness treats as "best available", not "confirmed live".
/// </summary>
public static class GaSessionValidator
{
    /// <summary>
    /// Top-level `session` keys accepted by `RealtimeSessionCreateRequest` (14 fields, listed
    /// as "{ type, audio, include, 11 more }" on the reference doc page and confirmed by
    /// enumerating all 14). Independently matches `_GA_SESSION_TOP_LEVEL` in rtmt.py — that's
    /// cross-validation, not the source.
    /// </summary>
    public static readonly IReadOnlySet<string> GaSessionTopLevelKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "type", "model", "instructions", "tools", "tool_choice", "max_output_tokens",
        "output_modalities", "audio", "tracing", "include", "prompt", "truncation",
        "reasoning", "parallel_tool_calls",
    };

    /// <summary>Keys accepted under `session.audio.input` (RealtimeAudioConfigInput).</summary>
    public static readonly IReadOnlySet<string> AudioInputKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "format", "noise_reduction", "transcription", "turn_detection",
    };

    /// <summary>Keys accepted under `session.audio.output` (RealtimeAudioConfigOutput).</summary>
    public static readonly IReadOnlySet<string> AudioOutputKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "format", "speed", "voice",
    };

    /// <summary>
    /// Valid top-level client event `type` values. Primary source is the live-probe rejection
    /// message (ground truth: it's literally the real server's own "Supported values" list for
    /// this exact check): session.update, transcription_session.update, session.close,
    /// input_audio_buffer.append, input_audio_buffer.commit, input_audio_buffer.clear,
    /// conversation.item.create, conversation.item.truncate, conversation.item.delete,
    /// conversation.item.retrieve, response.create, response.cancel. `output_audio_buffer.clear`
    /// is additionally documented on the reference page's RealtimeClientEvent union but was
    /// *not* enumerated in the live rejection message — kept in the allow-list anyway since the
    /// doc explicitly describes it and a single negative-message omission is weak evidence
    /// against it (see README for the exact wording and this discrepancy).
    /// </summary>
    public static readonly IReadOnlySet<string> GaClientEventTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "session.update", "transcription_session.update", "session.close",
        "input_audio_buffer.append", "input_audio_buffer.commit", "input_audio_buffer.clear",
        "conversation.item.create", "conversation.item.truncate", "conversation.item.delete",
        "conversation.item.retrieve", "response.create", "response.cancel",
        "output_audio_buffer.clear",
    };

    /// <summary>
    /// Validates a top-level client event `type` against <see cref="GaClientEventTypes"/> —
    /// catches leaked internal frame types (e.g. a stray `extension.*` frame from the browser
    /// contract accidentally forwarded upstream unchanged) exactly like the real GA endpoint's
    /// own "Invalid value" rejection for an unrecognised type.
    /// </summary>
    public static SessionUpdateValidationResult ValidateClientEventType(string? type)
    {
        if (type is not null && GaClientEventTypes.Contains(type))
        {
            return SessionUpdateValidationResult.Accepted;
        }

        return SessionUpdateValidationResult.Rejected(
            code: "invalid_value",
            param: "type",
            message: $"Invalid value: '{type}'. Supported values are: " +
                string.Join(", ", GaClientEventTypes.Select(t => $"'{t}'")) + ".",
            echoEventId: true);
    }

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
                code: "unknown_parameter",
                param: $"session.{unknown[0]}",
                message: $"Unknown parameter: 'session.{unknown[0]}'.",
                echoEventId: true);
        }

        // gpt-realtime-1.5-style deployments reject `reasoning` (and `parallel_tool_calls`),
        // sometimes without echoing event_id/param — mirrors the Python fake's two rejection
        // shapes so the backend's _SessionUpdateGuard correlation-by-order path is exercised too.
        // NOT independently live-verified (see class doc) — kept for behavioural parity only.
        if (deploymentModel.Contains("1.5", StringComparison.Ordinal) &&
            session.TryGetProperty("reasoning", out _))
        {
            return SessionUpdateValidationResult.Rejected(
                code: "invalid_value",
                param: null,
                message: "Unsupported option for this model: 'session.reasoning'.",
                echoEventId: false);
        }

        if (session.TryGetProperty("audio", out var audio) && audio.ValueKind == JsonValueKind.Object)
        {
            if (audio.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
            {
                var badInputKey = FirstUnknownKey(input, AudioInputKeys);
                if (badInputKey is not null)
                {
                    return SessionUpdateValidationResult.Rejected(
                        code: "unknown_parameter",
                        param: $"session.audio.input.{badInputKey}",
                        message: $"Unknown parameter: 'session.audio.input.{badInputKey}'.",
                        echoEventId: true);
                }
            }

            if (audio.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object)
            {
                var badOutputKey = FirstUnknownKey(output, AudioOutputKeys);
                if (badOutputKey is not null)
                {
                    return SessionUpdateValidationResult.Rejected(
                        code: "unknown_parameter",
                        param: $"session.audio.output.{badOutputKey}",
                        message: $"Unknown parameter: 'session.audio.output.{badOutputKey}'.",
                        echoEventId: true);
                }

                if (output.TryGetProperty("voice", out var voiceProp) && voiceProp.ValueKind == JsonValueKind.String)
                {
                    var requestedVoice = voiceProp.GetString();
                    if (state.AssistantAudioSeen &&
                        !string.IsNullOrEmpty(state.CurrentVoice) &&
                        !string.Equals(requestedVoice, state.CurrentVoice, StringComparison.Ordinal))
                    {
                        // NOT independently live-verified (see class doc) — kept for
                        // behavioural parity with the Python reference fake only.
                        return SessionUpdateValidationResult.Rejected(
                            code: "cannot_update_voice",
                            param: "session.audio.output.voice",
                            message: "Cannot update voice after assistant audio has been sent.",
                            echoEventId: true);
                    }
                }
            }
        }

        // GA requires the `type` discriminator on every session.update's `session` object
        // (SessionUpdateEvent.session: RealtimeSessionCreateRequest, whose `type` field is
        // non-optional — literal "realtime"). Checked last so the more specific errors above
        // (unknown key, reasoning-on-1.5, cannot_update_voice) win when a payload trips more
        // than one rule at once, matching the live-probe evidence that each of those fires
        // independently of whether `type` happens to be present.
        if (!session.TryGetProperty("type", out var typeProp) ||
            typeProp.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(typeProp.GetString()))
        {
            return SessionUpdateValidationResult.Rejected(
                code: "missing_required_parameter",
                param: "session.type",
                message: "Missing required parameter: 'session.type'.",
                echoEventId: true);
        }

        return SessionUpdateValidationResult.Accepted;
    }

    private static string? FirstUnknownKey(JsonElement obj, IReadOnlySet<string> allowed) =>
        obj.EnumerateObject().Select(p => p.Name).FirstOrDefault(name => !allowed.Contains(name));
}

public sealed record SessionUpdateValidationResult(bool IsAccepted, string? Code, string? Param, string? Message, bool EchoEventId)
{
    public static SessionUpdateValidationResult Accepted { get; } = new(true, null, null, null, true);

    public static SessionUpdateValidationResult Rejected(string code, string? param, string message, bool echoEventId) =>
        new(false, code, param, message, echoEventId);
}

/// <summary>Per-connection state the validator and response scripting need: current voice lock,
/// whether assistant audio has gone out yet, the last conversation item id (for GA's
/// `previous_item_id` chaining across responses on the same connection), and the accumulated
/// effective session (PR #22 review item 9: `session.updated` must echo the full merged session,
/// not a stub, since GA's `session.update` is a partial patch — each accepted update merges its
/// keys into whatever was already in effect, it doesn't replace the whole session).</summary>
public sealed class RealtimeSessionState
{
    public string? CurrentVoice { get; set; }
    public bool AssistantAudioSeen { get; set; }
    public string? LastConversationItemId { get; set; }

    /// <summary>Every conversation item id this connection has ever created, client-supplied
    /// ids included. GA rejects a `conversation.item.create` that reuses an id already present
    /// in the conversation with `item_create_duplicate_item_id` (live-verified against
    /// gpt-realtime-2.1 / gpt-realtime-2.1-dz for PR #30 review "G1") -- this set is what lets
    /// <see cref="RealtimeScript.WithVadDefaults"/> reproduce that rejection.</summary>
    public HashSet<string> SeenConversationItemIds { get; } = new(StringComparer.Ordinal);

    /// <summary>The full session as GA would report it in `session.updated`, accumulated across
    /// every accepted `session.update` on this connection. Top-level keys from each update
    /// overwrite the corresponding key here; `audio.input`/`audio.output` are merged one level
    /// deeper (so setting `audio.output.voice` alone doesn't drop a previously-set
    /// `audio.input.format`), matching GA's documented partial-update semantics.</summary>
    public System.Text.Json.Nodes.JsonObject EffectiveSession { get; } = new();

    /// <summary>Merges an accepted `session.update`'s `session` object into
    /// <see cref="EffectiveSession"/> using GA's partial-patch semantics.</summary>
    public void MergeSessionUpdate(JsonElement session)
    {
        foreach (var prop in session.EnumerateObject())
        {
            if (prop.NameEquals("audio") && prop.Value.ValueKind == JsonValueKind.Object)
            {
                var audio = EffectiveSession.TryGetPropertyValue("audio", out var existingAudioNode) &&
                    existingAudioNode is System.Text.Json.Nodes.JsonObject existingAudio
                    ? existingAudio
                    : new System.Text.Json.Nodes.JsonObject();
                EffectiveSession["audio"] = audio;

                foreach (var audioProp in prop.Value.EnumerateObject())
                {
                    if ((audioProp.NameEquals("input") || audioProp.NameEquals("output")) &&
                        audioProp.Value.ValueKind == JsonValueKind.Object)
                    {
                        var side = audio.TryGetPropertyValue(audioProp.Name, out var existingSideNode) &&
                            existingSideNode is System.Text.Json.Nodes.JsonObject existingSide
                            ? existingSide
                            : new System.Text.Json.Nodes.JsonObject();
                        audio[audioProp.Name] = side;

                        foreach (var sideProp in audioProp.Value.EnumerateObject())
                        {
                            side[sideProp.Name] = System.Text.Json.Nodes.JsonNode.Parse(sideProp.Value.GetRawText());
                        }
                    }
                    else
                    {
                        audio[audioProp.Name] = System.Text.Json.Nodes.JsonNode.Parse(audioProp.Value.GetRawText());
                    }
                }
            }
            else
            {
                EffectiveSession[prop.Name] = System.Text.Json.Nodes.JsonNode.Parse(prop.Value.GetRawText());
            }
        }
    }
}
