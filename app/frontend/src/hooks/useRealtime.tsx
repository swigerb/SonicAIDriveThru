import useWebSocket, { ReadyState } from "react-use-websocket";
import { useRef, useCallback, useEffect, useState } from "react";

import {
    InputAudioBufferAppendCommand,
    InputAudioBufferClearCommand,
    Message,
    ResponseAudioDelta,
    ResponseAudioTranscriptDelta,
    ResponseDone,
    SessionUpdateCommand,
    ExtensionMiddleTierToolResponse,
    ResponseInputAudioTranscriptionCompleted,
    ExtensionSessionMetadata,
    ExtensionRoundTripToken,
    ExtensionSessionResumed,
    ExtensionResumeRejected
} from "@/types";

type Parameters = {
    useDirectAoaiApi?: boolean; // If true, the middle tier will be skipped and the AOAI ws API will be called directly
    aoaiEndpointOverride?: string;
    aoaiApiKeyOverride?: string;
    aoaiModelOverride?: string;

    enableInputAudioTranscription?: boolean;
    onWebSocketOpen?: () => void;
    onWebSocketClose?: () => void;
    /** Fired whenever an open socket closes. The server session (and its order) is gone. */
    onConnectionLost?: (info: ConnectionLostInfo) => void;
    onWebSocketError?: (event: Event) => void;
    onWebSocketMessage?: (event: MessageEvent<any>) => void;

    onReceivedResponseCreated?: (message: Message) => void;
    onReceivedResponseAudioDelta?: (message: ResponseAudioDelta) => void;
    onReceivedInputAudioBufferSpeechStarted?: (message: Message) => void;
    onReceivedResponseDone?: (message: ResponseDone) => void;
    onReceivedExtensionMiddleTierToolResponse?: (message: ExtensionMiddleTierToolResponse) => void;
    onReceivedSessionMetadata?: (message: ExtensionSessionMetadata) => void;
    onReceivedSessionResumed?: (message: ExtensionSessionResumed) => void;
    onReceivedResumeRejected?: (message: ExtensionResumeRejected) => void;
    /** Background reconnect gave up (retries exhausted); the socket stays down until reconnect(). */
    onReconnectGaveUp?: () => void;
    onReceivedRoundTripToken?: (message: ExtensionRoundTripToken) => void;
    onReceivedResponseAudioTranscriptDelta?: (message: ResponseAudioTranscriptDelta) => void;
    onReceivedInputAudioTranscriptionCompleted?: (message: ResponseInputAudioTranscriptionCompleted) => void;
    onReceivedError?: (message: Message) => void;
};

// Server closes idle sessions with this code (session_manager.IDLE_CLOSE_CODE).
// It is intentional, so the hook stays disconnected until the guest taps again
// instead of silently opening a new socket that mic audio could leak into.
// The session is already gone server-side: idle is never resumable.
export const WS_CLOSE_IDLE_TIMEOUT = 4000;
// Another socket resumed this session (session_manager.SUPERSEDED_CLOSE_CODE).
export const WS_CLOSE_SUPERSEDED = 4002;
// Reply to extension.end_session: 1000 with this reason.
export const WS_CLOSE_SESSION_ENDED_REASON = "session_ended";

// Per-tab resume credential (docs/order_resume.md). Never put it in a URL.
export const RESUME_STORAGE_KEY = "sonic.resumeId";

export const resumeStore = {
    get(): string | null {
        try {
            return sessionStorage.getItem(RESUME_STORAGE_KEY);
        } catch {
            return null;
        }
    },
    set(id: string) {
        try {
            sessionStorage.setItem(RESUME_STORAGE_KEY, id);
        } catch {
            // storage unavailable: resume just won't work in this tab
        }
    },
    clear() {
        try {
            sessionStorage.removeItem(RESUME_STORAGE_KEY);
        } catch {
            // ignore
        }
    }
};

/** idle: 4000; superseded: 4002; ended: 1000 session_ended; transport: anything else (resumable). */
export type CloseKind = "idle" | "superseded" | "ended" | "transport";

export function classifyClose(event: Pick<CloseEvent, "code" | "reason">): CloseKind {
    if (event.code === WS_CLOSE_IDLE_TIMEOUT) return "idle";
    if (event.code === WS_CLOSE_SUPERSEDED) return "superseded";
    if (event.code === 1000 && event.reason === WS_CLOSE_SESSION_ENDED_REASON) return "ended";
    return "transport";
}

export type ConnectionLostInfo = {
    code: number;
    reason: string;
    idle: boolean;
    kind: CloseKind;
    /** A background reconnect will follow and present the stored resume id. */
    resuming: boolean;
};

// Exponential backoff: 1s, 2s, 4s, 8s, 16s, max 30s
const MAX_RETRIES = 10;
const BASE_DELAY_MS = 1000;
const MAX_DELAY_MS = 30000;

async function fetchSessionToken(): Promise<string | null> {
    try {
        const resp = await fetch("/api/auth/session");
        if (!resp.ok) return null;
        const data = await resp.json();
        return data.token ?? null;
    } catch {
        // Endpoint doesn't exist or server unavailable — graceful fallback
        return null;
    }
}

export default function useRealTime({
    useDirectAoaiApi,
    aoaiEndpointOverride,
    aoaiApiKeyOverride,
    aoaiModelOverride,
    enableInputAudioTranscription,
    onWebSocketOpen,
    onWebSocketClose,
    onConnectionLost,
    onWebSocketError,
    onWebSocketMessage,
    onReceivedResponseCreated,
    onReceivedResponseDone,
    onReceivedResponseAudioDelta,
    onReceivedResponseAudioTranscriptDelta,
    onReceivedInputAudioBufferSpeechStarted,
    onReceivedExtensionMiddleTierToolResponse,
    onReceivedInputAudioTranscriptionCompleted,
    onReceivedSessionMetadata,
    onReceivedSessionResumed,
    onReceivedResumeRejected,
    onReconnectGaveUp,
    onReceivedRoundTripToken,
    onReceivedError
}: Parameters) {
    const [sessionToken, setSessionToken] = useState<string | null>(null);
    // Don't open the socket until the token fetch settles, otherwise the first
    // socket is torn down and replaced as soon as the token arrives.
    const [tokenReady, setTokenReady] = useState(!!useDirectAoaiApi);
    const [shouldConnect, setShouldConnect] = useState(true);

    // Fetch a session token on mount (graceful — null means no token required)
    useEffect(() => {
        if (useDirectAoaiApi) return;
        fetchSessionToken().then(token => {
            setSessionToken(token);
            setTokenReady(true);
        });
    }, [useDirectAoaiApi]);

    const buildWsEndpoint = () => {
        if (useDirectAoaiApi) {
            // GA realtime surface: /openai/v1/realtime addressed by `model`,
            // replacing the retired /openai/realtime?deployment=&api-version= form.
            return `${aoaiEndpointOverride}/openai/v1/realtime?api-key=${aoaiApiKeyOverride}&model=${aoaiModelOverride}`;
        }
        const base = `/realtime`;
        return sessionToken ? `${base}?token=${encodeURIComponent(sessionToken)}` : base;
    };

    const wsEndpoint = buildWsEndpoint();

    // Ref to break circular dependency: callbacks need sendJsonMessage,
    // but sendJsonMessage comes from useWebSocket which takes the callbacks.
    const sendJsonMessageRef = useRef<(msg: object, keep?: boolean) => void>(() => {});

    // The hook owns the outgoing queue: react-use-websocket is only ever called
    // with keep=false, so its own queue stays empty and cannot flush anything
    // ahead of extension.resume, which the server honours only as the first frame.
    const openRef = useRef(false);
    const pendingRef = useRef<object[]>([]);
    const send = useCallback((msg: object, keep = true) => {
        if (openRef.current) {
            sendJsonMessageRef.current(msg, false);
        } else if (keep) {
            pendingRef.current.push(msg);
        }
    }, []);

    const onMessageReceived = useCallback((event: MessageEvent<any>) => {
        onWebSocketMessage?.(event);

        let message: Message;
        try {
            message = JSON.parse(event.data);
        } catch (e) {
            console.error("Failed to parse JSON message:", e);
            throw e;
        }

        switch (message.type) {
            case "response.created":
                // Earliest signal that the AI is about to speak.
                // Flush any buffered mic audio on the server to prevent echo.
                sendJsonMessageRef.current({ type: "input_audio_buffer.clear" }, false);
                onReceivedResponseCreated?.(message);
                break;
            case "response.done":
                onReceivedResponseDone?.(message as ResponseDone);
                break;
            case "response.audio.delta":
                onReceivedResponseAudioDelta?.(message as ResponseAudioDelta);
                break;
            case "response.audio_transcript.delta":
                onReceivedResponseAudioTranscriptDelta?.(message as ResponseAudioTranscriptDelta);
                break;
            case "input_audio_buffer.speech_started":
                onReceivedInputAudioBufferSpeechStarted?.(message);
                break;
            case "conversation.item.input_audio_transcription.completed":
                onReceivedInputAudioTranscriptionCompleted?.(message as ResponseInputAudioTranscriptionCompleted);
                break;
            case "extension.middle_tier_tool_response":
                onReceivedExtensionMiddleTierToolResponse?.(message as ExtensionMiddleTierToolResponse);
                break;
            case "extension.session_metadata": {
                const metadata = message as ExtensionSessionMetadata;
                if (!useDirectAoaiApi && metadata.resumeId) resumeStore.set(metadata.resumeId);
                onReceivedSessionMetadata?.(metadata);
                break;
            }
            case "extension.session_resumed": {
                const resumed = message as ExtensionSessionResumed;
                if (resumed.resume_id) resumeStore.set(resumed.resume_id);
                onReceivedSessionResumed?.(resumed);
                break;
            }
            case "extension.resume_rejected":
                // The fresh session's extension.session_metadata (with a new id) follows.
                resumeStore.clear();
                onReceivedResumeRejected?.(message as ExtensionResumeRejected);
                break;
            case "extension.round_trip_token":
                onReceivedRoundTripToken?.(message as ExtensionRoundTripToken);
                break;
            case "error":
                onReceivedError?.(message);
                break;
        }
    }, [
        onWebSocketMessage,
        onReceivedResponseCreated,
        onReceivedResponseDone,
        onReceivedResponseAudioDelta,
        onReceivedResponseAudioTranscriptDelta,
        onReceivedInputAudioBufferSpeechStarted,
        onReceivedInputAudioTranscriptionCompleted,
        onReceivedExtensionMiddleTierToolResponse,
        onReceivedSessionMetadata,
        onReceivedSessionResumed,
        onReceivedResumeRejected,
        onReceivedRoundTripToken,
        onReceivedError,
        useDirectAoaiApi
    ]);

    const { sendJsonMessage, readyState } = useWebSocket(tokenReady ? wsEndpoint : null, {
        onOpen: () => {
            openRef.current = true;
            // Literal first frame on every open when this tab holds a resume id.
            const resumeId = useDirectAoaiApi ? null : resumeStore.get();
            if (resumeId) {
                sendJsonMessageRef.current({ type: "extension.resume", resume_id: resumeId }, false);
            }
            for (const queued of pendingRef.current.splice(0)) {
                sendJsonMessageRef.current(queued, false);
            }
            onWebSocketOpen?.();
        },
        onClose: (event) => {
            openRef.current = false;
            const kind = classifyClose(event);
            if (kind !== "transport") {
                // Final for this session: no background reconnect, and nothing
                // queued for it may leak into the next one.
                setShouldConnect(false);
                pendingRef.current = [];
                // 4002 keeps the id: another socket owns the session now.
                if (kind !== "superseded") resumeStore.clear();
            } else if (event.code === 4001 || event.reason?.includes("expired")) {
                // 401 close → refresh token and retry
                fetchSessionToken().then(setSessionToken);
            }
            const resuming = kind === "transport" && !useDirectAoaiApi && !!resumeStore.get();
            onConnectionLost?.({ code: event.code, reason: event.reason ?? "", idle: kind === "idle", kind, resuming });
            onWebSocketClose?.();
        },
        onError: event => onWebSocketError?.(event),
        onMessage: onMessageReceived,
        shouldReconnect: (event: CloseEvent) => classifyClose(event) === "transport",
        onReconnectStop: () => {
            setShouldConnect(false);
            onReconnectGaveUp?.();
        },
        reconnectAttempts: MAX_RETRIES,
        reconnectInterval: (attemptNumber: number) => {
            const delay = Math.min(BASE_DELAY_MS * Math.pow(2, attemptNumber), MAX_DELAY_MS);
            // Add jitter to prevent thundering herd
            return delay + Math.random() * 500;
        }
    }, shouldConnect);

    const isConnected = readyState === ReadyState.OPEN;

    // Re-open after an idle close or exhausted retries, with a fresh token
    // (the old one may have expired while the page sat idle).
    const reconnect = useCallback(async () => {
        if (shouldConnect) return;
        if (!useDirectAoaiApi) setSessionToken(await fetchSessionToken());
        setShouldConnect(true);
    }, [shouldConnect, useDirectAoaiApi]);

    // Keep ref in sync so onMessageReceived can call sendJsonMessage
    useEffect(() => {
        sendJsonMessageRef.current = sendJsonMessage;
    }, [sendJsonMessage]);

    const startSession = () => {
        const command: SessionUpdateCommand = {
            type: "session.update",
            session: {
                turn_detection: {
                    type: "server_vad",
                    threshold: 0.7,
                    prefix_padding_ms: 300,
                    silence_duration_ms: 500
                }
            }
        };

        if (enableInputAudioTranscription) {
            command.session.input_audio_transcription = {
                model: "whisper-1"
            };
        }

        // Kept for the next socket; sent after extension.resume when one is pending.
        send(command);
    };

    const addUserAudio = (base64Audio: string) => {
        const command: InputAudioBufferAppendCommand = {
            type: "input_audio_buffer.append",
            audio: base64Audio
        };

        // keep=false: drop, never queue, audio while the socket isn't open —
        // queued frames are replayed onto the next socket ahead of session.update.
        send(command, false);
    };

    const inputAudioBufferClear = () => {
        const command: InputAudioBufferClearCommand = {
            type: "input_audio_buffer.clear"
        };

        send(command, false);
    };

    const cancelResponse = () => {
        send({ type: "response.cancel" }, false);
    };

    const sendVerboseLogging = (enabled: boolean) => {
        send({ type: "extension.set_verbose_logging", enabled });
    };

    const sendLogToFile = (enabled: boolean) => {
        send({ type: "extension.set_log_to_file", enabled });
    };

    const sendVoiceChoice = (voice: string) => {
        send({ type: "extension.set_voice", voice });
    };

    // Explicit new order: the server deletes the order and closes 1000
    // session_ended. The id is dropped either way so no later open resumes it.
    const endSession = () => {
        if (!useDirectAoaiApi) send({ type: "extension.end_session" }, false);
        resumeStore.clear();
        pendingRef.current = [];
    };

    return {
        startSession,
        addUserAudio,
        inputAudioBufferClear,
        cancelResponse,
        sendVerboseLogging,
        sendLogToFile,
        sendVoiceChoice,
        endSession,
        isConnected,
        reconnect
    };
}
