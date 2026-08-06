# Project Context

- **Owner:** Brian Swiger
- **Project:** Sonic AI Drive-Thru Voice Assistant — AI-powered voice ordering experience using Azure OpenAI GPT-4o Realtime, Azure AI Search, and Azure Container Apps
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

### 2026-03-25: Startup Validation & Health Check Hardening (Phase 2/3)
- **Startup Validation:** Implemented fail-fast startup checks in `app.py` before server accepts connections. Validates 4 required env vars (`AZURE_OPENAI_EASTUS2_ENDPOINT`, `AZURE_OPENAI_REALTIME_DEPLOYMENT`, `AZURE_SEARCH_ENDPOINT`, `AZURE_SEARCH_INDEX`), prompt YAML loading via PromptLoader, config.yaml (module-level via `get_config()`). On failure: `logger.critical()` + `sys.exit(1)` — container never starts broken. On success: `✅ Startup validation passed` log message.
- **Optional Service Connectivity Check:** Non-blocking 5s timeout ping of Azure OpenAI + Azure Search. Warns only (logs but doesn't fail startup). Catches network/DNS issues early in container environments.
- **Health Endpoint (GET /health):** Returns structured JSON: `{ "status": "healthy|unhealthy", "version": "1.0.0", "checks": { "prompts_loaded": true, "config_loaded": true, "env_vars": true } }`. HTTP 200 when all checks pass, 503 when any fail. No external service calls — <10ms latency suitable for Azure Container Apps probes. Already wired in Bicep startup/liveness/readiness probes; no infrastructure changes needed.
- **Module-level Truth:** `_startup_checks` module-level dict is single source of truth for health state — atomic updates, no locks needed.
- **Testing:** Added 3 new tests for health endpoint. All existing tests unaffected. Team should use `SystemExit` assertion for env var validation tests (replaces old `RuntimeError`).
- **Coordination:** Integrated with Summer's parallel code refactoring (rtmt.py split into session_manager + audio_pipeline) without conflicts. Both changes ready for single git commit.

### 2026-08-06: Stale Dunkin Branding Cleanup & Roster Population
- **Squad state rebrand**: Updated `team.md` (line 3 tagline + Project Context description) and all 10 agent `history.md` Project lines (rick, morty, summer, birdperson, squanchy, unity, fenster, hockney, keaton, mcmanus) from stale Dunkin branding to "Sonic AI Drive-Thru Voice Assistant". Preserved role-specific suffixes on each agent line.
- **Preserved historical records**: Left rebrand history intact in `decisions/decisions.md` (6 entries), `rick/history.md` line 10 (rebrand scope), `summer/history.md` line 10 (rebrand completion), `birdperson/history.md` lines 18-19 (verification tests), and `squanchy/history.md` lines 14-15 (fork provenance).
- **Roster populated**: Filled `.squad/roster.md` template placeholders with real data from `team.md` — owner, stack, description, created date, and full 8-member roster (Rick/Morty/Summer/Birdperson/Squanchy/Unity + Scribe/Ralph). Preserved Coding Agent section, `copilot-auto-assign` comment, and Capabilities block.
- **Notebook outputs cleared**: Stripped all cached cell outputs and reset `execution_count` to null in `scripts/menu_ingestion_search_json.ipynb` via Python `json` module. Removed ~110 Dunkin hits (stale pip install paths leaking `c:\users\brswig\source\repos\dunkinvoicechat\...`) and old Dunkin menu data outputs. Source cells verified clean — zero Dunkin references. Notebook remains valid JSON with 1-space indentation preserved.
- **Verification**: `squad doctor` passes (11/11). Final `grep -rin dunkin .squad scripts` returns only the preserved historical records.

### 2026-08-06: Replace Integrated Vectorization with Client-Side Embedding Ingestion
- **Problem**: `azd up` hard-failed at postprovision because `setup_intvect.py` called `os.scandir("data")` on a nonexistent directory. The integrated vectorization design was also incompatible with the Free search tier's AI-enrichment quota limits.
- **New script** (`app/backend/setup_search_index.py`): Headless, idempotent ingestion that reads `app/frontend/src/data/menuItems.json`, generates 3072-dim embeddings via `text-embedding-3-large` using `DefaultAzureCredential`, creates/updates the search index (with `AzureOpenAIVectorizer` for query-time vectorization), and uploads documents via `merge_or_upload_documents`.
- **Vectorizer decision**: Kept `AZURE_SEARCH_USE_VECTOR_QUERY=true` with an `AzureOpenAIVectorizer` on the index. The search service's managed identity already has `Cognitive Services OpenAI User` on the OpenAI resource (`openAiRoleSearchService` in main.bicep). This enables `VectorizableTextQuery` in `tools.py` without requiring client-side embedding at query time. Free tier supports vectorizers.
- **Field mapping reconciled** (parameters → env → index → tools.py): `AZURE_SEARCH_IDENTIFIER_FIELD=id`, `AZURE_SEARCH_CONTENT_FIELD=description`, `AZURE_SEARCH_TITLE_FIELD=name`, `AZURE_SEARCH_EMBEDDING_FIELD=embedding`, `AZURE_SEARCH_SEMANTIC_CONFIGURATION=menuSemanticConfig`.
- **Postprovision rewired**: `azure.yaml` now calls `setup_search_index.ps1`/`.sh` (which invoke `setup_search_index.py`). `write_env.ps1`/`.sh` still run first.
- **Deleted**: `app/backend/setup_intvect.py`, `scripts/setup_intvect.ps1`, `scripts/setup_intvect.sh` — confirmed only referenced by the old `azure.yaml` hook (now replaced) and a historical note in `.squad/agents/summer/history.md`.
- **Left alone**: Storage account in Bicep (still provisioned, may serve other purposes), `storageRoleSearchService` role assignment (harmless, removal is risky), `AZURE_STORAGE_*` pipeline variables. `README.md`/`DEPLOY.md` unchanged (they describe `scripts/deploy.sh`, not the azd postprovision flow).
- **Validation**: `az bicep build` succeeds (warnings only), `py_compile` clean, 365 tests pass, `ruff check .` clean, all script paths verified on disk.

