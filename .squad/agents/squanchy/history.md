# Project Context

- **Owner:** Brian Swiger
- **Project:** Dunkin Voice Chat Assistant — AI-powered voice ordering experience using Azure OpenAI GPT-4o Realtime, Azure AI Search, and Azure Container Apps
- **Stack:** Python backend (aiohttp, WebSockets, Azure OpenAI Realtime, Azure AI Search, Azure Speech SDK), React/TypeScript frontend (Vite, Tailwind CSS, shadcn/ui), Bicep IaC (infra/), Docker, azd CLI
- **Created:** 2026-03-19

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2026-03-19: Repository Creation
- GitHub repo created at `brswig_microsoft/SonicAIDriveThru` (private — EMU accounts cannot create public repos)
- Forked from `swigerb/dunkin-chat-voice-assistant` as starting codebase (208 files, 26K+ lines)
- Local `.squad/`, `.copilot/`, `.github/` configs preserved over dunkin originals; `.gitignore` and `.gitattributes` merged
- Remote: `https://github.com/brswig_microsoft/SonicAIDriveThru`
- Branch: `main`

### 2026-03-19: Production Performance Hardening
- **Dockerfile**: Reordered layers for optimal caching — npm deps cached separately from source, pip requirements cached before backend copy. Added `--no-cache-dir` and `npm cache clean`. Added HEALTHCHECK on `/health` endpoint. Configured gunicorn with 2 async workers, 120s timeout (WebSocket-friendly), 65s keep-alive (outlasts Azure LB 60s idle), graceful shutdown.
- **Container App Bicep**: Added full health probe suite (startup/liveness/readiness) via parameterized `healthProbePath`. Added HTTP-based auto-scaling rule (20 concurrent requests trigger). Enabled explicit WebSocket transport (`transport: 'http'`). Set max replicas to 5 with min 1 (always-warm).
- **Backend app.py**: Added `/health` JSON endpoint. Made log level configurable via `LOG_LEVEL` env var (defaults to INFO, not DEBUG).
- **Start scripts**: Both `start.ps1` and `start.sh` now accept `--production` / `-Production` flag to skip frontend rebuild and launch gunicorn with production settings.
- Vite outputs to `../backend/static` (resolves to `/backend/static` in Docker build stage) — confirmed path is correct in Dockerfile COPY.
- `.dockerignore` already filters `node_modules`, `__pycache__`, `.env`, and `static/` (rebuilt by multistage build).
- **Performance Audit Orchestration (2026-03-19T13-21)**: Team completed full-stack performance sprint with 5 agents. Rick lead: 8 fixes across JSON parsing, token cap, search params, system prompt, JSON caching, VAD timing, and response filtering. Summer: 10 fixes for race conditions, hot-path fast-returns, search caching, compression, gzip, logging, memory. Morty: 9 fixes for AudioContext reuse, zero-alloc buffers, memoization, lazy loading, vendor chunking. Squanchy: 6 infrastructure fixes for Gunicorn async, health probes, auto-scaling, Docker caching. Birdperson: 28 performance tests validating latency, memory, thread safety, production readiness. All decisions documented in decisions.md. Orchestration logs written per-agent.

### 2026-03-22: Architecture Review Bugfix Sprint (4 fixes)
- **Happy Hour Timezone Bug (order_state.py):** `datetime.now()` returns UTC in Azure Container Apps, breaking the 2-4 PM happy hour window. Fixed with `zoneinfo.ZoneInfo` using `STORE_TIMEZONE` env var (default "America/Chicago"). Added `tzdata` as Windows-only dependency in requirements.txt.
- **`_sent_greeting` Memory Leak (rtmt.py):** Session IDs were added to `self._sent_greeting` set on WebSocket connect but never removed on disconnect. Added `self._sent_greeting.discard(session_id)` in the `finally` cleanup block alongside existing `_session_map` and `order_state` cleanup.
- **Dead Code Removal:** Deleted `azurespeech.py` and `azure_speech_gpt4o_mini.py` — legacy Speech SDK integration superseded by Realtime API. Removed `azure-cognitiveservices-speech==1.38.0` from requirements.txt. No external imports found.
- **Deduplication (menu_utils.py):** Created `app/backend/menu_utils.py` as single source of truth for `SIZE_MAP`, `SIZE_ALIASES`, `normalize_size()`, `infer_category()`, and `MENU_CATEGORY_MAP`. Updated `tools.py` and `order_state.py` to import from it. Removed ~70 lines of duplicated code. All 125 tests pass.

### 2026-03-25: Parallel Three-Agent Sprint — Prompt Externalization & Bug Fixes
- **Coordination:** Summer (prompt_loader.py infrastructure), Unity (prompt YAML content extraction), Squanchy (4 bugfixes) executed in parallel without conflicts. All 125 tests passing throughout.
- **Outcome:** Complete YAML-driven prompt and config externalization infrastructure with backward-compatible fallbacks. 4 architectural bugs fixed. 9 decision inbox items merged into decisions.md. Orchestration logs written (3 per-agent, 1 session log). Ready for git commit of .squad/ changes.
- **Key Coordination Notes:**
  - Summer's `prompt_loader.py` manifest-driven discovery coordinates seamlessly with Unity's YAML structure
  - Squanchy's 4 bugfixes (timezone, memory leak, dead code, deduplication) identified during architecture review, executed without touching loader code
  - All 125 tests pass after integration of Summer's loader + Squanchy's bugfixes; no test changes needed (backward compatibility preserved)
  - Decision inbox merge: 9 items covering echo fix, tool-calling fix, verbose logging, bugfixes, YAML extraction, token limits

### 2026-03-25: Startup Validation & Health Check Endpoint (Phase 3)
- **Startup Validation (app.py):** Added `_validate_startup` flow at the top of `create_app()`. Checks 4 required env vars (`AZURE_OPENAI_EASTUS2_ENDPOINT`, `AZURE_OPENAI_REALTIME_DEPLOYMENT`, `AZURE_SEARCH_ENDPOINT`, `AZURE_SEARCH_INDEX`), validates prompt YAML loading via PromptLoader, confirms config.yaml loaded (at module level via `get_config()`). On failure: logs specific error via `logger.critical()` and exits with code 1 — no broken server starts. On success: logs `✅ Startup validation passed: prompts loaded, config valid, 4/4 env vars set`.
- **Optional Service Connectivity Check:** Added `_check_service_connectivity()` async function that pings Azure OpenAI and Azure Search endpoints with 5s timeout. Non-blocking — logs warnings on failure, never prevents startup. Useful for early detection of network/DNS issues in container environments.
- **Health Check Endpoint (GET /health):** Enhanced from simple `{"status": "healthy"}` to full probe-ready JSON: `{ "status": "healthy|unhealthy", "version": "1.0.0", "checks": { "prompts_loaded": true, "config_loaded": true, "env_vars": true } }`. Returns HTTP 200 when healthy, 503 when unhealthy. No external service calls — fast (<10ms). Used by Azure Container Apps startup/liveness/readiness probes.
- **Bicep Health Probes:** Already configured in `infra/core/host/container-app.bicep` with startup (50s budget), liveness (30s interval), readiness (10s interval) — all pointing to `/health`. No Bicep changes needed.
- **Test Updates:** Added 3 new health endpoint tests (`HealthEndpointTests`), updated `test_performance.py` to expect `SystemExit` instead of `RuntimeError` for missing env vars. All 128 tests pass.
- **Note:** `rtmt.py` has an in-progress refactor by another agent (SyntaxError at line 429, imports `session_manager` and `audio_pipeline`). Tests were validated by temporarily restoring the committed version. The refactoring agent should resolve the `nonlocal` binding issue.
