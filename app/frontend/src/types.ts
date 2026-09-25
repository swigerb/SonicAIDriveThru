// Represents a grounding file
export type GroundingFile = {
    id: string;
    name: string;
    content: string;
};

// Represents an item in the history
export type HistoryItem = {
    id: string;
    transcript: string;
    groundingFiles?: GroundingFile[];
    sender: "user" | "assistant";
    timestamp: Date; // Add timestamp field
};

// Represents a command to update the session
export type SessionUpdateCommand = {
    type: "session.update";
    session: {
        turn_detection?: {
            type: "server_vad" | "none";
            threshold?: number;
            prefix_padding_ms?: number;
            silence_duration_ms?: number;
        };
        input_audio_transcription?: {
            model: "whisper-1";
        };
    };
};

// Represents a command to append audio to the input buffer
export type InputAudioBufferAppendCommand = {
    type: "input_audio_buffer.append";
    audio: string; // Ensure this is a valid base64-encoded string
};

// Represents a command to clear the input audio buffer
export type InputAudioBufferClearCommand = {
    type: "input_audio_buffer.clear";
};

// Represents a generic message
export type Message = {
    type: string;
};

// Represents a response containing an audio delta
export type ResponseAudioDelta = {
    type: "response.audio.delta";
    delta: string; // Ensure this is a valid base64-encoded string
};

// Represents a response containing an audio transcript delta
export type ResponseAudioTranscriptDelta = {
    type: "response.audio_transcript.delta";
    delta: string;
};

// Represents a response indicating that input audio transcription is completed
export type ResponseInputAudioTranscriptionCompleted = {
    type: "conversation.item.input_audio_transcription.completed";
    event_id: string;
    item_id: string;
    content_index: number;
    transcript: string;
};

// Represents a response indicating that the response is done
export type ResponseDone = {
    type: "response.done";
    event_id: string;
    response: {
        id: string;
        output: { id: string; content?: { transcript: string; type: string }[] }[];
    };
};

// Represents a response from an extension middle tier tool
export type ExtensionMiddleTierToolResponse = {
    type: "extension.middle_tier_tool_response";
    previous_item_id: string;
    tool_name: string;
    tool_result: string; // JSON string that needs to be parsed into ToolResult
};

export type ExtensionSessionMetadata = {
    type: "extension.session_metadata";
    sessionToken: string;
    roundTripIndex: number;
    roundTripToken: string;
    /** Single-use resume credential for this tab; absent when resume is disabled server-side. */
    resumeId?: string;
};

// Same shape the ticket renders from JSON.parse(tool_result).
export type OrderSummaryWire = {
    items: { item: string; size: string; quantity: number; price: number; display: string }[];
    total: number;
    tax: number;
    finalTotal: number;
    // #47/PR #50 follow-up: additive, backend-rounded display strings (money_utils.format_money,
    // ROUND_HALF_UP) mirroring OrderSummaryProps' *Display fields in order-summary.tsx. Optional
    // because they're additive on the wire -- older payloads/tests that only set the four numeric
    // fields above remain valid -- but present on every real backend response (including
    // extension.session_resumed's order_summary) so the resumed ticket keeps reading the same
    // single source of truth as a fresh order instead of falling back to a client-side re-round.
    totalDisplay?: string;
    taxDisplay?: string;
    finalTotalDisplay?: string;
};

// Reply to extension.resume: the dropped session (and its order) is back.
export type ExtensionSessionResumed = {
    type: "extension.session_resumed";
    order_summary: OrderSummaryWire;
    session_token: string;
    round_trip_index: number;
    round_trip_token: string;
    /** Rotated credential; the one just presented is spent. */
    resume_id: string;
};

export type ResumeRejectReason = "unknown" | "expired" | "malformed" | "disabled" | "not_first_frame";

// Reply to extension.resume: a fresh session follows (extension.session_metadata).
export type ExtensionResumeRejected = {
    type: "extension.resume_rejected";
    reason: ResumeRejectReason | string;
};

export type ExtensionRoundTripToken = {
    type: "extension.round_trip_token";
    sessionToken: string;
    roundTripIndex: number;
    roundTripToken: string;
};

// A model response was rate-limited. attempt 1: the server's silent retry failed
// too and a second retry is coming (play the apology clip). final: that failed as
// well; the guest is asked to say it again. See docs/rate_limit_recovery.md.
export type ExtensionRateLimited = {
    type: "extension.rate_limited";
    attempt: number;
    final?: boolean;
};
