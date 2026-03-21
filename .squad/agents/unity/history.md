# Unity — History

## Project Context

- **Project:** Dunkin Voice Chat Assistant — an Inspire Brands–themed, voice-driven ordering experience showcasing Azure OpenAI GPT-4o Realtime, Azure AI Search, and Azure Container Apps.
- **Owner:** Brian Swiger
- **Stack:** Python (aiohttp, WebSockets), React/TypeScript, Azure OpenAI GPT-4o Realtime API, Azure AI Search, Azure Speech SDK
- **Key files:** `app/backend/rtmt.py` (realtime middle tier), `app/backend/app.py`, `app/frontend/src/hooks/useRealtime.tsx`
- **Joined:** 2026-03-21

## Learnings

### Demo Readiness Audit (2026-03-21)
- **System prompt best practices for gpt-realtime-1.5:** Bullets > paragraphs, ALL CAPS for emphasis, explicit variety rules prevent robotic repetition. Dense paragraph prompts cause instruction-following failures.
- **VAD threshold is context-dependent:** 0.8 is for noisy/echo environments. With proper echo suppression in place, 0.7 is better for demo/office settings. Always tune VAD after adding echo suppression — the two interact.
- **Prefix padding:** 200ms clips plosive consonants. 300ms is the minimum for reliable speech capture in demos.
- **Echo suppression architecture:** The server-side approach (ai_speaking flag + cooldown + buffer clear) is the correct pattern for middle-tier WebSocket proxies. Client-side and server-side protections are complementary.
- **Key file paths:** System prompt in `app/backend/app.py` line 127+, VAD config in `app/frontend/src/hooks/useRealtime.tsx` line 163+, echo suppression in `app/backend/rtmt.py` lines 298-386.
- **Token limits:** 250 max_tokens is the demo-safe value. 150 risks truncation on multi-item recaps. The system prompt's "ONE or TWO sentences" instruction handles brevity.
- **Voice choice:** `coral` is the correct voice for Sonic carhop persona — warm, friendly, good energy.
- **Brian's priority:** Demo polish for Inspire Brands executives. Zero tolerance for robotic repetition, hallucinated menu items, or truncated responses.

### Quantity Limit Guardrails (2026-03-21)
- **Conversational quantity limits in system prompt:** Added QUANTITY LIMITS section between ORDERING and CLOSING sections. Per-item max 10, total order max 25 — matches Summer's backend enforcement. Uses friendly drive-thru language, not error messages. NEVER refuses service — always offers the closest alternative.
- **Prompt placement matters:** Quantity limits go between ORDERING and CLOSING because that's the natural conversation flow — the AI processes the order, checks limits, then closes.
- **Defense-in-depth pattern continues:** AI handles it conversationally first (this change), backend enforces hard limits second (Summer's change). Same layered approach as echo suppression.
