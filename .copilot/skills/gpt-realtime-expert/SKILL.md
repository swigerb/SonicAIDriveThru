---
name: gpt-realtime-expert
description: Expert guidance for implementing OpenAI gpt-realtime-1.5, including WebRTC, WebSocket, and SIP configurations.
confidence: high
---

# gpt-realtime-1.5 Expertise
You are an expert in the OpenAI Realtime API (gpt-realtime-1.5). 

## Model Capabilities
- **Low Latency:** Optimized for speech-to-speech with ~32k input and 4k output tokens.
- **Modality:** Supports Text, Audio, and Image input; Text and Audio output.
- **Features:** Enhanced tool calling, multilingual accuracy, and natural prosody.

## Implementation Standards
- Prefer **WebRTC** for browser-based low-latency audio.
- Use **WebSockets** for server-side middle-tier applications.
- When handling audio deltas, use the updated event paths: `response.output_audio.delta`.
- Always implement VAD (Voice Activity Detection) for natural turn-taking.

## System Prompt Best Practices
- Use **ALL CAPS** for emphasis on critical instructions (NEVER, ALWAYS, ONLY).
- Use **bulleted format** organized into named sections — NEVER dense paragraphs.
- Add **variety rules** with example phrases to prevent robotic repetition.
- Add explicit **grounding rules** ("ONLY recommend items found in search results") to prevent hallucination.
- Keep prompts tight — long prompts increase first-response latency.

## VAD Tuning Guidelines
- **Threshold 0.7** for quiet demo/office environments with echo suppression active.
- **Threshold 0.8** for noisy environments or when echo suppression is incomplete.
- **prefix_padding_ms: 300** minimum — 200ms clips plosive consonants.
- **silence_duration_ms: 500** is optimal for conversational ordering flows.
- Always re-tune VAD threshold after adding/changing echo suppression — they interact.

## Echo Suppression Pattern (Middle-Tier WebSocket)
- Track `ai_speaking` flag: set on `response.audio.delta`, clear on `response.audio.done`.
- Apply cooldown (300ms) after AI stops speaking before accepting user audio.
- Send `input_audio_buffer.clear` after AI finishes to flush echoed audio.
- Reset suppression on `input_audio_buffer.speech_started` to preserve barge-in.
- Use substring checks (not JSON parse) for performance on the audio hot path.

## Critical Constraints
- Use all caps for emphasis in system prompts for this model.
- Use bullets over paragraphs for better instruction following.
- Avoid robotic repetition by adding "variety rules" in the session configuration.
- Use a pleasant and quick-serve restaurant drive-thru friendly voice.
