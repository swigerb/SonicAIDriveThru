# Unity — History

## Project Context

- **Project:** Dunkin Voice Chat Assistant — an Inspire Brands–themed, voice-driven ordering experience showcasing Azure OpenAI GPT-4o Realtime, Azure AI Search, and Azure Container Apps.
- **Owner:** Brian Swiger
- **Stack:** Python (aiohttp, WebSockets), React/TypeScript, Azure OpenAI GPT-4o Realtime API, Azure AI Search, Azure Speech SDK
- **Key files:** `app/backend/rtmt.py` (realtime middle tier), `app/backend/app.py`, `app/frontend/src/hooks/useRealtime.tsx`
- **Joined:** 2026-03-21

## Learnings

### 2026-03-26: Combo Size Prompting Fix Sprint (Unity's Part)
- **Problem:** When guest accepted a combo upsell, the AI defaulted combo side/drink to Medium without asking what size the guest wanted.
- **Root Cause:** Blanket "default to MEDIUM" rule in MENU_AND_PRICING overriding contextual combo-completion behavior.
- **Solution:** Scoped "default to MEDIUM" rule to standalone items only. Added explicit COMBO SIZE PROMPTING section requiring AI to ask for side and drink sizes when guest accepts combo. Updated SUGGESTIVE_SELLING with explicit size-ask instructions.
- **Changes:** Three surgical edits to `app/backend/prompts/sonic/system_prompt.yaml` (MENU_AND_PRICING, COMBO_LOGIC, SUGGESTIVE_SELLING sections).
- **Trade-off:** Adds one extra conversational turn when guest doesn't specify sizes (acceptable — better than wrong sizes).
- **Impact:** Eliminates silent Medium defaulting on combo components. Guests now get asked what size they want, improving order accuracy and UX.

### 2026-03-21 through 2026-03-22: Demo Readiness & System Prompt Optimization (Consolidated)

**System Prompt Best Practices (gpt-realtime-1.5 Patterns):**
- Bullets > paragraphs for instruction-following. ALL CAPS for emphasis. Explicit negative instructions ("NEVER say X WITHOUT calling Y FIRST") + consequence statements ("item WILL NOT appear") required for tool-calling mandates. Dense paragraphs cause failures.
- Section positioning matters heavily — TOOL-CALLING RULES must be early (section #2, right after VOICE STYLE). gpt-realtime-1.5 prioritizes top-of-prompt instructions.
- COMBO LOGIC — DETERMINISTIC: Strict priority (Item Selection → Combo Completion → Upsell → Treat Suggestion) prevents jumping to desserts before combo sides.
- QUANTITY LIMITS: Conversational tone ("suggest capping"), never refuse service. Complements backend enforcement.
- TOOL HINTS: `[SYSTEM HINT]` patterns from backend — AI acts on them immediately, NEVER reads aloud.

**VAD & Latency Optimization:**
- VAD threshold: 0.8 for noisy/echo environments; 0.7 for clean demo settings. Always retune after echo suppression.
- Prefix padding: Minimum 300ms for reliable speech capture (avoids plosive clipping).
- No filler words at response start (Okay, So, Well) — reduces perceived latency.
- Temperature: 0.5 for fast TTFT.

**Prompt Token Budgeting:**
- 250 max_tokens was insufficient once ordering flows grew (combo hints, upsell suggestions, multi-item readbacks). Raised to 1024.
- Token limits must be re-evaluated whenever prompt complexity increases.
- Tool calls share token budget with verbal output — must reserve headroom.

**Coordination Patterns:**
- Backend message reordering (Summer) + system prompt tool-calling mandate (Unity) both required for reliable tool execution.
- Backend `[SYSTEM HINT]` injection + Unity's TOOL HINTS section = defense-in-depth backend decides *when* to hint, AI knows *how* to act.
- Backend enforcement + AI conversational guardrails = defense-in-depth.

### 2026-03-25: Prompt YAML Content Extraction

**Files Created:**
- `system_prompt.yaml` — 22 sections extracted verbatim, priority-ordered for gpt-realtime-1.5 compliance
- `greeting.yaml`, `tool_schemas.yaml`, `error_messages.yaml`, `hints.yaml`, `manifest.yaml`
- Total: ~8.5 KB of brand-portable prompt content

**Key Decisions:**
- TOOL-CALLING RULES moved to section #2 (confirmed gpt-realtime-1.5 best practice)
- System prompt trimmed ~33% via section merging, verbose example removal
- max_response_output_tokens increased to 1024 (tool call + verbal budget)
- Tool descriptions branded (not generic) for future brand portability
- Error messages use Jinja2 StrictUndefined for early validation

**Coordination:** Summer's `prompt_loader.py` reads manifest-driven YAML at startup. All 125 tests pass.

### 2026-03-26: Same-Utterance Combo Fix (Critical Demo Bug)

**Problem:** When a customer specified a combo entree, side, AND drink in one sentence (e.g., "bacon double cheeseburger combo with medium tots and a large diet Coke"), the AI ignored the side and drink, then re-asked for them — causing multiple wasted turns.

**Root Cause:** `update_order` is single-item. After the first call (combo entree), the backend's `get_combo_requirements()` returns a `[SYSTEM HINT]` saying "ask for side and drink." The AI blindly followed the hint instead of processing the remaining items the customer already specified.

**Fix (prompt-only, 3 sections):**
1. **COMBO_LOGIC** — Added "SAME-UTTERANCE COMBO RULE" block: parse ALL components from the sentence first, call update_order back-to-back for each, ignore [SYSTEM HINT] if items already mentioned, only ask about truly missing components.
2. **COMBO_PIVOT_RULES** — Added: hints reflect state after each individual call; if unprocessed items remain from utterance, add them before responding to the hint.
3. **TOOL_CALLING_RULES** — Added "MULTI-ITEM UTTERANCES" rule: process all mentioned items before responding verbally.

**Validation:** YAML valid, 337 tests pass, no code changes.

### 2026-03-27: Combo Size Prompting Fix (Critical Demo Bug)

**Problem:** When a guest accepted a combo upsell (e.g., "Yeah, I'll take Tots and a drink"), the AI defaulted the side to Medium without asking the guest what size they wanted. Drink size was also not asked.

**Root Cause:** Prompt priority conflict — the blanket "default to MEDIUM" rule in MENU_AND_PRICING (priority 4) overrode the vague "ask for missing details" in SUGGESTIVE_SELLING (priority 12). gpt-realtime-1.5 prioritizes higher-ranked sections.

**Fix (3 surgical edits to system_prompt.yaml):**
1. **MENU_AND_PRICING** — Scoped "default to MEDIUM" to STANDALONE items only. Added explicit callout that combo side/drink slots require asking the guest.
2. **COMBO_LOGIC** — Added new "COMBO SIZE PROMPTING — CRITICAL" block: MUST ask what size for combo components, ask side size first then drink, skip asking only if guest already specified sizes.
3. **SUGGESTIVE_SELLING** — Changed vague "ask for missing details" to specific: "ask what SIZE side and what SIZE drink they want" with example phrasing.

**Pattern:** When a blanket default rule conflicts with a contextual behavior rule, scope the default explicitly. Use ⚠️ CRITICAL markers and ALL CAPS for override rules — gpt-realtime-1.5 respects these formatting cues for instruction priority.

**Validation:** YAML valid, 347 tests pass (1 pre-existing async failure unrelated).

<!-- Older detailed sections archived above for space. Current learnings focused on Phase 3 integration. -->

