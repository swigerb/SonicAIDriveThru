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

## Previous Decisions (Archived)

### Copilot Directive (2026-02-25T22-39)
Copilot CLI configuration directive for squad ceremonies and agent interactions.

### Voice Chat Architecture & Prompt Engineering (Fenster, 2026-02-25)
Initial system prompt design leveraging Azure OpenAI GPT-4o Realtime for voice-based ordering. Foundation for Sonic rebrand implementation.

### Repository Initialization (Squanchy, 2026-02-25)
SonicAIDriveThru repository created with Azure Container Apps, Bicep IaC, React frontend (Vite, Tailwind, shadcn/ui), Python backend (aiohttp, WebSockets, Azure OpenAI Realtime, Azure AI Search).
