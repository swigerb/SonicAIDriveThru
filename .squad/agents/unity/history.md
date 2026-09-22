# Unity — History

## Project Context

- **Project:** Sonic AI Drive-Thru Voice Assistant — a voice-driven drive-thru ordering experience showcasing Azure OpenAI GPT-4o Realtime, Azure AI Search, and Azure Container Apps.
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

### 2026-07-09: Model Migration — gpt-4o-realtime-preview → gpt-realtime-1.5 (GA)

**Problem:** The demo was pinned to `gpt-4o-realtime-preview` (version `2024-10-01`), which has been retired from Azure and can no longer be deployed. `azd up` would fail at provisioning.

**Target:** `gpt-realtime-1.5` version `2026-02-23` (GA, `GlobalStandard` SKU, retirement 2027-08-24 — longest runway of any realtime model).

**GA API Surface Changes (verified against official docs):**
- **WebSocket URL:** `/openai/realtime?api-version=X&deployment=Y` → `/openai/v1/realtime?model=Y` (no api-version param)
- **Event names (server→client):** `response.audio.delta` → `response.output_audio.delta`, `response.audio.done` → `response.output_audio.done`, `response.audio_transcript.delta` → `response.output_audio_transcript.delta`, `response.audio_transcript.done` → `response.output_audio_transcript.done`, `response.text.delta` → `response.output_text.delta`, `response.text.done` → `response.output_text.done`, `conversation.item.created` → `conversation.item.added`
- **Session config:** Voice moved from `session.voice` to `session.audio.output.voice`; audio format to nested `audio.input/output`; turn detection to `audio.input.turn_detection`
- **Auth:** Kept `cognitiveservices.azure.com/.default` scope (matches Brian's `Microsoft.CognitiveServices/account` resource type)
- **Doc sources:** `https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/realtime-audio-websockets`, `https://developers.openai.com/api/reference/resources/realtime`

**Key Design Decision — Frontend Compatibility:**
Since `app/frontend/` is off-limits, the middleware (`rtmt.py` + `audio_pipeline.py`) translates GA event names back to legacy names before forwarding to the client. Both GA and legacy names are in `_PASSTHROUGH_SERVER_TYPES`. A `_GA_TO_LEGACY_EVENTS` mapping dict handles the translation.

**Voice:** Unified on `coral` (warm, friendly, clear — fits carhop persona). Fixed inconsistency where `config.yaml` had `coral` but `main.parameters.json` defaulted to `alloy`.

**Files Changed (16):**
- `infra/main.bicep` — deployment name/model/version updated
- `infra/main.parameters.json` — voice default `alloy` → `coral`
- `app/backend/rtmt.py` — removed `api_version`, changed WS URL/params, added GA event translation, dual voice injection, `conversation.item.added` handling, echo suppression for both marker sets
- `app/backend/audio_pipeline.py` — expanded passthrough sets, added `_GA_TO_LEGACY_EVENTS` mapping, updated markers
- `app/backend/config.yaml` — removed `api_version` line
- `app/backend/app.py` — removed `api_version` assignment
- `app/backend/.env-sample` — updated deployment name, removed version env var
- `app/backend/prompts/sonic/system_prompt.yaml`, `manifest.yaml` — model name updated
- `docs/existing_services.md`, `docs/manual_setup.md`, `voice_rag_README.md` — model references updated
- `app/backend/tests/test_rtmt.py` — 5 new tests for GA event translation and passthrough coverage
- `app/backend/tests/test_app.py`, `test_performance.py` — deployment name in mock env vars

**Validation:** Bicep builds (pre-existing BCP420 error in `container-apps.bicep` unrelated). 365 tests passed (360 original + 5 new). Ruff clean.

**Needs Live Verification:**
- Does `cognitiveservices.azure.com/.default` auth scope work with `/openai/v1` endpoint?
- Does the GA server accept flat `session.update` format alongside nested?
- Which exact event names appear on the wire from `gpt-realtime-1.5`?
- Which voices does `gpt-realtime-1.5` actually support? (docs don't enumerate per-model)

### 2026-09-22: $0.00 Carhop Ticket — Unconfigured Upstream Session + GA Voice Lock; gpt-realtime-2.1

**Problem:** On the deployed demo the Carhop Ticket stayed at $0.00 for the whole conversation. The assistant sounded in character but never called a tool. Its first reply was generic ("Hey there! Sounds like you're just warming up..."). No application code had changed since the verified August deploy.

**Root Cause (confirmed in prod Log Analytics + a local repro against the real endpoint):**
- The browser sends `session.update` only from `startSession()`, i.e. when the mic is pressed. It does not send one when react-use-websocket auto-reconnects.
- Prod sequence:
  - 21:02:30 — the idle checker closed the session.
  - 21:03:15 — `Received frame with non-zero reserved bits` killed the new socket. It auto-reconnected with the mic live.
  - The fresh upstream session ran on service defaults: no tools, generic instructions, voice alloy, server VAD auto-responding.
  - 21:03:24 — the model answered the mic audio; that was the generic line.
  - 21:03:30 — the browser's `session.update` (4 tools, `tool_choice=auto`, voice shimmer) was **rejected** with `invalid_request_error` / `cannot_update_voice`.
- GA rejects the *whole* event, so tools and instructions were never applied. The "persona" came only from the greeting item text.
- Two premises were wrong. There *was* an error event, and the instructions were *not* reaching the model.
- `tool_choice` was never "none": `self.tools` is populated synchronously before `attach_to_app`.
- Voice lock, verified live on gpt-realtime-1.5:
  - Sending the same voice after audio is accepted.
  - Sending a different voice after audio rejects the whole event.
  - Omitting the voice is accepted.

**Fix (`rtmt.py`):**
- A server-authoritative bootstrap `session.update` is now the first upstream frame after `ws_connect`. It carries tools, instructions, voice, and the browser's VAD/transcription values (`_BOOTSTRAP_CLIENT_SESSION`).
- A per-connection `assistant_audio_seen` flag tracks when the model has spoken.
  - Once it is set, `_build_session(voice_locked=True)` strips `audio.output.voice`.
  - `extension.set_voice` is deferred once it is set. The picker's voice is process-wide, so another tab can change it mid-call.
- The greeting waits for `session.updated` (5 s timeout). This is the same fix as the sibling brand repo's `ba8c94d`.
- The greeting is triggered by the client `session.update`, not the bootstrap, so the page never greets unprompted.
- The server-override logic moved into `_build_session()`, so the bootstrap and the client update share one path.

**Validation:**
- Local repro (reconnect scenario, real AOAI):
  - Before the fix: `cannot_update_voice`, `update_order_calls=0`, total $0.00.
  - After the fix: no error, `update_order_calls=2`, total $10.13.
- `tests/test_session_bootstrap.py` has 10 tests. Mutation-checked:
  - reverting rtmt.py fails 6;
  - removing the bootstrap fails 5;
  - removing the voice strip fails 3;
  - an unconditional picker send fails 1.
- 412 backend tests pass; ruff is clean.

**gpt-realtime-2.1 (GA 2026-07-07, retires 2027-07-31):**
- `infra/main.bicep` → `gpt-realtime-2.1` / `2026-07-07` / `GlobalStandard`.
  - The deployment name changes, so ARM's incremental mode leaves the old 1.5 deployment in place for rollback.
- The GA surface is unchanged vs 1.5: same URL, session shape, event names, and 10 voices.
- The only additions are `reasoning.effort` and `parallel_tool_calls`, for reasoning models only.
  - `_to_ga_session` allows both through, but nothing sends them by default.
  - On a non-reasoning model an unsupported field would reject the update, and the tools with it.
- Learn still labels 2.1 "preview"; the resource model catalog says GenerallyAvailable.
- Voices: OpenAI recommends marin/cedar for best quality.
  - Recommended carhop default: **marin**.
  - `shimmer` is left in place pending Brian's ear test, because the voice set did not change.

**Needs Live Verification:**
- Whether 2.1 accepts the bootstrap payload, including `input_audio_transcription.model=whisper-1`. Learn notes an Azure deviation that requires a deployment name in that field.
- `reasoning.effort` latency tuning.
- The cause of the reserved-bits websocket error.

<!-- Older detailed sections archived above for space. Current learnings focused on Phase 3 integration. -->

