import useWebSocket from "react-use-websocket";
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
    ExtensionRoundTripToken
} from "@/types";

type Parameters = {
    useDirectAoaiApi?: boolean; // If true, the middle tier will be skipped and the AOAI ws API will be called directly
    aoaiEndpointOverride?: string;
    aoaiApiKeyOverride?: string;
    aoaiModelOverride?: string;

    enableInputAudioTranscription?: boolean;
    onWebSocketOpen?: () => void;
    onWebSocketClose?: () => void;
    onWebSocketError?: (event: Event) => void;
    onWebSocketMessage?: (event: MessageEvent<any>) => void;

    onReceivedResponseCreated?: (message: Message) => void;
    onReceivedResponseAudioDelta?: (message: ResponseAudioDelta) => void;
    onReceivedInputAudioBufferSpeechStarted?: (message: Message) => void;
    onReceivedResponseDone?: (message: ResponseDone) => void;
    onReceivedExtensionMiddleTierToolResponse?: (message: ExtensionMiddleTierToolResponse) => void;
    onReceivedSessionMetadata?: (message: ExtensionSessionMetadata) => void;
    onReceivedRoundTripToken?: (message: ExtensionRoundTripToken) => void;
    onReceivedResponseAudioTranscriptDelta?: (message: ResponseAudioTranscriptDelta) => void;
    onReceivedInputAudioTranscriptionCompleted?: (message: ResponseInputAudioTranscriptionCompleted) => void;
    onReceivedError?: (message: Message) => void;
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
    onReceivedRoundTripToken,
    onReceivedError
}: Parameters) {
    const [sessionToken, setSessionToken] = useState<string | null>(null);

    // Fetch a session token on mount (graceful — null means no token required)
    useEffect(() => {
        fetchSessionToken().then(setSessionToken);
    }, []);

    const buildWsEndpoint = () => {
        if (useDirectAoaiApi) {
            return `${aoaiEndpointOverride}/openai/realtime?api-key=${aoaiApiKeyOverride}&deployment=${aoaiModelOverride}&api-version=2024-10-01-preview`;
        }
        const base = `/realtime`;
        return sessionToken ? `${base}?token=${encodeURIComponent(sessionToken)}` : base;
    };

    const wsEndpoint = buildWsEndpoint();

    const retryCountRef = useRef(0);
    // Ref to break circular dependency: callbacks need sendJsonMessage,
    // but sendJsonMessage comes from useWebSocket which takes the callbacks.
    const sendJsonMessageRef = useRef<(msg: object) => void>(() => {});

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
                sendJsonMessageRef.current({ type: "input_audio_buffer.clear" });
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
            case "extension.session_metadata":
                onReceivedSessionMetadata?.(message as ExtensionSessionMetadata);
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
        onReceivedRoundTripToken,
        onReceivedError
    ]);

    const { sendJsonMessage } = useWebSocket(wsEndpoint, {
        onOpen: () => {
            retryCountRef.current = 0;
            onWebSocketOpen?.();
        },
        onClose: (event) => {
            // 401 close → refresh token and retry
            if (event.code === 4001 || event.reason?.includes("expired")) {
                fetchSessionToken().then(setSessionToken);
            }
            onWebSocketClose?.();
        },
        onError: event => onWebSocketError?.(event),
        onMessage: onMessageReceived,
        shouldReconnect: () => true,
        reconnectAttempts: MAX_RETRIES,
        reconnectInterval: (attemptNumber: number) => {
            const delay = Math.min(BASE_DELAY_MS * Math.pow(2, attemptNumber), MAX_DELAY_MS);
            // Add jitter to prevent thundering herd
            return delay + Math.random() * 500;
        }
    });

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

        sendJsonMessage(command);
    };

    const addUserAudio = (base64Audio: string) => {
        const command: InputAudioBufferAppendCommand = {
            type: "input_audio_buffer.append",
            audio: base64Audio
        };

        sendJsonMessage(command);
    };

    const inputAudioBufferClear = () => {
        const command: InputAudioBufferClearCommand = {
            type: "input_audio_buffer.clear"
        };

        sendJsonMessage(command);
    };

    const cancelResponse = () => {
        sendJsonMessage({ type: "response.cancel" });
    };

    const sendVerboseLogging = (enabled: boolean) => {
        sendJsonMessage({ type: "extension.set_verbose_logging", enabled });
    };

    const sendLogToFile = (enabled: boolean) => {
        sendJsonMessage({ type: "extension.set_log_to_file", enabled });
    };

    return { startSession, addUserAudio, inputAudioBufferClear, cancelResponse, sendVerboseLogging, sendLogToFile };
}
