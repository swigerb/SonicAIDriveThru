# Skill: WebSocket Echo Suppression for Realtime Voice AI

## Problem

In a WebSocket middleware that bridges client audio ↔ AI model (e.g., OpenAI Realtime API), the AI's audio response leaks from the client's speakers back into the microphone, gets forwarded to the model, and is transcribed as phantom user input — creating an infinite self-conversation loop.

## Pattern

Track "AI is speaking" state server-side using message type detection. Gate incoming user audio while AI audio is flowing + a brief cooldown. Flush the model's input buffer after each response.

### Key Components

1. **State tracking** (in server→client message path):
   - `response.audio.delta` → set `ai_speaking = True`
   - `response.audio.done` → clear flag, start cooldown timer, send `input_audio_buffer.clear`
   - `input_audio_buffer.speech_started` → clear flag + cooldown (barge-in)

2. **Audio gating** (in client→server message path):
   - If `ai_speaking` or within cooldown window → drop `input_audio_buffer.append`

3. **Performance**: Use fast substring markers (`'"response.audio.delta"' in data`) instead of JSON parse on the hot path.

4. **Thread safety**: asyncio single-threaded event loop means shared state between coroutines needs no locks.

## When to Use

- Any WebSocket middleware sitting between a voice client and an AI realtime API
- Complement to (not replacement for) client-side mic muting — covers the timing gap

## Tunables

- `_ECHO_COOLDOWN_SEC` (default 0.3s): Post-response suppression window. Increase if echo persists on high-latency audio hardware.
