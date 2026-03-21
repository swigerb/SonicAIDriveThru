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

## Previous Decisions (Archived)

### Copilot Directive (2026-02-25T22-39)
Copilot CLI configuration directive for squad ceremonies and agent interactions.

### Voice Chat Architecture & Prompt Engineering (Fenster, 2026-02-25)
Initial system prompt design leveraging Azure OpenAI GPT-4o Realtime for voice-based ordering. Foundation for Sonic rebrand implementation.

### Repository Initialization (Squanchy, 2026-02-25)
SonicAIDriveThru repository created with Azure Container Apps, Bicep IaC, React frontend (Vite, Tailwind, shadcn/ui), Python backend (aiohttp, WebSockets, Azure OpenAI Realtime).
