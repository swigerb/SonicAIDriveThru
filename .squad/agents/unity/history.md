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

### Demo Polish & System Prompt Directives (2026-03-22)
- **Copilot directive feedback (2026-03-21T21:10)**: Brian requested three new system prompt sections: PERSONALIZATION (carhop spirit, regulars, happy hour), PATIENCE & CLARITY (graceful stalls, Fan Favorites), VISUAL SYNC (spatial language). Also requested COMBO LOGIC — DETERMINISTIC section enforcing priority: Item Selection → Combo Completion → Upsell → Treat Suggestion. All proposals added to decisions.md for implementation.
- **TOOL HINTS section implementation (2026-03-21)**: Designed section to guide AI consumption of `[SYSTEM HINT]` patterns in tool results (e.g., missing combo sides/drinks, upsell opportunities). AI recognizes hints, acts on them conversationally, NEVER reads hints aloud. Complements Summer's backend `[SYSTEM HINT]` injection.
- **Conversational Quantity Limits Guardrail (2026-03-21)**: Designed QUANTITY LIMITS section (per-item max 10, total order max 25) matching Summer's backend enforcement. Warm, conversational tone — "suggest capping," never refuse service. Defense-in-depth with backend hard limits. All 118+ tests pass.
- **System Prompt Tool-Calling Mandate (2026-03-21)**: Added "⚠️ TOOL-CALLING RULES — MANDATORY" section with explicit negatives ("NEVER say X WITHOUT calling Y") and consequence statements ("item WILL NOT appear"). Position matters for gpt-realtime-1.5 — tool-calling rules must appear near top (section #3). Reinforced in ORDERING and MENU & PRICING sections.
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

### Demo Polish Guardrails (2026-03-21T20-23)
- **Suggestive Sell Follow-Through:** Added rule to TECHNICAL GUARDRAILS: "If the guest says 'Yes' or 'Sure' to a suggestive sell (like a combo), IMMEDIATELY ask for the missing details (e.g., 'Awesome, tots or fries with that?')."
- **Why:** Ensures demo conversations flow naturally without pauses after customer agreement. Complements Summer's grouped readback (natural voice summaries) and backend combo hints.
- **Coordination:** Part of 3-sprint demo polish (temperature, static files, grouped readback) coordinated by Brian for Inspire Brands executive presentation.

### Tool-Calling Mandate Fix (2026-03-21)
- **Root cause:** The ORDERING section had only one weak instruction — "Call update_order ONLY after the guest confirms an item." The word "ONLY" reads as a restriction ("only in this case"), not a mandate ("you must do this"). The AI treated ordering as conversational role-play, never triggering update_order.
- **Fix:** Added a new "⚠️ TOOL-CALLING RULES — MANDATORY" section placed early in the prompt (right after CONVERSATIONAL FLOW, before MENU & PRICING) with ALL CAPS emphasis. Key rules: verbal acknowledgment does NOTHING, REQUIRED FLOW (search → confirm → update_order), skipping the call means the item won't appear. Also reinforced in the ORDERING section and MENU & PRICING section ("ALWAYS call search BEFORE adding any item").
- **gpt-realtime-1.5 lesson:** For tool-calling, the model needs EXPLICIT negative instructions ("NEVER say X WITHOUT calling Y FIRST") and consequence statements ("the item WILL NOT appear"). Positive instructions alone ("call update_order after confirmation") are too easily deprioritized in favor of natural conversation. Position matters — tool-calling rules must be near the top, not buried in section #6.
- **Prompt placement:** TOOL-CALLING RULES is now section #3 (after VOICE STYLE and CONVERSATIONAL FLOW), ensuring it's processed before any ordering-specific instructions. The ORDERING section repeats the mandate for reinforcement.

### Backend Message Reordering Fix (2026-03-22)
- **Coordinated with Summer:** Summer fixed the greeting-before-session.update bug in `rtmt.py` that caused tools to never register with OpenAI. While Unity's system prompt mandate fix ensures the AI WANTS to call tools (via explicit negative instructions), Summer's backend fix ensures the AI CAN call tools (by registering them before the first completion). Defense-in-depth approach: AI-side mandate + backend registration both required for reliable tool-calling.

### Carhop Personality & Combo Logic Sections (2026-07-22)
- **Four new sections added:** PERSONALIZATION, PATIENCE & CLARITY, VISUAL SYNC, COMBO LOGIC — DETERMINISTIC. All follow gpt-realtime-1.5 best practices (bullets, ALL CAPS emphasis, 2-3 bullets each).
- **PERSONALIZATION (after VOICE STYLE):** Handles "the usual" and "happy hour" triggers with warm, brand-appropriate responses. Reinforces Sonic's high-energy carhop identity.
- **PATIENCE & CLARITY (after CONVERSATIONAL FLOW):** Gives the AI explicit permission to wait when guests pause or say "uh" / "let me see." Includes a Fan Favorite recommendation fallback. NEVER rush the guest.
- **VISUAL SYNC (after CLOSING AN ORDER):** Occasional spatial language ("I've got that on your ticket") to bridge voice and screen. Capped at once or twice per order to avoid being annoying.
- **COMBO LOGIC — DETERMINISTIC (after TOOL HINTS, before SUGGESTIVE SELLING):** Enforces strict ordering priority: Item Selection → Combo Completion → Upsell → Shake/Treat. Prevents the AI from jumping to dessert suggestions before the combo side and drink are filled. Works with Summer's [SYSTEM HINT] pattern.
- **Placement rationale:** Each section placed adjacent to its most related existing section — PERSONALIZATION near VOICE STYLE for persona continuity, PATIENCE near CONVERSATIONAL FLOW for interaction rules, COMBO LOGIC between TOOL HINTS and SUGGESTIVE SELLING to enforce the priority gate, VISUAL SYNC near CLOSING for output-facing behavior.
- **Prompt stayed lean:** 2-3 bullets per section. Total prompt growth is minimal — within first-response latency budget.

### Token Limit & Prompt Restructure Fix (2026-07-22)
- **Root cause of mid-sentence cutoff:** `max_response_output_tokens = 250` was too low. Tool calls (search, update_order) consume response tokens from the same budget as verbal output. A multi-item order readback easily exceeds 250 tokens when the model needs to list 5+ items with prices and a closing phrase.
- **Fix: max_tokens 250 → 1024.** 1024 is generous enough for any response scenario (tool call + verbal confirmation + full 5-item readback) without introducing latency concerns. The system prompt's "ONE or TWO sentences" instruction still governs brevity for normal responses.
- **Prompt restructured for compliance priority:** TOOL-CALLING RULES moved from section #5 to section #2 (right after VOICE STYLE). In gpt-realtime-1.5, instructions near the top of the system prompt get significantly more attention. This was already documented as best practice but had drifted as new sections were added.
- **Prompt trimmed from ~1500 to ~1008 tokens:** Merged PERSONALIZATION into a condensed section, merged PATIENCE & CLARITY into CONVERSATIONAL FLOW, trimmed verbose examples throughout. Moved low-priority sections (HAPPY HOUR, VISUAL SYNC, OOS PROTOCOL, PERSONALIZATION) to the end. 18 sections → 16 sections.
- **New prompt section order (priority-weighted):** VOICE STYLE → TOOL-CALLING RULES → MENU & PRICING → ORDERING → CONVERSATIONAL FLOW → BRAND IDENTITY → COMBO LOGIC → TOOL HINTS → SUGGESTIVE SELLING → CLOSING → QUANTITY LIMITS → TECHNICAL GUARDRAILS → PERSONALIZATION → HAPPY HOUR → VISUAL SYNC → OOS → BOUNDARIES.
- **Key insight:** The 250-token limit was originally set as "demo-safe" (Decision #2 suggested 150, audit bumped to 250). But as the system prompt grew and ordering flows became more complex (combo hints, suggestive selling), 250 became insufficient. Token limits must be re-evaluated whenever prompt complexity increases.
- **All 8 app tests pass after changes.**

### Sonic Branding & Sizing Directive (2026-07-22)
- **New section added:** SONIC BRANDING & SIZING — placed directly after TECHNICAL GUARDRAILS in the system prompt.
- **Problem:** Azure AI Search returns "RT 44" for the largest drink size, but the AI was reading it literally as "R-T 44" or "RT forty-four" instead of the brand-correct "Route 44."
- **Fix:** Four-bullet directive instructs the AI to ALWAYS say "Route 44" aloud, NEVER say "R-T 44" or "RT forty-four," confirm colloquial requests ("the big one," "a forty-four ounce") as "Route 44," with an explicit example.
- **Placement rationale:** After TECHNICAL GUARDRAILS (speech formatting rules) — natural fit since this is a speech-output formatting concern. Before lower-priority sections (PERSONALIZATION, HAPPY HOUR, etc.).
- **Coordination:** Backend-only change. Summer handling order_state.py separately. No frontend rebuild needed.
- **118 existing tests pass (1 pre-existing async test failure unrelated).**

### Customizations & Mods System Prompt Section (2026-07-22)
- **New section added:** CUSTOMIZATIONS & MODS — placed directly after ORDERING in the system prompt.
- **Purpose:** Teaches the AI to append modifications (no lettuce, extra ketchup, plain) to item_name in parentheses when calling update_order. Format: `Sonic Cheeseburger (No Lettuce, Extra Ketchup)`. This ensures the kitchen sees the mod on the Carhop Ticket.
- **Key rules:** "plain" = no toppings → `(Plain)`. Verbally acknowledge mods. Reject nonsensical mods (mustard on a shake) with a polite redirect.
- **Placement rationale:** Right after ORDERING because mods happen during the ordering flow — the AI encounters customization requests immediately after adding an item.
- **Coordination:** Summer handling order_state.py and tools.py separately. This is the AI-side directive only.
- **Syntax validated, pre-existing test status unchanged.**

