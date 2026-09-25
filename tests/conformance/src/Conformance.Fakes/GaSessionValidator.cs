using System.Text.Json;
using System.Text.Json.Nodes;

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
    /// #28 N11: keys accepted under `session.audio.input.transcription` (the `AudioTranscription`
    /// object) -- previously unchecked, so a translator that forwarded a legacy/renamed nested key
    /// (e.g. a pre-GA field name) here would pass the fake but be rejected by the real service.
    ///
    /// #28 F2 (PR #52 review): re-verified against the OpenAI Realtime API reference --
    /// https://developers.openai.com/api/reference/resources/realtime (section "Audio
    /// Transcription", fetched 2026-09-25) -- which currently documents the `AudioTranscription`
    /// object as `{ delay, keywords, language, languages, model, prompt }` (6 keys). `languages`
    /// ("Possible languages of the input audio... Supported by `gpt-transcribe` and
    /// `gpt-live-transcribe`") is a real, currently-documented GA key alongside the singular
    /// `language` -- kept, not removed. The same re-check also found the set here was missing
    /// two other now-documented keys, `delay` and `keywords`, which are added below for the same
    /// reason N11 exists: an unchecked/incomplete nested-key set lets a translator regression
    /// slip past the fake. Azure's realtime reference --
    /// https://learn.microsoft.com/en-us/azure/foundry/openai/realtime-audio-reference (fetched
    /// 2026-09-25) -- confirms it "follows the OpenAI Realtime API specification" here, with its
    /// only documented deviation being the accepted *value* format for `model` (a deployment
    /// name), not the key set.
    /// </summary>
    public static readonly IReadOnlySet<string> AudioInputTranscriptionKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "delay", "keywords", "language", "languages", "model", "prompt",
    };

    /// <summary>
    /// #28 N11: keys accepted under `session.audio.input.turn_detection` -- previously unchecked.
    /// GA's `turn_detection` is a discriminated union on `type` (`ServerVad` vs `SemanticVad`);
    /// this is the union of both variants' keys, since the discriminator itself
    /// (`turn_detection.type`) isn't validated as a separate concern here — an unknown key is an
    /// unknown key regardless of which variant the caller meant. `ServerVad`:
    /// `{ type, create_response, idle_timeout_ms, interrupt_response, prefix_padding_ms,
    /// silence_duration_ms, threshold }` (7 keys, confirmed by enumerating the reference's
    /// "4 more" past the first three named in its preview). `SemanticVad`:
    /// `{ type, create_response, eagerness, interrupt_response }` (4 keys) — `eagerness` is the
    /// only key not already covered by `ServerVad`.
    /// </summary>
    public static readonly IReadOnlySet<string> AudioInputTurnDetectionKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "type", "create_response", "idle_timeout_ms", "interrupt_response",
        "prefix_padding_ms", "silence_duration_ms", "threshold", "eagerness",
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

                // #28 N11: nested keys under audio.input.transcription / audio.input.turn_detection
                // were never validated -- only that the parent object itself was named correctly.
                if (input.TryGetProperty("transcription", out var transcription) &&
                    transcription.ValueKind == JsonValueKind.Object)
                {
                    var badTranscriptionKey = FirstUnknownKey(transcription, AudioInputTranscriptionKeys);
                    if (badTranscriptionKey is not null)
                    {
                        return SessionUpdateValidationResult.Rejected(
                            code: "unknown_parameter",
                            param: $"session.audio.input.transcription.{badTranscriptionKey}",
                            message: $"Unknown parameter: 'session.audio.input.transcription.{badTranscriptionKey}'.",
                            echoEventId: true);
                    }
                }

                if (input.TryGetProperty("turn_detection", out var turnDetection) &&
                    turnDetection.ValueKind == JsonValueKind.Object)
                {
                    var badTurnDetectionKey = FirstUnknownKey(turnDetection, AudioInputTurnDetectionKeys);
                    if (badTurnDetectionKey is not null)
                    {
                        return SessionUpdateValidationResult.Rejected(
                            code: "unknown_parameter",
                            param: $"session.audio.input.turn_detection.{badTurnDetectionKey}",
                            message: $"Unknown parameter: 'session.audio.input.turn_detection.{badTurnDetectionKey}'.",
                            echoEventId: true);
                    }
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

    /// <summary>#28 N18: the fake's own copy of every conversation item it has ever sent, keyed
    /// by item id, so `conversation.item.retrieve` has something real to answer with. Populated
    /// wherever an item is created (client-supplied via `conversation.item.create`, or
    /// fake-generated audio/function-call items), and overwritten with the finalized version once
    /// an in-progress item completes -- a scenario retrieving an item mid-response back gets
    /// whatever content had actually been sent by then, not the eventual final content. This is
    /// deliberately a plain content mirror, not a source of truth for id-uniqueness or ordering:
    /// #30 owns duplicate-id rejection (<see cref="SeenConversationItemIds"/>) and
    /// `previous_item_id` tracking (<see cref="LastConversationItemId"/>).</summary>
    public Dictionary<string, JsonObject> ConversationItemsById { get; } = new(StringComparer.Ordinal);

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
