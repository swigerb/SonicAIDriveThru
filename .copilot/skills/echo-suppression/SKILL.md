# Skill: WebSocket Echo Suppression for Realtime Voice AI

## Problem

In a WebSocket middleware that bridges client audio ↔ AI model (e.g., OpenAI Realtime API), the AI's audio response leaks from the client's speakers back into the microphone, gets forwarded to the model, and is transcribed as phantom user input — creating an infinite self-conversation loop.

## Pattern

Multi-layered suppression combining **client-side early muting** and **server-side audio gating** to eliminate the timing gap where echo leaks through.

### Client-Side (Frontend)

1. **Early mute on `response.created`** — mute the mic gain node at the earliest possible server event, BEFORE audio deltas arrive. Muting on `response.audio.delta` is too late — audio samples have already been sent.
2. **`input_audio_buffer.clear` on `response.created`** — flush any already-buffered echo from the server's audio pipeline.
3. **Gain node mute/unmute** — set gain to 0 (muted) / 1 (unmuted). Keeps the media stream alive so there's no permission re-prompt, and hardware echo cancellation stays active.
4. **Unmute on `response.done`** — re-open the mic when the AI finishes speaking.
5. **Barge-in handling** — `input_audio_buffer.speech_started` stops playback, unmutes mic, resets state.

### Server-Side (Middleware)

1. **State tracking** (in server→client message path):
   - `response.audio.delta` → set `ai_speaking = True`
   - `response.audio.done` → clear flag, start cooldown timer, send `input_audio_buffer.clear`
   - `input_audio_buffer.speech_started` → clear flag + cooldown (barge-in)

2. **Audio gating** (in client→server message path):
   - If `ai_speaking` or within cooldown window → drop `input_audio_buffer.append`

3. **Performance**: Use fast substring markers (`'"response.audio.delta"' in data`) instead of JSON parse on the hot path.

### Key Insight: Event Ordering

The OpenAI Realtime API sends events in this order:
1. `response.created` ← **mute here** (earliest signal)
2. `response.output_item.added`
3. `response.content_part.added`
4. `response.audio.delta` (repeated) ← too late to mute
5. `response.audio_transcript.delta` (interleaved)
6. `response.done` ← unmute here

### Circular Dependency Pattern (React)

When a `useCallback` inside a hook needs to call `sendJsonMessage` from `useWebSocket`, but `useWebSocket` takes that callback as a parameter, use a `useRef` to break the cycle:
```typescript
const sendRef = useRef<(msg: object) => void>(() => {});
const onMessage = useCallback(() => { sendRef.current({...}); }, []);
const { sendJsonMessage } = useWebSocket(url, { onMessage });
useEffect(() => { sendRef.current = sendJsonMessage; }, [sendJsonMessage]);
```

## When to Use

- Any WebSocket middleware sitting between a voice client and an AI realtime API
- Client-side mic muting alone isn't enough — you also need buffer clearing
- Server-side gating alone isn't enough — there's a timing gap before the gate activates

## Tunables

- `_ECHO_COOLDOWN_SEC` (default 0.3s): Post-response suppression window. Increase if echo persists on high-latency audio hardware.
- VAD `threshold` (default 0.8): Higher rejects weak echo; too high may miss soft-spoken users.
- VAD `silence_duration_ms` (default 500): Buffer before committing detected speech.
