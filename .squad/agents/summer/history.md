# Project Context

- **Owner:** Brian Swiger
- **Project:** Sonic AI Drive-Thru Voice Assistant — AI-powered voice ordering experience using Azure OpenAI GPT-4o Realtime, Azure AI Search, and Azure Container Apps
- **Stack:** Python backend (aiohttp, WebSockets, Azure OpenAI Realtime, Azure AI Search, Azure Speech SDK), React/TypeScript frontend (Vite, Tailwind CSS, shadcn/ui), Bicep IaC, Docker, azd CLI
- **Created:** 2026-03-19

## Core Context

**Rebrand & Setup (2026-03-19)**: Completed Sonic rebrand from Dunkin across all backend systems. Key learning: `_infer_category()` in tools.py couples frontend menuItems.json to backend category inference. Team coordinated: Rick scope, Morty frontend, Summer backend (69 tests), Birdperson verification (12 tests).

**Performance Hardening (2026-03-19)**: Major backend perf pass: (1) `_tools_pending` moved from shared to per-connection (race condition). (2) `_PASSTHROUGH_TYPES` frozenset for O(1) hot-path bypass. (3) `__slots__` on classes, module-level search cache, compression middleware, gzip on HTTP. All 100 tests pass.

**Menu Integration (2026-03-19)**: Created Sonic menu ingestion notebook (recursive category traversal, size variant resolution, 172 of 1334 products). Key lesson: `.ipynb` files require programmatic Python JSON editing.

**Critical Ordering Bugs (2026-03-19)**: Fixed three issues: (1) `.env` pointed to wrong search index. (2) System prompt didn't extract prices from `sizes` JSON. (3) `max_tokens=150` truncated closing phrases → raised to 250. All 100 tests pass.

## Archive (2026-03-20 through 2026-03-22)

Series of debugging and feature work across demo readiness, debugging sprint, and architecture review:
- **Echo Suppression (2026-03-20):** rtmt.py audio gating with ai_speaking flag, 300ms→500ms cooldown, buffer clear. Refined 2026-03-21 with fast substring detection.
- **System Prompt (2026-03-21):** Converted to bulleted format with ALL CAPS emphasis. Explicit anti-hallucination grounding.
- **Order Routing (2026-03-21):** Changed `update_order` from TO_CLIENT to TO_BOTH, fixing dead silence on valid orders.
- **Tools Hardening (2026-03-21):** Price validation ($0 rejection), combo detection, human-readable size formatting, upsell hints.
- **Demo Polish (2026-03-21):** Temperature 0.6→0.5, added `get_grouped_order_for_readback()`. All 118 tests passing.
- **Menu Data (2026-03-22):** Audited menuItems.json, synced 50 Sonic items, dynamic MENU_CATEGORY_MAP inference.
- **Tool-Call Fix (2026-03-22):** Reordered WebSocket messages so session.update (with tools) arrives before greeting.
- **Greeting Fix (2026-03-22):** Rewritten imperative greeting prompt. Pre-set ai_speaking before response.create. Increased cooldown 500ms→1.5s.
- **Happy Hour (2026-03-22):** Drinks/slushes half-price 2:00–4:00 PM via STORE_TIMEZONE (fixed UTC bug).
- **OOS Machine (2026-03-22):** Ice cream [OOS] tag in search results. Non-blocking steering.
- **reset_order Tool (2026-03-22):** Big Red Button with TO_CLIENT routing.
- **Verbose Logging (2026-03-22):** Dedicated `sonic-verbose` logger, per-session toggles, message lifecycle logging.
- **Combo Pivot (2026-03-22):** Fixed state overwriting on combo + sides/drinks combo absorption with session counters.
- **Item Customization (2026-03-22):** End-to-end mods ("no lettuce", "extra ketchup") with natural voice readback.
- **Prompt Externalization (2026-03-25):** Full YAML-driven infrastructure (prompt_loader.py, config_loader.py, config.yaml). Wired into app.py, rtmt.py, tools.py with backward-compatible fallbacks. All 125 tests pass.
- **Architecture Review (2026-03-25):** Fixed 4 bugs: happy hour timezone, `_sent_greeting` memory leak, dead code, size/category deduplication in menu_utils.py.



<!-- Append new learnings below. Each entry is something lasting about the project. -->

## Learnings — Current (Phase 7)

### 2026-08-06: Ruff Lint Cleanup — 70 Errors → 0
- **Baseline:** 70 ruff errors across backend + scripts (F401×29, I001×13, F541×6, UP015×6, UP006×5, F841×5, UP017×2, UP035×2, E402×1, UP041×1). 354 tests passing.
- **Auto-fixed (66):** `ruff check . --fix` handled all F401 (unused imports), I001 (import sorting), F541 (empty f-strings), UP006 (Dict→dict annotations), UP015 (redundant open modes), UP017 (timezone.utc→UTC alias), UP035 (typing.Dict→dict), UP041 (TimeoutError alias). Zero test regressions.
- **Hand-fixed (4):** F841×5 — three `sid = create_session()` in test_rtmt.py were side-effect calls (removed assignment); two `response = upload_documents()` in notebooks were side-effect calls (renamed to `_`). None were latent bugs. E402×1 — `from collections import Counter` mid-cell in notebook, suppressed with `# noqa: E402, I001` (legitimate placement near usage in 150-line data-processing cell).
- **No global ignores added.** All suppressions are targeted per-line noqa with justification.
- **Key Insight:** The test_rebrand_verification.py branding guard was safe — its f-strings without placeholders were just `f"message"` strings that ruff correctly converted to plain strings; the test's scanning constants were not affected.
- **Test Results:** 354/354 passing after all changes. Zero regressions.

### 2026-08-06: Dependency Update Sprint — Security + Version Bumps
- **Security-Critical Upgrades:** aiohttp 3.10.11→3.14.3 (33+ CVEs including PYSEC-2026-2102, PYSEC-2026-3545, PYSEC-2026-237), python-dotenv 1.0.1→1.2.2 (PYSEC-2026-2270), cryptography ≥50.0.0 added as floor pin (9 CVEs in transitive 46.0.0). pip-audit now reports zero known vulnerabilities.
- **Major Bumps Taken:** azure-search-documents 11.6.0→12.0.0 (verified: breaking changes only affect beta API users, not our `SearchClient`/`VectorizableTextQuery` surface in tools.py), azure-identity 1.19.0→1.25.3, azure-storage-blob 12.24.0→12.30.0, cffi 1.17.1→2.1.1 (required by cryptography ≥50), rich 13.9.4→15.0.0 (only used in setup_intvect.py).
- **Deliberately Skipped:** openai stays at 1.109.1 (latest 1.x) — not imported anywhere in backend code (app uses raw aiohttp WebSockets to Azure OpenAI Realtime), but scripts/notebooks in `scripts/` use `from openai import AzureOpenAI` and would break on 2.x. gunicorn stays at 23.0.0 — no CVEs, UNIX-only server, 26.0.0 is a 3-major jump with insufficient changelog visibility.
- **pyproject.toml Fix:** `target-version` corrected from `"py311"` to `"py312"` — Dockerfile uses `python:3.12-slim`, devcontainer uses `python:1-3.12-bookworm`.
- **Key Insight:** cffi is pinned in requirements.txt but is purely a transitive dependency (not imported in any backend source). cryptography ≥50.0.0 requires cffi ≥2.0, so the two pins must move together. msal constrains cryptography to <51.
- **Test Results:** 354/354 passing before AND after changes. Zero regressions.

## Learnings — Current (Phase 6)

### 2026-03-26: Combo Conversion Sprint — Mod-Stripping + Size Prompting + Regression Tests
- **Part 1 — Mod-Stripping Bug Fix:** When AI sends a combo with mods already embedded (e.g., `SuperSONIC® Bacon Double Cheeseburger Combo (Pickles Only)`), the `combo_base` retained the parenthesized mods while `existing_base` stripped them via `.split("(")[0]`. This mismatch prevented standalone-to-combo removal, causing both items to appear on the ticket. Fix: Added `combo_base = combo_base[:combo_base.find("(")].strip()` after the initial normalization, mirroring how `existing_base` already strips mods. Two-line change in `order_state.py` line 128-129. Key Insight: The combo conversion comparison must be symmetric — both sides need identical normalization. The AI may send combo names with or without mods depending on context, so the backend must handle both.
- **Part 2 — Combo Size Prompting (Unity):** Scoped MENU_AND_PRICING "default to MEDIUM" rule to standalone items only. Added explicit COMBO SIZE PROMPTING block requiring AI to ask for side and drink sizes during combo completion. Updated SUGGESTIVE_SELLING upsell instructions. Three surgical edits to system_prompt.yaml — all 337 tests pass.
- **Part 3 — Regression Tests (Birdperson):** Wrote 7 new tests to `test_order_state.py` covering: (1) combo with mods, standalone without → removed, (2) no-mods regression, (3) different mods on standalone vs combo → stripped, removed, (4) mod carry-forward, (5) multiple standalones selective removal, (6) quantity>1 decrement, (7) ® symbol normalization. All 354 tests passing.

### 2026-03-25: Phase 6 — Critical Combo Conversion Bug Fix
- **Combo Conversion Bug (Demo Blocker):** Fixed double-charging bug where converting a standalone burger to a combo resulted in BOTH the standalone ($6.59) AND the combo ($10.19) in the order, plus separate side ($2.79) and drink ($2.49). Root cause was two missing behaviors in `handle_order_update()`: (1) No auto-removal of the standalone entree when the combo version was added. (2) No post-combo absorption — sides/drinks added AFTER a combo were not recognized as combo components. Fix: `order_state.py` now auto-removes matching standalone entrees on combo add (with mod carry-over, e.g., "(Pickles Only)" transfers to combo), and absorbs post-combo sides/drinks into unfilled combo slots. `tools.py` updated to use return value from `handle_order_update()` for accurate delta text ("included with your combo" / "Upgraded to combo"). System prompt COMBO_PIVOT_RULES updated to reflect automatic backend handling. 9 new tests added, 1 existing test updated. All 346 tests pass.
- **Key Insight:** The combo absorption code only ran when a combo was added (absorbing pre-existing sides/drinks). The common real-world flow is: burger first → combo conversion → side → drink. This requires BOTH entree replacement AND reverse absorption (sides/drinks added after the combo). The code approach is more reliable than prompt instructions — the AI might forget to call remove, but the backend always catches it.

## Learnings — Current (Phase 5)

### 2026-03-25: Phase 5 — RTMT + Tool Calling Test Coverage
- **Test Coverage Sprint (Birdperson):** Two new test files (141 tests) covering the two largest zero-coverage gaps: (1) `test_rtmt.py` (75 tests) — WebSocket lifecycle, SessionManager creation/cleanup/concurrency/greeting/idle-timeout, ContextMonitor thresholds, EchoSuppressor state machine (audio delta/done/cooldown/barge-in/greeting suppression), TYPE_RE regex, pre-serialized messages, ToolResult/Tool/RTToolCall value objects, HMAC token create/validate, RTMiddleTier init/attach/config, message processing (session.update injection, passthrough audio, session.created stripping, tool execution with TO_BOTH routing, error logging, malformed JSON), WebSocket handler (origin rejection, token rejection). (2) `test_tool_calling.py` (66 tests) — search pipeline (formatting, sizes, empty results, errors, caching with TTL/eviction/clear/case-insensitive), OOS annotations, order CRUD (add/remove/quantity limits per-item 10/total 25, incremental), get_order/reset_order, tax calculation, upsell hints (burger→combo, drink→addon, side→drink, combo→upgrade), combo validation, menu_utils normalize_size/infer_category, extras validation, edge cases (special chars, duplicate items, empty cart). Total suite: 337/337 passing (196 pre-existing + 141 new). All Azure/OpenAI calls mocked at boundaries.
- **Flagged Issue (Pre-existing):** `INVALID_MODS` referenced in `tools.py:112` but never defined — would raise `NameError` if any item with parenthesized customizations hit `validate_customization()`. Noted for future review.
- **EchoSuppressor Async:** `on_audio_done()` uses `asyncio.ensure_future()` internally — tests wrapped with `asyncio.run()` for proper event loop context. No issues.

## Learnings — Current (Phase 4 — Archived)

### 2026-03-25: Phase 4 — Demo-Safe Security
- **Token Provider Async Refresh (Task 1):** Replaced blocking `self._token_provider()` call in `_forward_messages` with background refresh loop (`_refresh_token_loop`) that proactively refreshes the Azure AD token every 5 minutes via `run_in_executor`. Cached token served to new connections. Startup warm-up still synchronous (fine for one-time init). Background task starts on app startup, cancels on shutdown.
- **Session/Connection Limits (Task 2):** Added `active_session_count`, `can_accept_session()`, and idle-timeout tracking to `SessionManager`. Max 10 concurrent sessions, 5-min idle timeout — both configurable in `config.yaml` under `security:`. Over-limit connections get a friendly JSON error (`"Server is busy"`) and clean close. Idle checker runs every 60s as a background task.
- **Origin Validation (Task 3):** Added origin check in `_websocket_handler` before WebSocket accept. Same-origin always allowed (no Origin header or matching Host). Cross-origin allowed via `security.allowed_origins` list. Rejected origins logged at WARNING. Default: same-origin only.
- **HMAC Session Token (Task 4):** `create_hmac_token()` and `validate_hmac_token()` utilities in rtmt.py. `GET /api/auth/session` endpoint in app.py returns 15-min tokens. `os.urandom(32)` secret generated per app startup. Auth disabled by default (`require_session_token: false`) — zero demo impact until explicitly enabled.
- **Frontend Token Wiring (Task 5):** `useRealtime.tsx` fetches `/api/auth/session` on mount, appends `?token=...` to WebSocket URL. Graceful fallback if endpoint unavailable (null token = no param). Token refresh on 401 close. No behavior change when backend doesn't require tokens.
- **All 195 tests pass.** One pre-existing flaky perf test (`test_search_formatting_empty_results`, 18ms > 10ms threshold) occasionally fails due to timing sensitivity — unrelated to security changes.

## Learnings — Archive (2026-03-19 through 2026-03-25)

Detailed technical learnings from demo readiness, debugging, and prompt externalization sprints archived here for reference.


- **max_response_output_tokens budgets tool calls AND audio**: In the OpenAI Realtime API, `max_response_output_tokens` is shared between audio/text output AND tool call arguments in the same response. With 250 tokens, the model generates audio first (streaming left-to-right), consuming most of the budget, then has insufficient tokens for tool call JSON — so it silently skips the tool call. Set to 4096 to eliminate this constraint; the system prompt already controls verbosity ("ONE or TWO short sentences max").
- **Silent API error passthrough was a critical diagnostic gap**: `"error"` was in `_PASSTHROUGH_SERVER_TYPES`, meaning OpenAI errors (e.g., rejected session.update with malformed tool schemas) were forwarded to the client without any backend logging. Moved error handling into `_process_message_to_client` match/case with `logger.error()`. Always ensure error-class messages go through the full processing path.
- **response.done diagnostic logging**: Added INFO-level logging in `response.done` to report whether each response contained tool calls or only audio/text. Without this, there's no way to distinguish "model didn't try to call tools" from "tool call was silently dropped." Critical for diagnosing tool-calling regressions.
- **Echo cooldown 0.5s too short for mid-conversation**: After AI finishes speaking, speakers still resonate and mic picks up tail-end echo. 0.5s cooldown wasn't enough — OpenAI's VAD interprets the residual audio as a new speech turn, triggering a self-talk loop. 1.5s cooldown (3.0s for greeting) provides adequate margin. The delayed second buffer flush (`loop.call_later`) catches echo audio that accumulates DURING the cooldown window — the immediate flush at `response.audio.done` only clears what's already there.
- **System prompt patience instructions cause self-talk**: "If the guest pauses, give them space" was interpreted by the model as permission to generate unsolicited patience responses ("No rush!", "Take your time!"). In a voice system with echo, silence-after-echo looks like a pause → model fills it → echo repeats → infinite loop. Replace with explicit "NEVER speak unless the guest has spoken first" to break the cycle.
- **Delayed buffer flush pattern**: `loop.call_later()` can't call coroutines directly. Use `asyncio.ensure_future(coro)` inside a regular callback. Default arg `tws=target_ws` captures the websocket reference at definition time, preventing closure issues if the outer variable changes.
- **Verbose logging architecture**: Dedicated `sonic-verbose` logger (separate from `sonic-drive-in`) with per-session toggle via `extension.set_verbose_logging` WebSocket message + `VERBOSE_LOGGING` env var for always-on. Extension messages intercepted in `from_client_to_server()` and NOT forwarded to OpenAI. The `_vlog()` helper checks per-connection `verbose` flag OR global `_VERBOSE_GLOBAL`. Audio data never logged (floods terminal) — only frame counts and message types. Tool calls get full lifecycle logging with args, result (truncated to 500 chars), direction, and execution time.
- **Per-connection file handler lifecycle**: `session_file_handler` is a per-connection `logging.FileHandler` tracked via `nonlocal` in `from_client_to_server()`. Attached to `vlogger` when enabled, removed+closed in the `finally` block on disconnect. Uses `line_buffering=True` via `stream.reconfigure()` for immediate flush. The `_LOGS_DIR` is `pathlib.Path(__file__).parent / "logs"` — relative to rtmt.py, not CWD. `_VERBOSE_LOG_FILE_GLOBAL` env var attaches a module-level handler at import time (separate from per-connection handlers).
- **Search index ingestion architecture**: Three notebooks in `scripts/`: (1) `sonic_menu_ingestion_search.ipynb` — reads from `sonic-menu-items.json` (1334 products, nested Sonic API format), the original production ingestion. (2) `menu_ingestion_search_json.ipynb` — reads from `menuItems.json` (flat 50-item format), simpler and correct for current use. (3) `menu_ingestion_search_pdf.ipynb` — PDF-based ingestion (not relevant). The JSON notebook is the one to use going forward — it reads `menuItems.json`, generates 3072-dim embeddings, and uploads in batches of 15 to the index named in `AZURE_SEARCH_INDEX` env var. The `structured_menu_items` file at repo root is a reference snapshot — the notebook reads directly from `menuItems.json`, not from it.
- **rtmt.py Code Organization (Phase 3)**: Broke 766-line god file into 3 focused modules: `session_manager.py` (154 lines — SessionManager class for session lifecycle, greeting state, ContextMonitor for token tracking), `audio_pipeline.py` (199 lines — EchoSuppressor class, verbose logging infrastructure, audio constants/markers), and `rtmt.py` (586 lines — thin orchestrator, RTMiddleTier, WebSocket routing, message processing). Public API unchanged — `from rtmt import RTMiddleTier, ToolResult, ToolResultDirection, Tool, RTToolCall` still works. No circular imports. EchoSuppressor encapsulates the ai_speaking/cooldown_end/greeting_in_progress state machine with clean methods (should_suppress_audio, on_audio_delta, on_audio_done, on_speech_started, on_barge_in). SessionManager owns _session_map, _sent_greeting, and _context_monitors dicts — single point of cleanup in cleanup_session().
- **Context Window Monitoring**: Added ContextMonitor class in session_manager.py. Tracks estimated token usage per session using ~4 chars/token heuristic. Logs WARNING at 80% and CRITICAL at 95% of configurable max_tokens (128K default). Tracks: system message, tool schemas, tool call args/results, AI response content, user transcriptions, greeting text. Config in config.yaml under `context:` key. No truncation — monitoring only. Warns once per threshold per session (no spam).
