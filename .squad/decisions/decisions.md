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

## Previous Decisions (Archived)

### Copilot Directive (2026-02-25T22-39)
Copilot CLI configuration directive for squad ceremonies and agent interactions.

### Voice Chat Architecture & Prompt Engineering (Fenster, 2026-02-25)
Initial system prompt design leveraging Azure OpenAI GPT-4o Realtime for voice-based ordering. Foundation for Sonic rebrand implementation.

### Repository Initialization (Squanchy, 2026-02-25)
SonicAIDriveThru repository created with Azure Container Apps, Bicep IaC, React frontend (Vite, Tailwind, shadcn/ui), Python backend (aiohttp, WebSockets, Azure OpenAI Realtime).
