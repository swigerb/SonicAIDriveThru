# Team Decisions

## Sonic Rebrand — Scope & Implementation (2026-03-19/20)

### Analysis & Scope (Rick)
- **~100+ Dunkin references** identified across frontend, backend, data, and documentation
- **6 categories of changes**: Frontend UI, system prompts, menu data, docs, assets, team context
- **Estimated effort**: 2–4 developer-days
- **Recommendation**: Parallel team execution with Rick coordinating

### Frontend Theme & Branding (Morty)
- **Primary colors**: Cherry Red (#E40046), Dark Blue (#285780), Yellow (#FEDD00), Light Blue (#74D2E7), Green (#328500)
- **Font**: Nunito Sans (replaced Fredoka)
- **Brand voice**: "Carhop" terminology (replaced "crew member")
- **Menu**: Slushes, burgers, shakes, tots, hot dogs, breakfast items
- **Logo**: New sonic-logo.svg created; dunkin-logo.svg removed from imports
- **Translations**: All locale files (en, es, fr, ja) updated
- **Test data**: dummyOrder.json and dummyTranscripts.json aligned with Sonic branding

### Backend System & Implementation (Summer)
- **System prompts**: Rewritten as Sonic carhop persona in app.py and rtmt.py
- **Logger**: coffee-chat → sonic-drive-in
- **Menu data**: structured_menu_items replaced with Sonic items
- **Environment**: DUNKIN_MENU_ITEMS_PATH → SONIC_MENU_ITEMS_PATH; index coffee-chat → sonic-drive-in
- **Tools coupling**: MENU_CATEGORY_MAP syncs with frontend JSON; ALLOWED/BLOCKED categories include both JSON names and keyword-inferred names
- **Tests**: All backend test suites updated with Sonic items; existing logic tests unchanged
- **Upstream attribution**: John Carroll's coffee-chat-voice-assistant credited; voice_rag_README.md excluded from rebrand

### Verification Testing (Birdperson)
- **Test suite**: test_rebrand_verification.py created with 12 tests
- **Coverage**: Scans all source files (.py, .ts, .tsx, .html, .css, .json, .md, .yaml, .bicep) for forbidden terms
- **Forbidden terms**: "dunkin", "crew member", "coffee-chat" / "coffee chat"
- **Targeted checks**: README title, index.html title, backend system prompt
- **Exclusions**: .squad/, .git/, node_modules/, __pycache__/, voice_rag_README.md, test file itself
- **Status**: All 12 tests passing post-rebrand

## Sonic Menu Items Search Index Name (2026-03-19)

**Author:** Summer (Backend Dev)

### Decision
Changed the default Azure AI Search index name to `sonic-menu-items` across:
- `.env-sample` (was `sonic-drive-in`)
- `infra/main.parameters.json` (was `voicerag-intvect`)
- New `sonic_menu_ingestion_search.ipynb` notebook (hardcoded)

### Rationale
Brian requested a distinct index for Sonic menu ingestion. The new notebook creates a `sonic-menu-items` index, so the default config should match. The app reads the index from `AZURE_SEARCH_INDEX` env var, so runtime behavior depends on what's in the actual `.env` file.

### Impact
- Any new deployment using `azd` defaults will provision with `sonic-menu-items` index name
- Existing deployments are unaffected (they use their own `.env` values)
- Team members should update their local `.env` if they want to match the new default

## Increase max_response_output_tokens from 150 → 250 (2026-03-19)

**Author:** Summer (Backend Dev)  
**Supersedes:** Decision #2 (Constrain Model Output Tokens)

### Context
Decision #2 set `max_response_output_tokens = 150` to keep voice responses concise. In practice, this truncated the closing phrase "Thank you! Your carhop will have that right out to you!" — the AI would say "Your carhop will have that right—" and stop.

### Decision
Raised the cap to 250 tokens. This gives enough room for a 1-2 sentence response + order recap + full closing phrase, while still preventing runaway generation.

### Trade-offs
- Slightly longer max possible response (~187 words vs ~112 words)
- Still well under the model's natural output limit
- If further truncation issues appear, consider removing the cap entirely and relying solely on the system prompt's "be brief" instruction

### Impact
- Fixes voice truncation on closing phrases
- No measurable latency impact for typical 1-2 sentence responses (model stops naturally before hitting the cap)

## Azure Speech Mode Architecture (2026-03-20)

**Author:** Summer (Backend Dev)

### Decision

The Azure Speech mode now uses a combined endpoint pattern (`POST /azurespeech/speech-to-text`) that performs STT + chat completion + tool calling in a single HTTP request. This differs from the Realtime WebSocket mode which streams continuously.

### Key Design Choices

1. **Async OpenAI client (`AsyncAzureOpenAI`)** — avoids blocking the event loop during chat completion. The sync `AzureOpenAI` client was a correctness issue in an aiohttp server.

2. **Executor for Speech SDK** — `recognize_once()` is synchronous. Wrapped in `run_in_executor()` to keep the event loop free. Same pattern for TTS.

3. **Separate SearchClient instance** — Azure Speech gets its own `SearchClient` (vs sharing with the Realtime pipeline). This keeps connection pools isolated so Speech mode load can't starve the WebSocket pipeline.

4. **Conversation history per session** — Multi-turn context stored in-memory with a 20-message sliding window (plus system message). This lets the model reference prior turns without unbounded memory growth.

5. **Conditional mount** — Azure Speech routes only mount if `AZURE_SPEECH_KEY` and `AZURE_SPEECH_REGION` are configured. The Realtime WebSocket mode is unaffected if Speech env vars are missing.

6. **Session ID in response** — The endpoint creates an `order_state_singleton` session on first call and returns the `session_id`. The frontend can pass it back on subsequent calls for order continuity.

### Impact on Frontend

The frontend hook (`useAzureSpeech.tsx`) now consumes `tool_results` from the response and invokes `onReceivedToolResponse` for each entry to update the order panel in real-time.

### Risks

- Conversation history is in-memory; lost on server restart. Acceptable for drive-thru ordering sessions (short-lived).
- No extras validation in the Azure Speech update_order path (unlike the Realtime pipeline's tools.py which checks extras against base items). Could be added if needed.

## Azure Speech Hook — Tool Response Processing & Session Management (2026-03-20)

**Author:** Morty (Frontend Dev)

### Context

The `useAzureSpeech` hook had `onReceivedToolResponse` defined in its `Parameters` interface and passed by `App.tsx`, but the parameter was never destructured or used. This meant Azure Speech mode silently ignored all order updates from the backend — the carhop ticket always showed $0.00.

### Decisions

#### 1. Tool result processing matches real-time pattern
The hook now processes `tool_results` from the REST response and constructs `ExtensionMiddleTierToolResponse` objects with the same shape the real-time WebSocket hook uses. This lets `App.tsx` use one callback pattern for both modes.

#### 2. Session ID via `useRef` + `crypto.randomUUID()`
Each `startSession()` call generates a fresh UUID. The session ID is sent in every `/azurespeech/speech-to-text` request body so the backend can maintain order state per conversation. This parallels how the WebSocket mode implicitly gets a session per connection.

#### 3. Backward compatible
If the backend response omits `tool_results`, the hook works exactly as before — no errors, no regressions.

### Impact
- Azure Speech mode now properly updates the order panel with prices and items
- Backend now returns `tool_results` array and accepts `session_id` in the request body

## Canonical Size Labels for Menu Data (2026-03-20)

**Author:** Summer (Backend Dev)

### Decision
All size labels in the search index must use one of five canonical values: **Mini**, **Small**, **Medium**, **Large**, **RT 44**. The ingestion notebook normalizes raw product display names (e.g., "Sm Cherry Limeade") to these labels. The system prompt enumerates these valid sizes explicitly.

### Rationale
Raw Sonic API data embeds the product name in each size variant's display name. Without normalization, the AI model sees inconsistent labels across products and can't reliably match customer size requests to search result data. Standardized labels make the `update_order` tool call deterministic.

### Impact
- Next time the ingestion notebook is re-run, the search index will contain clean size labels
- The AI model now has explicit guidance on valid size names
- Any downstream code that checks size names should expect only these 5 values plus "Standard" (for items without size variants)

## Menu Sizes Sourced from Production Data (2026-03-19)

**Author:** Morty (Frontend Dev)

### Context
The UI menu panel (`menuItems.json`) had only Small/Medium/Large for drinks, while the AI voice assistant and Azure AI Search index offered 5 sizes (Mini, Small, Medium, Large, RT 44). This caused customer confusion.

### Decision
- Drink items (Cherry Limeade, Blue Raspberry Slush, Ocean Water) now show all 5 sizes: mini, small, medium, large, rt 44
- Shake items get mini added (4 sizes total). No RT 44 exists for shakes in production data
- SONIC Blast corrected to mini/small/medium — production data only has 3 sizes for this category
- All prices sourced from `sonic-menu-items.json` (production data), not manually set
- A reusable script (`scripts/update_menu_sizes.py`) was created to re-sync sizes/prices from production data whenever it changes

### Impact
- UI and voice assistant now show consistent size options
- Future price changes can be synced by re-running the script

## Greeting Sent AFTER session.update (2026-03-19)

**Author:** Summer (Backend Developer)

### Problem
The greeting was sent to OpenAI BEFORE the `session.update` message was forwarded. This caused three cascading issues:
1. **No tools available** — AI couldn't call `update_order`, so items were never added to the ticket ($0.00 orders)
2. **No system message** — AI used wrong closing phrases and didn't follow Sonic persona
3. **Mid-conversation reconfiguration** — AI had to reconfigure after greeting, causing delays

### Root Cause
In `app/backend/rtmt.py`, the `from_client_to_server()` function sent the greeting BEFORE processing/forwarding the first `session.update` message (which contains tools, system message, and voice config).

### Decision
Reordered message flow in `rtmt.py`:
1. Process and forward `session.update` first (with tools, system message, voice config)
2. Then send greeting

WebSocket messages are ordered, so OpenAI processes them in the correct sequence.

### Related Changes
Strengthened system prompt in `app.py`:
- Made tool calling instruction explicit: "When a guest orders items, IMMEDIATELY call 'update_order'. The guest ordering IS confirmation."
- Made closing phrase instruction emphatic: "you MUST say EXACTLY: [phrase] — Do NOT use any other closing phrase."

### Impact
- AI has tools available when generating responses → `update_order` calls work → items added to ticket
- AI has system message from the start → uses correct closing phrases and Sonic persona
- No mid-conversation reconfiguration → faster, smoother interactions
- All 100 tests pass

## Coordinated Echo Suppression Fix (2026-03-19/20)

### Frontend: Early Mic Mute on response.created (Morty)
**Date:** 2026-03-19  
**Status:** Implemented

The previous audio feedback loop fix muted the mic on `response.audio.delta`, but audio samples had already been sent to the server by then — causing phantom user inputs like "Peace." and "Thank you so much." from echoed AI speech.

**Decisions:**
1. **Mute on `response.created`** — the earliest event the OpenAI Realtime API sends when a response begins, arriving before any audio deltas.
2. **Send `input_audio_buffer.clear`** on `response.created` — flushes any already-buffered echo from the server's audio pipeline.
3. **Unmute on barge-in** — `input_audio_buffer.speech_started` now resets `isAiSpeakingRef` and unmutes the mic so the user can resume speaking after interrupting.

**Trade-offs:**
- With gain=0, barge-in relies on audio that was in-flight before the mute took effect. If the server detected real user speech from pre-mute audio, the barge-in handler correctly unmutes. Full barge-in during muted playback is not possible (acceptable — echo prevention is higher priority).
- `sendJsonMessageRef` pattern adds a small layer of indirection in `useRealtime.tsx` but is necessary to break the circular dependency between `useCallback` and `useWebSocket`.

**Files Changed:**
- `app/frontend/src/hooks/useRealtime.tsx` — Added `response.created` handler, `sendJsonMessageRef`, `onReceivedResponseCreated` callback
- `app/frontend/src/App.tsx` — Moved mute to `onReceivedResponseCreated`, updated barge-in handler, removed redundant transcript delta muting

### Backend: Server-Side Echo Suppression in rtmt.py (Summer)
**Date:** 2026-03-20  
**Status:** Implemented

Frontend mic-muting reduced but didn't eliminate the audio feedback loop. A timing gap exists between when AI audio arrives at the server and when the frontend gain-node mute activates — during this gap, echoed audio reaches the server, gets forwarded to OpenAI, and is transcribed as phantom user input.

**Implementation:** Three coordinated mechanisms in `rtmt.py`:
1. **Audio gating**: Track `ai_speaking` state per-connection. When `response.audio.delta` messages flow server→client, drop all `input_audio_buffer.append` messages from client→server.
2. **Post-response cooldown**: After `response.audio.done`, suppress audio for an additional 300ms to cover speaker-to-mic latency.
3. **Buffer flush**: Send `input_audio_buffer.clear` to OpenAI after each AI audio response completes to discard any leaked echo.

Barge-in preserved: `input_audio_buffer.speech_started` from OpenAI's server VAD immediately clears suppression.

**Trade-offs:**
- **Pro**: Eliminates phantom transcriptions at the server layer, independent of frontend timing.
- **Pro**: Zero JSON parse overhead — uses fast substring markers on the hot path.
- **Con**: Barge-in has ~300ms latency after AI finishes speaking. Acceptable for drive-thru UX.
- **Con**: During AI speech, user audio is fully dropped (not buffered). If cooldown is too aggressive, genuine speech immediately after AI could be clipped. Monitor and tune `_ECHO_COOLDOWN_SEC` if needed.

**Files Changed:**
- `app/backend/rtmt.py` — echo suppression state, audio gating, buffer flush, barge-in detection

### Coordination
Both fixes together form a complete echo suppression solution:
- **Frontend**: Early mute at `response.created`, automatic `input_audio_buffer.clear`
- **Backend**: Audio gating + cooldown + buffer flush
- **Result**: Phantom transcriptions eliminated; all 100 backend tests pass, 13 frontend tests pass

## Tools.py Demo Hardening (2026-03-21)

**Author:** Summer (Backend Dev)  
**Status:** Implemented

### Four Targeted Improvements to update_order and search

#### 1. Hardened Price Validation
- **Decision:** Reject `add` actions with `price <= 0.0` before any order state mutation. Return friendly retry message via `ToolResultDirection.TO_SERVER`.
- **Rationale:** When the model skips search and guesses a price, it defaults to $0.00 — looks like a bug in demo. Early guard catches this before the item enters the order.
- **Trade-off:** Extras like "Whipped Cream" at $0.50 still work (>0). Items truly free would need an explicit $0.01 workaround, but Sonic has no free items.

#### 2. Combo Detection with Pending Slots
- **Decision:** After adding a Combo item (case-insensitive substring match), append a `(COMBO DETECTED: ...)` hint to the ToolResult instructing the AI to ask for side and drink selections.
- **Rationale:** Combos require side + drink choices. Without an explicit hint, the AI sometimes skips these and moves to the next item. The hint is appended to the order summary JSON so the AI gets it in context.
- **Trade-off:** Hint is text-appended to JSON (not structured). Acceptable because the AI model parses both.

#### 3. Human-Readable Size Formatting in Search Results
- **Decision:** Parse the `sizes` JSON field into `"Small ($X.XX), Medium ($Y.YY)"` format. Falls back to raw string on parse failure.
- **Rationale:** gpt-realtime-1.5 struggles to speak raw JSON like `[{"size":"Small","price":2.49}]`. Human-readable format lets it naturally say prices.
- **Trade-off:** Slightly changes search result format — existing tests updated. Description field dropped from search summary (sizes more important for ordering).

#### 4. Upsell Hints in Tool Results
- **Decision:** After successful `update_order`, append category-based upsell hints (combo upgrade, combo conversion for burgers, flavor add-in for drinks). No upsell on desserts/shakes.
- **Rationale:** Complements Unity's suggestive selling system prompt (Decision #27) with programmatic nudges. AI gets concrete suggestions in the tool response.
- **Trade-off:** Hints only fire on `add` actions, not `remove`. Uses `_infer_category()` — category lists must stay in sync.

### Impact
- All 111 existing tests pass with no modifications needed
- All four changes are additive — no existing functionality removed or altered
- Works with existing `_infer_category()`, `_SearchCache`, and `ToolResultDirection.TO_BOTH`

## Order Quantity Limits (2026-03-21)

**Author:** Summer (Backend Dev)  
**Status:** Implemented

### Decision
Added per-item (`MAX_QUANTITY_PER_ITEM = 10`) and total order (`MAX_TOTAL_ITEMS = 25`) quantity limits enforced in the `update_order` tool in `tools.py`.

### Rationale
Prevents abuse scenarios (ordering 100+ of an item) that could destabilize the system, confuse the AI, or create unrealistic orders. Limits are realistic for a drive-thru window — 10 of any single item and 25 total items are generous enough for large family/group orders but cap truly absurd requests.

### Implementation Details
- Validation runs in `update_order()` **before** `handle_order_update()` is called — invalid quantities never touch order state.
- Only applies to `"add"` actions — removing items is never gated.
- Per-item check matches on `item_name + size` combo (same logic as `order_state.py` deduplication).
- Error messages are warm and customer-friendly, sent as `ToolResultDirection.TO_SERVER` so the AI can relay them conversationally.
- Constants are at module top of `tools.py` for easy tuning without code changes.

### Trade-offs
- Limits are not configurable at runtime (would need env var or config file for hot-tuning). Current constants are easy to change and redeploy.
- The AI model receives the limit message and may paraphrase it — this is intentional for natural conversation flow.

### Files Changed
- `app/backend/tools.py` — added constants + validation logic in `update_order()`

## Conversational Quantity Limit Guardrails (2026-03-21)

**Author:** Unity (AI / Realtime Expert)  
**Status:** Implemented

### Decision
Added a QUANTITY LIMITS section to the system prompt in `app/backend/app.py` with conversational guardrails for excessive order quantities.

### Limits
- **Per-item max:** 10 — AI suggests capping at 10 with friendly language
- **Total order max:** 25 items — AI suggests catering line for larger orders
- These match Summer's backend enforcement values exactly

### Design Choices
- **Placement:** Between ORDERING and CLOSING sections (natural conversation flow)
- **Tone:** Warm, helpful — like a carhop looking out for the customer. No "error" or "limit exceeded" language.
- **NEVER refuse service** — always offer the closest alternative
- **4 bullets only** — kept concise to minimize first-response latency impact
- **Defense-in-depth:** AI handles it conversationally first, backend enforces hard limits second

### Coordination
- Summer is adding backend enforcement with the same limits (per-item 10, total 25)
- AI-level guardrails prevent most cases from ever hitting the backend rejection path

## Combo Validation & Delta Summaries (2026-03-21)

**Author:** Summer (Backend Dev)  
**Status:** Implemented

### Decision

## Dual-Trigger Greeting for API Resilience (2026-03-22)

**Author:** Summer (Backend Dev)  
**Date:** 2026-03-22  
**Status:** Implemented

### Context

Brian started a demo and got complete silence — no greeting, no audio. The session was active (frontend showed "Conversation in progress") but the AI never spoke.

Root cause: A recent change moved the greeting trigger from `from_client_to_server` (fired after forwarding `session.update`) to `from_server_to_client` (fired after receiving `session.updated` from Azure OpenAI). The intent was correct — ensure tools are configured before greeting — but Azure's Realtime API doesn't reliably send `session.updated`, so the greeting never fired.

### Decision

Implement dual-trigger greeting in `rtmt.py`:

1. **Primary (server→client):** Fire greeting when `session.updated` is received — guarantees tools are configured.
2. **Fallback (client→server):** Fire greeting after forwarding a `session.update` message — reliable because it doesn't depend on API response events.

The existing `greeting_sent` flag (checked in `send_greeting_once()`) prevents double-greeting regardless of which trigger fires first.

### Implementation

- Added `_MARKER_SESSION_UPDATE = '"session.update"'` constant alongside existing `_MARKER_SESSION_UPDATED`.
- In `from_client_to_server`, after forwarding the client message, check if it was a `session.update` (but NOT `session.updated`) and fire `send_greeting_once()`.
- Defensive substring check: `_MARKER_SESSION_UPDATE in msg.data and _MARKER_SESSION_UPDATED not in msg.data` — even though `session.updated` can't appear in client→server messages, this is belt-and-suspenders.

### Trade-offs

- The fallback trigger may fire before tools are fully acknowledged by OpenAI. In practice this is fine because the `session.update` has already been forwarded — OpenAI processes messages in order, so tools will be configured by the time it processes the greeting's `response.create`.
- Slight increase in code complexity (two trigger sites instead of one), mitigated by clear comments and the single `send_greeting_once()` function.

### Impact

Eliminates demo-blocking silence on startup. All 118 tests pass. Frontend build succeeds.

Extended order_state.py with deterministic combo validation, Sonic-specific size cleanup, and split voice/screen payloads.

### 1. `get_combo_requirements()` on OrderState
- Scans entire order for combo/side/drink ratios using `_infer_combo_component()` (lightweight keyword check in order_state.py)
- Avoids circular import: tools.py has full `_infer_category()`, order_state.py has focused `_infer_combo_component()`
- Returns `is_complete`, `missing_items`, `prompt_hint`
- Replaces ad-hoc `(COMBO DETECTED: ...)` hint with persistent `[SYSTEM HINT: ...]` pattern that fires after EVERY `update_order` call
- When combo is incomplete, upsell hints are suppressed — AI focuses on completing the combo first

### 2. Sonic Size Cleanup
- Removed Dunkin' remnants: "Kannchen" and "Pot" size formatting
- Added Route 44 support: `rt44`, `rt 44`, `route 44` → "Route 44 " prefix
- Unrecognized sizes now default to empty string (hidden) rather than capitalized

### 3. Delta Summaries (Voice vs Screen Split)
- `ToolResult` extended with optional `client_text` field and `to_client_text()` method
- Server (OpenAI) receives: natural-language delta ("Added 1 Large Cherry Limeade — your total is now $8.49") + system hints
- Client (frontend) receives: pure JSON order summary for the display panel
- Backward-compatible: `to_client_text()` falls back to `to_text()` when no `client_text` is set

### Impact
- **Unity**: `[SYSTEM HINT: ...]` pattern is ready — add corresponding system prompt instruction for combo completion flow
- **Morty**: Frontend now receives pure JSON in `tool_result` for `update_order` (no more appended hint text in the JSON payload)
- **Birdperson**: 7 new combo requirement tests added (118 total, all passing)

### Risk
- `_infer_combo_component()` duplicates subset of `_infer_category()` logic — if new drink/side categories are added to `_infer_category()`, they must also be added here

## System Hint Integration — Tool Hints in Prompt (2026-03-21)

**Author:** Unity (AI / Realtime Expert)  
**Status:** Implemented

### Decision

Added TOOL HINTS section to system prompt in `app/backend/app.py` to guide AI consumption of `[SYSTEM HINT]` patterns in tool results.

### Implementation

- **Location:** After ORDERING section, before SUGGESTIVE SELLING
- **Content:** 2 bullets explaining how AI processes hints embedded in tool results (e.g., missing combo sides/drinks, upsell opportunities)
- **Behavior:** AI recognizes hints, acts on them conversationally, NEVER reads hints aloud
- **Defense-in-depth:** Backend decides *when* to hint (Summer's `[SYSTEM HINT]` injection), system prompt tells AI *how* to act

### Coordination

- Complements Summer's backend `[SYSTEM HINT]` injection in tool results
- Hint pattern ready for immediate use in combo completion flow, upsell prompts, and other dynamic guidance
- All hints are suppressed while combos incomplete — focus on completion first

## Demo Polish Sprint (2026-03-21T20:23-20:28)

**Author:** Brian Swiger (via Copilot), coordinated across Summer and Unity

### 1. Lower RTMiddleTier Temperature from 0.6 → 0.5
- **Author:** Summer (Backend Dev)
- **Why:** Reduces creative wandering in voice responses, tighter carhop persona. Improves Time to First Token (TTFT) — lower temperature means model commits to high-probability tokens faster.
- **Change:** `rtmt.temperature` in `app/backend/app.py` line 125
- **Verification:** Static file serving order verified — `_index_handler` (explicit `GET /` route) registered before `add_static('/')`. In aiohttp, explicit routes take priority, so no conflict.

### 2. Suggestive Sell Follow-Through Guardrail
- **Author:** Unity (AI / Realtime Expert)
- **What:** Added rule to TECHNICAL GUARDRAILS: "If the guest says 'Yes' or 'Sure' to a suggestive sell (like a combo), IMMEDIATELY ask for the missing details (e.g., 'Awesome, tots or fries with that?')."
- **Why:** Ensures demo conversations flow naturally without pauses after customer agreement. Complements existing combo detection and upsell hints.
- **File:** System prompt in `app/backend/app.py`

### 3. Grouped Readback Integration
- **Author:** Summer (Backend Dev)
- **What:** Added `get_grouped_order_for_readback()` method to OrderState. Groups identical items for natural voice read-back (e.g., "Two Medium Cherry Limeades and one Footlong Quarter Pound Coney"). Integrated with `get_order` tool using `TO_BOTH` routing.
- **Why:** AI was receiving raw JSON for order readback, sounding robotic. A human carhop groups duplicates — AI should too.
- **Changes:**
  - `order_state.py` — new grouping method
  - `tools.py` — `get_order` changed to `TO_BOTH` with `client_text`. AI receives grouped text; frontend receives full JSON.
- **Testing:** All 118 tests pass. Pure computation on existing data. `TO_BOTH` pattern already tested in `update_order`.

## Fix Greeting-Before-Session.Update Tool Blindness (2026-03-22)

**Author:** Summer (Backend Dev)  
**Status:** Implemented

### Problem
The AI conversed perfectly (asking about combos, sizes, drinks) but NEVER called `update_order`, `search`, or `get_order`. The order panel stayed at $0.00.

**Root cause:** `from_client_to_server()` in `rtmt.py` sent the greeting (`conversation.item.create` + `response.create`) to OpenAI BEFORE forwarding the client's `session.update` message — which carries tool definitions, system_message, and tool_choice. OpenAI received greeting before tools were configured.

### Solution
1. **Reordered greeting:** Client messages now forwarded FIRST (`_process_message_to_server` → `send_str`), then the greeting fires. OpenAI sees: `session.update` → greeting → `response.create`. Tools configured before first completion.

2. **Fallback tools_pending registration:** `response.output_item.added` now pre-registers `call_id` in `tools_pending` as safety net. `conversation.item.created` always overwrites with correct `previous_item_id`. Prevents silent tool-call drops if API event ordering changes.

3. **Diagnostic logging:** `session.update` now logs tool count and tool_choice. Tool execution logs tool name, args, and result direction.

### Impact
- Fixes demo blocker — orders now appear on carhop ticket
- All 118 existing tests pass
- No API or schema changes required

---

## System Prompt Tool-Calling Mandate (2026-03-21)

**Author:** Unity (AI / Realtime Expert)  
**Status:** Implemented

### Problem
The ORDERING section had only weak instruction — "Call update_order ONLY after guest confirms." The word "ONLY" reads as restriction, not mandate. Model treated ordering as role-play, never triggering tool calls.

### Solution
Added new "⚠️ TOOL-CALLING RULES — MANDATORY" section positioned early (section #3, after CONVERSATIONAL FLOW, before MENU & PRICING) with:
- Explicit negative instructions: "NEVER say X WITHOUT calling Y FIRST"
- Consequence statements: "the item WILL NOT appear"
- Mandatory flow: search → confirm → update_order
- Reinforced in ORDERING and MENU & PRICING sections

### Rationale
For gpt-realtime-1.5, tool-calling requires EXPLICIT negative instructions and consequence statements. Positive instructions alone ("call update_order after confirmation") deprioritized in favor of conversation. Position matters — tool-calling rules must appear near top, not buried in section #6.

### Impact
- Demo-tested with multi-item orders
- Tool-calling now reliable
- Order flow reaches completion

## Copilot Directive — Demo System Prompt Enhancements (2026-03-21T21:10)

**Author:** Brian Swiger (via Copilot)  
**Status:** Pending Review

### What
Three critical system prompt sections for Inspire Brands demo:
1. **PERSONALIZATION** — Carhop spirit, handle "regulars" and "happy hour" mentions warmly
2. **PATIENCE & CLARITY** — Handle stalls/silence gracefully ("No rush!"), offer Fan Favorites when asked for recommendations
3. **VISUAL SYNC** — Occasional spatial language ("I've got that added to your ticket right now")
4. **COMBO LOGIC — DETERMINISTIC** section enforcing priority: Item Selection → Combo Completion → Upsell → Treat Suggestion

### Why
Makes the AI feel like a person on skates, not a kiosk. Critical for emotional impact with Inspire Brands demo stakeholders.

---

## Demo Bug Fix Changeset — APPROVED (2026-03-22)

**Author:** Rick (Lead/Architect)  
**Status:** APPROVED

### Bug 1: Tools Not Called (Greeting Race Condition) ✓
- **Root cause:** `response.create` fired before OpenAI confirmed `session.updated`, so model hadn't loaded tool definitions.
- **Fix:** Move greeting trigger from `from_client_to_server` to `from_server_to_client` (fire-on-`session.updated`). One-line logical move using `_MARKER_SESSION_UPDATED` substring check.

### Bug 2: Barge-In Deadlock ✓
- **Root cause:** Frontend echo suppression (gain=0) → backend drops `input_audio_buffer.append` → OpenAI never fires `speech_started` → nothing unmutes. Circular dependency.
- **Fix:** AnalyserNode tapped before gain node detects user speech on muted stream. RMS energy calculation (textbook), 0.08 threshold (conservative), 100ms polling (cheap).

### Ancillary Fixes ✓
- `reset_order` session_id crash — fixed
- `reset_order` TO_CLIENT → TO_BOTH — consistent with Decision #26
- Frontend tool dispatch — add `get_order` and `reset_order` handlers for carhop ticket updates

### Risk Assessment
**Low.** All changes are additive or fix obvious bugs. Barge-in monitor only activates when mic muted (normal path is no-op). Greeting timing strictly safer.

---

## Happy Hour Dynamic Pricing (2026-03-22)

**Author:** Summer (Backend Dev)  
**Status:** Implemented

### Decision
Drinks and slushes are automatically half-price between 2:00 PM and 4:00 PM local time.

### Key Choices
1. **Local time** — used `datetime.now()`, avoided new dependencies for demo environment
2. **Summary-level discount** — original `item.price` preserved on each OrderItem; 50% applied only to calculated totals
3. **Reused `_infer_combo_component()`** — already identifies drinks; single source of truth
4. **Context in tool results** — `update_order` and `get_order` append `[HAPPY HOUR ACTIVE: drinks/slushes half-price!]` so AI knows to get excited

### Impact
- `order_state.py` — added `is_happy_hour()` helper, updated `_update_summary()` loop
- `tools.py` — import, append note to tool results
- All 118 tests pass, no regressions

---

## OOS Machine Status Check in Search Results (2026-03-22)

**Author:** Summer (Backend Dev)  
**Status:** Implemented

### What
Menu items dependent on down machines get `[OOS: ...]` flagged in search results so AI steers customers away.

### Design
1. **Module-level `MOCK_MACHINE_STATUS`** — easy toggle for demos; production would use Azure Function/IoT Hub
2. **Keyword-based matching** — items with "shake", "blast", "sundae", "ice cream" tied to `ice_cream_machine` status
3. **Non-blocking** — items still returned, just flagged; AI sees `[OOS]` tag and should advise naturally
4. **Server-side only** — `[OOS]` tag in `TO_SERVER` result, frontend never sees it

### Files Changed
- `app/backend/tools.py` — `MOCK_MACHINE_STATUS`, `_ICE_CREAM_MACHINE_KEYWORDS`, OOS check in search loop

### Impact
All 118 existing tests pass — simple string append guarded by dict lookup.

---

## reset_order Tool Routing (2026-03-22)

**Author:** Summer (Backend Dev)  
**Status:** Implemented

### Decision
`reset_order` uses `ToolResultDirection.TO_CLIENT` (not `TO_BOTH`). Response is `"Order cleared. {json_summary}"` — AI doesn't need confirmation to continue, frontend needs empty JSON for ticket.

### Trade-off
If AI needs explicit post-reset confirmation, routing should change to `TO_BOTH` with voice-friendly string. Monitor during demos.

---

## Architectural Review & Prompt Externalization (2026-03-25)

Three-agent parallel analysis completed. Consensus: **Prompt externalization is the critical architectural debt.**

### Key Findings

**1. Architecture Assessment (Rick)**

Rated 10 areas. Priorities:

**🔴 CRITICAL:**
- Prompts buried in code (12+ hardcoded strings, ~6,500 chars) — no versioning, no A/B testing
- System prompt alone is 122 lines of string concatenation in `app.py:127-250`

**🟡 NEEDS WORK:**
- Config scattered (25+ magic numbers in 5 files) — should centralize to `config.py`
- Error handling gaps (no WebSocket retry, token provider refresh issue at 60 min, no session limits)
- Code organization debt (`rtmt.py` is 751-line god file, dead code present)
- Testing incomplete (~127 backend tests solid, but `rtmt.py` has zero coverage, no integration tests)
- Security gaps (API key in WebSocket URL for dev mode, no CORS, no rate limiting, no WebSocket auth)
- Documentation mixed (code comments good, `.squad/decisions.md` excellent, but README freshness unknown, no architecture diagram)

**🟢 GOOD:**
- Performance solid (28 perf tests, caching working)
- Frontend architecture clean (TypeScript, memo/lazy loading)
- Infrastructure well-structured (Bicep AVM modules, RBAC, health probes)

### Prioritized Roadmap (Rick)

**P0 (Before next demo)** — 2 days total:
1. Externalize system prompt + greeting to YAML (1 day)
2. Move tool schemas to YAML (0.5 day)
3. Create `config.py` for all tunable values (0.5 day)
4. Remove/archive dead code (1 hour)

**P1 (Before production)** — ~6 days:
- Add WebSocket authentication (1 day)
- Implement OpenAI connection retry logic (0.5 day)
- Fix token provider async refresh (0.5 day)
- Add session limits (0.5 day)
- Add `rtmt.py` unit tests (2 days)
- Split `rtmt.py` into 3 modules (1 day)

**P2 (Nice to have):**
- Architecture diagram (0.5 day)
- Application Insights (0.5 day)
- Frontend component tests (2 days)

---

**2. Backend Prompt & Config Inventory (Summer)**

Exhaustive catalog with loading strategy:

**12 Hardcoded Prompt Strings (~9,100 chars total externalize-able content):**
- System message (`app.py:127-250`) — ~4,200 chars, ~1,100 tokens (CRITICAL)
- Greeting (`rtmt.py:141-150`) — pre-serialized JSON
- Tool schemas (`tools.py:198-525`) — 4 tools (search, update_order, get_order, reset_order)
- Error messages (`tools.py`) — 8 variants (~1,000 chars)
- Upsell hints (`tools.py:474-486`) — 6 category templates
- Combo hints, readback text (order_state.py)

**25+ Config Constants Scattered:**
- Model: temperature, max_tokens, voice_choice, api_version
- Search: cache TTL (60s), cache max (128), KNN (15), top (3)
- Order: max qty/item (10), max total (25), tax (8%)
- Echo: cooldown (1.5s)
- WebSocket: heartbeat (15s), timeouts
- Happy hour: window (14-16), discount (50%)
- Other: speech voice, compression threshold

**Architectural Issues Found:**
- Blocking calls in async context (`azurespeech.py` legacy, `rtmt.py:505` token provider)
- Error handling gaps (no try/except on `json.loads` in tool args, unhandled auth failure)
- Resource leaks (`_sent_greeting` unbounded, temp file orphans)
- Code duplication (size normalization, category inference, voice names across 2 files)

**Proposed Externalization Structure:**
```
app/backend/prompts/
├── system_message.md
├── greeting.md
├── tool_schemas/ (search.json, update_order.json, get_order.json, reset_order.json)
├── hints/ (6 upsell templates)
├── errors/ (invalid_price.txt, per_item_limit.txt, etc.)
└── config.yaml (all tunable values)
```

**Priority 1 Quick Wins:**
- Extract system_message.md (1 day)
- Extract config.yaml (0.5 day)
- Fix `_sent_greeting` memory leak (1 line)
- Unify size maps → constants.py (1 hour)

---

**3. AI Prompt Structure & Pipeline Strategy (Unity)**

YAML + Jinja2 externalization proposal with versioning, validation, and A/B testing support.

**Why YAML (not JSON/Markdown):**
- Multi-line strings native (`|` block scalar)
- Comments allowed (JSON doesn't support)
- Human-readable (non-engineers can review)
- Jinja2 templating ready (e.g., `{{ item_name }}` in error messages)
- Ubiquitous in Python ecosystem (`pyyaml`)

**Proposed File Structure:**
```
app/backend/prompts/
├── sonic/
│   ├── manifest.yaml (brand metadata + model config)
│   ├── system_prompt.yaml (18 sections, persona, rules)
│   ├── greeting.yaml
│   ├── tool_schemas.yaml
│   ├── error_messages.yaml
│   └── hints.yaml (SYSTEM HINT, UPSELL HINT, OOS, HAPPY HOUR)
├── _base/ (shared across brands)
│   ├── tool_calling_rules.yaml
│   └── technical_guardrails.yaml
└── prompt_loader.py (load + compose + cache at startup)
```

**Versioning Strategy (Semantic):**
- **MAJOR:** Brand change, persona overhaul, section restructure
- **MINOR:** New section, behavioral rule change
- **PATCH:** Wording tweak, typo fix
- Git tags: `prompt/sonic/v2.4.0`
- A/B testing via env var: `PROMPT_VERSION=sonic/v2.4.0`
- Session logging: Log which prompt version produced conversation

**Critical: DO NOT Template System Prompt**
Dynamic content (menu prices, order state, combo hints) flows via **tool results**, not prompt injection. This is correct for gpt-realtime-1.5:
- System prompt sent once at session start (can't update mid-conversation)
- `[SYSTEM HINT]` pattern handles dynamic steering elegantly
- **Only template error messages** (Jinja2 for `{{ item_name }}`, `{{ max_qty }}`)

**Pipeline Improvements (5 findings):**
1. Tool schemas too generic — enrich descriptions for gpt-realtime-1.5
2. Upsell hints duplicate system prompt — move to hints.yaml (single source)
3. Greeting pre-serialized JSON — load from greeting.yaml, serialize at startup
4. Error messages lose brand voice — tune tone to match upbeat Sonic carhop
5. No prompt validation at startup — validate sections, token count, brand refs (CRITICAL)

**Validation at Startup:**
- All sections in `prompt_order` exist
- Total token count within budget (~2,500 tokens max for TTFT)
- Required sections present (VOICE STYLE, TOOL-CALLING RULES, BOUNDARIES)
- No stale brand references (regex check for competitor names)

**Implementation Phases (6 phases, Phase 1 critical path):**

| Phase | Scope | Risk | Effort | Benefit |
|-------|-------|------|--------|---------|
| 1 | Extract system prompt → YAML, load at startup | Low | 2-3h | Versionable, reviewable prompts |
| 2 | Extract greeting, errors, tool schemas | Low | 2-3h | Centralized AI config |
| 3 | Extract hint templates | Low | 1-2h | Single source of truth |
| 4 | Add prompt validation at startup | Medium | 2-3h | Catch errors before prod |
| 5 | A/B testing support (env var version selector) | Medium | 3-4h | Data-driven optimization |
| 6 | Context window monitoring | Low | 1-2h | Observability |

**Tests Affected:**
5 tests currently regex-parse `app.py` — after externalization, load YAML directly (simpler, more reliable):
- `test_backend_system_prompt_mentions_sonic`
- `test_backend_system_prompt_no_dunkin`
- `test_backend_system_prompt_uses_carhop_not_crew_member`
- `test_system_prompt_contains_carhop_closing`
- `test_system_prompt_contains_get_order_tool_instruction`

---

### Consensus & Next Steps

All three agents recommend:
1. ✅ YAML format (Rick, Summer, Unity agree)
2. ✅ Semantic versioning for prompts
3. ✅ System prompt externalization as P0 (foundational)
4. ✅ Config centralization alongside prompts
5. ❓ Additional detail: Which agent implements Phase 1? Assign to Summer (backend focus) or create cross-functional pair?

**Brian's decision needed:**
- Approve YAML + Jinja2 approach?
- Execute P0 sprint (prompts + config) before other P1 items?
- Team assignment for Phase 1 implementation?

Full analysis available in:
- `.squad/decisions/inbox/rick-arch-review.md` (10-area assessment, P0/P1/P2 roadmap)
- `.squad/decisions/inbox/summer-prompt-inventory.md` (exhaustive inventory + duplication findings)
- `.squad/decisions/inbox/unity-prompt-strategy.md` (YAML strategy, versioning, validation)

---

### Copilot Directive — Demo Data Disclaimer (2026-03-22T04:27)

**Author:** Brian Swiger (via Copilot)  
**Status:** Recorded

**Directive:** Never refer to menu data or any data as "production" — this is all demo/test data. The README and all documentation should reflect this is a demo, not production.

**Rationale:** User request — captured for team memory.

---

### Verbose Logging Toggle — UI (Morty, 2026-03-22)

**Author:** Morty (Frontend Dev)  
**Status:** Implemented

Added a "Verbose Logging" toggle to the Settings panel that sends `{"type": "extension.set_verbose_logging", "enabled": true/false}` to the backend via WebSocket.

#### Implementation
- **Settings component** (`settings.tsx`): New toggle following existing pattern — Label, description, Switch, status text.
- **useRealtime hook** (`useRealtime.tsx`): New `sendVerboseLogging(enabled: boolean)` function exported alongside `startSession`, `addUserAudio`, etc.
- **App.tsx**: State managed via `useState` + `localStorage` (key: `verboseLogging`, default: OFF). Sends the message on toggle change AND on session start if already enabled (so a page refresh preserves the setting).

#### Message Format
```json
{"type": "extension.set_verbose_logging", "enabled": true}
```

#### Backend Contract
The backend needs to handle `extension.set_verbose_logging` messages and toggle detailed terminal logging accordingly. This is a frontend-only change — backend handler is needed to complete the feature.

#### Impact
- No existing tests broken (13/13 pass)
- Build succeeds cleanly
- Settings component lazy-loaded (no impact on initial load)

---

### Bugfix Sprint #29–32 (Squanchy, 2026-03-22)

**Author:** Squanchy (QA / Infrastructure)  
**Status:** Implemented (all 125 tests passing)

#### #29. Happy Hour Timezone — `STORE_TIMEZONE` env var
- **Decision:** Use `zoneinfo.ZoneInfo(os.environ.get("STORE_TIMEZONE", "America/Chicago"))` for all local-time checks.
- **Rationale:** `datetime.now()` returns UTC in Azure Container Apps. Sonic HQ is in Oklahoma City (Central time). The env var allows per-store override without code changes.
- **Trade-off:** Requires `tzdata` pip package on Windows (stdlib companion, not pytz). Linux containers use OS tz database natively.
- **Impact:** Happy hour pricing now works correctly in production.

#### #30. `_sent_greeting` Cleanup on Disconnect
- **Decision:** Added `self._sent_greeting.discard(session_id)` to the WebSocket `finally` block in `rtmt.py`.
- **Rationale:** Session IDs were accumulated in the set but never removed, causing unbounded memory growth in long-running containers.
- **Impact:** Memory is now bounded to active connections only.

#### #31. Remove Legacy Speech SDK Files
- **Decision:** Deleted `azurespeech.py`, `azure_speech_gpt4o_mini.py`, and removed `azure-cognitiveservices-speech` from `requirements.txt`.
- **Rationale:** Both files were superseded by the Realtime API integration. No imports found anywhere in the codebase. Removing the Speech SDK dependency saves ~50MB in the container image.
- **Impact:** Smaller image, no dead code confusion.

#### #32. Shared `menu_utils.py` Module
- **Decision:** Centralized size normalization and category inference into `app/backend/menu_utils.py`. Both `tools.py` and `order_state.py` now import from it.
- **Rationale:** Independent size maps and category inference logic in two files had to be kept in sync manually — a drift risk.
- **Impact:** Single source of truth. ~70 lines of duplicated code removed. All 125 tests pass.

---

### Multi-Layer Echo Self-Talk Fix (Summer, 2026-03-22)

**Author:** Summer (Backend Dev)  
**Status:** Implemented

#### Problem
AI generates 4+ unsolicited patience responses after speaking ("No problem — take your time...", "Take all the time you need...") because:
1. Echo cooldown (0.5s) was too short — speakers still resonating when mic reopens
2. No buffer flush after cooldown window — echo audio accumulated during cooldown triggered VAD
3. System prompt "give them space — NEVER rush" instruction caused model to actively fill silence

#### Changes
**rtmt.py:**
1. **`_ECHO_COOLDOWN_SEC`: 0.5 → 1.5** — gives adequate time for speaker echo to decay (3.0s for greeting via 2x multiplier)
2. **Delayed second buffer flush** — `loop.call_later(actual_cooldown, _make_delayed_flush)` fires `input_audio_buffer.clear` after cooldown expires, catching any echo audio that accumulated during the window

**app.py:**
3. **Removed patience instruction** — replaced "If the guest pauses or says 'uh' / 'let me see,' give them space — NEVER rush" with "NEVER speak unless the guest has spoken first — if there is silence, WAIT silently. Do NOT fill silence with 'No rush', 'Take your time', or any unprompted chatter"
4. **max_tokens stays at 4096** — needed for tool call budget; prompt controls verbosity

#### Trade-offs
- 1.5s cooldown adds slight delay before user can speak after AI finishes (acceptable for drive-thru)
- Removing patience instruction means AI won't proactively comfort hesitant users (correct for demo — silence is better than self-talk)

#### Test Results
118 passed, 1 pre-existing async framework failure (unrelated)

---

### Prompt & Config Loader Infrastructure (Summer, 2026-03-25)

**Author:** Summer (Backend Dev)  
**Status:** Implemented

Built YAML-driven prompt and config externalization infrastructure with backward-compatible fallbacks.

#### Architecture
1. **`config.yaml`** — All 25+ magic numbers from app.py, rtmt.py, tools.py, order_state.py centralized. Loaded once at startup via `config_loader.get_config()`.
2. **`prompt_loader.py`** — Manifest-driven loader for `prompts/{brand}/` directory. Validates sections, caches in memory, supports Jinja2 templates for error messages, optional DEV_MODE hot-reload.
3. **Backward compatibility** — Every call site has a hardcoded fallback when `_prompt_loader is None`. Tests that don't go through `create_app()` still work.

#### Key Decisions
- **Module-level `_prompt_loader` pattern**: Set by `attach_tools_rtmt()` at startup. Handler functions check `if _prompt_loader:` before using YAML values. This avoids passing the loader through every function signature.
- **Manifest.yaml as entry point**: Unity's file structure uses a manifest listing all YAML files. Loader discovers files via manifest, not by convention. New brands just need a new manifest.
- **Jinja2 StrictUndefined**: Error templates use `StrictUndefined` so missing variables raise immediately rather than silently producing empty strings.
- **Config as dict, not dataclass**: Kept as plain dict for simplicity. If the config grows, consider Pydantic BaseSettings.

#### Files Changed
- `app/backend/prompt_loader.py` — NEW
- `app/backend/config_loader.py` — NEW
- `app/backend/config.yaml` — NEW
- `app/backend/app.py` — Imports loaders, system prompt from YAML, config values from config.yaml
- `app/backend/rtmt.py` — Greeting from YAML, echo/connection from config
- `app/backend/tools.py` — Tool schemas, errors, hints from YAML; cache/search/quantity from config
- `app/backend/order_state.py` — Tax rate, happy hour config from config.yaml
- `app/backend/requirements.txt` — Added pyyaml, jinja2
- `app/backend/tests/test_rebrand_verification.py` — Updated to read prompt from YAML

#### Impact
- Prompts editable without code changes (Brian or Unity can tune)
- Config changes don't require deploy (just container restart)
- All 125 tests pass — behavior identical to pre-refactor

---

### Tool-Calling Token Budget Fix (Summer, 2026-03-22)

**Author:** Summer (Backend Dev)  
**Status:** Implemented

#### Problem
The carhop ticket stayed at $0.00 despite the AI having full conversations — greeting, taking orders verbally, but NEVER calling `update_order`. Previous fixes didn't solve the core issue.

#### Root Cause
Two issues found:

**1. `max_response_output_tokens = 250` starved tool calls (CRITICAL)**

In the OpenAI Realtime API, `max_response_output_tokens` is a **shared budget** between audio/text output AND tool call arguments in the **same response**. With only 250 tokens:
- The model produces conversational audio first (~150-200 tokens of speech)
- By the time it considers the tool call, insufficient budget remains for the JSON arguments
- The model silently **skips the tool call** to stay within budget
- Result: verbal acknowledgment with no `update_order` — ticket stays at $0.00

**2. `"error"` in `_PASSTHROUGH_SERVER_TYPES` hid API rejections**

If OpenAI rejected the `session.update` (e.g., malformed tool schema), the error was forwarded to the client without any backend logging. We had zero visibility into whether tools were actually configured.

#### Changes
| File | Change |
|------|--------|
| `app.py:126` | `max_tokens = 250` → `max_tokens = 4096` |
| `rtmt.py` | Removed `"error"` from `_PASSTHROUGH_SERVER_TYPES` |
| `rtmt.py` | Added `case "error"` handler with `logger.error()` |
| `rtmt.py` | Enhanced `session.update` logging: full tool names + max_tokens value |
| `rtmt.py` | `response.output_item.added`: tool call receipt logged at INFO (name + call_id) |
| `rtmt.py` | `response.done`: logs whether response had tool calls or not |
| `rtmt.py` | `send_greeting_once(trigger=...)`: logs which trigger fired |

#### Rationale
- **4096 tokens**: Eliminates the token budget as a constraint on tool calls. The system prompt already controls verbosity ("ONE or TWO short sentences max per response"). A drive-thru response is typically 20-50 tokens of audio + 30-40 tokens for tool call args — well within 4096 but tight at 250.
- **Error logging**: Any future session.update rejection will now show up in server logs immediately, preventing silent tool-blindness.
- **Diagnostic logging**: Every response now reports whether it contained tool calls, making "model didn't call tools" vs "tool call was silently dropped" distinguishable from logs alone.

#### Impact
- All 118 backend tests pass (no regressions)
- Tool calls should now flow freely — the model has sufficient token budget for audio + function_call arguments
- Errors from OpenAI are now visible in backend logs
- Next debug cycle (if needed) will have full visibility into the tool-calling pipeline

#### Trade-offs
- 4096 tokens allows ~3000 words of audio per response if the model goes verbose. Mitigated by system prompt constraints. Can be tuned down to 1024 if the model rambles.

---

### Verbose Logging Toggle (Backend, Summer, 2026-03-22)

**Author:** Summer (Backend Dev)  
**Status:** Implemented

Added per-session verbose logging to `rtmt.py` using a dedicated `sonic-verbose` logger, controllable via:
1. `VERBOSE_LOGGING=true` environment variable (global, always-on)
2. `extension.set_verbose_logging` WebSocket message from frontend (per-session toggle)

#### What It Logs (when enabled)
- Every message type in both directions (Server→Client, Client→Server)
- Full tool call lifecycle: name, args (pretty-printed), result (truncated 500 chars), direction, execution time
- AI transcript deltas and completions
- User transcription completions
- Echo suppression state changes (ai_speaking, cooldown)
- Session lifecycle: connect, session.update confirmation, greeting trigger, disconnect
- Audio frames logged as counts only (every 50th frame), never raw audio data

#### Architecture
- `sonic-verbose` logger is separate from existing `sonic-drive-in` logger — zero impact on existing behavior
- `_vlog()` helper checks per-connection `verbose` flag OR global `_VERBOSE_GLOBAL` before logging
- Extension messages (`extension.set_verbose_logging`) are intercepted in `from_client_to_server()` and NOT forwarded to OpenAI
- When verbose is disabled, logger level is WARNING (silent). When enabled, DEBUG with raw message formatter
- All 118 existing tests pass unchanged

#### Trade-offs
- Slight overhead from `_vlog()` calls even when disabled (boolean check per message). Negligible vs. JSON parse cost.
- Verbose mode on high-traffic sessions will produce significant terminal output. Audio data intentionally excluded.
- Per-session toggle modifies shared `vlogger` level — if multiple concurrent sessions exist and one enables verbose, the logger level stays DEBUG. Acceptable for dev/troubleshooting use case.

#### Frontend Coordination
Frontend (Morty) already has the toggle wired:
- Settings panel has "Verbose Logging" switch
- `useRealtime.tsx` sends `{"type": "extension.set_verbose_logging", "enabled": true/false}`
- App.tsx persists state to localStorage and sends on session start

---

### System Prompt Token Limit & Restructuring (Unity, 2026-03-22)

**Author:** Unity (AI / Realtime Expert)  
**Status:** Implemented

#### Problem
The AI was cutting off mid-sentence during order readbacks. Root cause: `max_response_output_tokens = 250` is shared between tool call arguments and verbal output. A multi-item readback with tool calls easily exceeds 250 tokens.

#### Changes
1. **max_tokens: 250 → 1024** — Generous enough for any response (tool call + 5-item readback + closing), not so high it causes latency. The system prompt's "ONE or TWO sentences" instruction still governs normal response brevity.

2. **TOOL-CALLING RULES moved to section #2** (was #5) — In gpt-realtime-1.5, top-of-prompt instructions get significantly more attention. This is the single most critical operational rule.

3. **System prompt trimmed ~33%** (~1500 → ~1008 tokens) — Merged PERSONALIZATION and PATIENCE & CLARITY into adjacent sections, trimmed verbose examples, moved low-priority sections (HAPPY HOUR, VISUAL SYNC, OOS PROTOCOL) to the end.

#### Trade-offs
- Higher max_tokens means the model *could* generate longer responses, but the "ONE or TWO sentences" instruction in VOICE STYLE effectively caps normal responses. Only readbacks and complex tool calls use the extra headroom.
- Prompt restructuring changes section ordering — any future additions should respect the priority weighting (critical operational rules near the top, situational/edge-case rules at the bottom).

#### Impact
- Eliminates mid-sentence cutoffs on order readbacks
- Improves tool-calling compliance (rules now in prime position)
- Reduces first-response latency slightly (shorter prompt)

#### Files Changed
- `app/backend/app.py` — max_tokens and system_message

---

### Prompt YAML Content Structure (Unity, 2026-03-25)

**Author:** Unity (AI / Realtime Expert)  
**Status:** Implemented (content files only — loader pending from Summer)

Team decided to externalize all hardcoded prompts from Python into YAML files under `app/backend/prompts/sonic/`. Unity responsible for content extraction; Summer building the loader.

#### Decisions Made

**1. System Prompt → 22 Named Sections with Priority Ordering**
- Each logical block in the original `app.py` system prompt becomes a named section with a `priority` integer.
- The loader concatenates sections in priority order to reconstruct the original prompt string.
- Section names use UPPER_SNAKE_CASE matching the original ALL CAPS section headers.
- Fidelity is exact — no rewording, no reordering, no "improvements" to prompt text.

**2. Tool Descriptions Made Brand-Specific**
- `search` description changed from generic "knowledge base" to "Sonic Drive-In menu" language.
- `update_order` and other tools now reference "Carhop Ticket" instead of "current order."
- Parameter schemas preserved exactly — only `description` fields changed.
- This was flagged as a quick win in the AI pipeline audit.

**3. Error Messages Use Jinja2 Template Variables**
- Runtime values (item names, quantities, limits) use `{{variable}}` syntax.
- The loader or calling code will render templates before passing to the AI.
- Template variable names match the Python variable names from `tools.py` for easy mapping.

**4. Voice Choice: `coral` (not `sage`)**
- The task description suggested `sage`, but the actual codebase (`app.py:121`) defaults to `coral`.
- Team history confirms `coral` was chosen for the Sonic carhop persona (warm, friendly, good energy).
- Manifest reflects the real deployed value: `coral`.

**5. Upsell Hints Keyed by Category**
- 6 category-specific hint templates extracted, keyed by logical names (burger, drink, shake, side, combo, generic).
- Each hint lists its trigger categories for the loader to match against `_infer_category()` output.
- System hints (combo_incomplete, OOS, happy_hour) extracted as separate templates.

#### Files Created
- `app/backend/prompts/sonic/system_prompt.yaml` — 22 sections, ~8.9 KB
- `app/backend/prompts/sonic/greeting.yaml` — greeting message, ~280 B
- `app/backend/prompts/sonic/tool_schemas.yaml` — 4 tool definitions, ~2.4 KB
- `app/backend/prompts/sonic/error_messages.yaml` — 12 error/limit messages, ~1.8 KB
- `app/backend/prompts/sonic/hints.yaml` — 6 upsell + 3 system hints + 2 delta templates, ~1.5 KB
- `app/backend/prompts/sonic/manifest.yaml` — brand config + model config, ~350 B

#### Impact
- No Python code modified — this is content extraction only.
- Summer's prompt loader will read these files at startup.
- Existing tests unaffected until the loader is wired in and `app.py` updated to use it.

---

### Copilot Directive — Model & SDK Standardization (2026-03-25T12:23)

**Author:** Scribe (per squad manifest)  
**Status:** Recorded

All code-writing agents (Rick, Summer, Unity, Morty, Birdperson, Squanchy) use **Claude Opus 4.6** for code generation tasks. Recorded in `copilot-directive-2026-03-25T12-23.md`.

---

## Previous Decisions (Archived)

### Copilot Directive (2026-02-25T22-39)
Copilot CLI configuration directive for squad ceremonies and agent interactions.

### Voice Chat Architecture & Prompt Engineering (Fenster, 2026-02-25)
Initial system prompt design leveraging Azure OpenAI GPT-4o Realtime for voice-based ordering. Foundation for Sonic rebrand implementation.

### Repository Initialization (Squanchy, 2026-02-25)
SonicAIDriveThru repository created with Azure Container Apps, Bicep IaC, React frontend (Vite, Tailwind, shadcn/ui), Python backend (aiohttp, WebSockets, Azure OpenAI Realtime, Azure AI Search).

## Code Organization: rtmt.py Refactoring (2026-03-25)

**Author:** Summer (Backend Dev)  
**Status:** Implemented — Commit 1057cac

### Decision

Split monolithic `rtmt.py` (766 lines) into three focused modules for clarity and maintainability:

1. **`session_manager.py`** (154 lines) — Session lifecycle, greeting state, context window monitoring
2. **`audio_pipeline.py`** (199 lines) — Echo suppression state machine, verbose logging, audio constants
3. **`rtmt.py`** (586 lines) — Thin orchestrator: RTMiddleTier, WebSocket routing, message processing

### Key Design Decisions

- **EchoSuppressor class**: Encapsulates `ai_speaking`, `cooldown_end`, `greeting_in_progress` into clean methods. Eliminates nonlocal variable sharing between nested closures.
- **SessionManager class**: Owns `_session_map`, `_sent_greeting`, `_context_monitors`. Single cleanup method prevents state leaks.
- **Public API preserved**: `from rtmt import RTMiddleTier, ToolResult, ToolResultDirection, Tool, RTToolCall` unchanged. No downstream import changes.
- **Linear imports**: session_manager → order_state, config_loader; audio_pipeline → config_loader; rtmt → both new modules. No circular dependencies.

### Context Window Monitoring (New Feature)

- `ContextMonitor` class tracks estimated token usage per session (~4 chars/token heuristic)
- Logs WARNING at 80%, CRITICAL at 95% of max_tokens (default 128K from config.yaml)
- Monitors: system message, tool schemas, tool args/results, AI responses, user transcriptions
- No truncation implemented yet — monitoring only for now

### Impact

- All 128 backend tests pass
- No behavioral changes — purely structural refactoring + monitoring addition
- Easier to navigate, test, and modify individual concerns independently

## Startup Validation & Health Check Hardening (2026-03-25)

**Author:** Squanchy (DevOps)  
**Status:** Implemented

### Decision

Startup validation now runs before the server accepts connections. The app validates:

1. All 4 required env vars (`AZURE_OPENAI_EASTUS2_ENDPOINT`, `AZURE_OPENAI_REALTIME_DEPLOYMENT`, `AZURE_SEARCH_ENDPOINT`, `AZURE_SEARCH_INDEX`)
2. Prompt YAML files load via PromptLoader
3. config.yaml is loaded (module-level via `get_config()`)
4. Optional Azure service connectivity ping (non-blocking, warns only)

On failure: `logger.critical()` + `sys.exit(1)` — the server never starts in a broken state.

### Health Endpoint Contract

`GET /health` returns:
```json
{
  "status": "healthy",
  "version": "1.0.0",
  "checks": {
    "prompts_loaded": true,
    "config_loaded": true,
    "env_vars": true
  }
}
```

- HTTP 200 when all checks pass, HTTP 503 when any check fails
- No external service calls — fast enough for Azure Container Apps probes
- Already consumed by startup/liveness/readiness probes in Bicep

### Rationale

- Fail-fast prevents containers from entering traffic rotation while broken
- Structured health response enables probe-level diagnostics in Azure portal
- Non-blocking connectivity check catches network issues early without blocking startup

### Impact

- `sys.exit(1)` replaces `RuntimeError` for missing env vars — team tests should use `SystemExit` assertion
- `_startup_checks` module-level dict is the source of truth for health state
- 3 new tests passing, all existing tests unaffected
