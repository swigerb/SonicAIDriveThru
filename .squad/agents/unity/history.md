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

### Upselling & ACV System Prompt Upgrade (2026-03-21)
- **Four new sections added:** CONVERSATIONAL FLOW, BRAND IDENTITY, SUGGESTIVE SELLING, TECHNICAL GUARDRAILS — all following gpt-realtime-1.5 best practices (bullets, ALL CAPS emphasis, concise).
- **Suggestive selling tiers:** Combo conversion (burger alone → combo ask), upsize (Small/Medium → Large), Sonic Signature treat suggestion when order has no dessert. ONE suggestion at a time to stay natural.
- **Brand Identity — Tots First:** Sonic's differentiator. Tots always mentioned before fries when offering sides. Execs will notice this.
- **Conversational Flow:** No filler words (Okay, So, Well) at response start — reduces perceived latency. Immediate pivot on guest interrupts.
- **Technical Guardrails:** Currency spoken naturally ("six forty-nine") — never "4.49" or dollar-sign reading. Long orders grouped ("Three Cheeseburger combos") instead of listing every modification.
- **ORDERING section updated:** Added combo-check directive — always ask about combo before moving on when burger/sandwich ordered alone.
- **CLOSING section updated:** Added item grouping rule for long orders.
- **Prompt stayed concise:** 4 new sections added without bloating — each section is 3-4 bullets max. Total prompt still fits comfortably within first-response latency budget.
- **Coordinated with Summer:** TO_BOTH routing ensures conversation continues naturally through multi-item orders (no dead silence after order confirmation).
- **Demo validation:** Tested with 3-4 complete orders covering all categories. Combo triggers work. Tots-first branding fires correctly. Currency spoken naturally. 5+ item orders group properly in closing recap.

### TOOL HINTS Section (2026-03-21)
- **[SYSTEM HINT] pattern:** Summer's backend embeds `[SYSTEM HINT: ...]` in tool results to guide the AI (e.g., prompting for missing combo sides/drinks, upsell opportunities). Added a 2-bullet TOOL HINTS section right after ORDERING so the AI knows to act on these hints immediately and conversationally — and NEVER read them aloud.
- **Placement rationale:** After ORDERING, before SUGGESTIVE SELLING. Tool results come back during ordering flow, so the AI encounters hints in that context. Keeps the instruction close to where it's actionable.
- **Coordination with Summer:** This is the AI-side complement to Summer's backend `[SYSTEM HINT]` injection in tool results. Defense-in-depth: backend decides *when* to hint, system prompt tells the AI *how* to act on it.
