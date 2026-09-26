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

### 2026-08-06: Declarative EasyAuth (Entra ID) for Container App
- **Problem**: Entra ID auth was configured live via `az` CLI. Running `azd up` would silently overwrite the Container App without the auth config, exposing the metered Azure OpenAI Realtime WebSocket endpoint.
- **New module** (`infra/core/security/container-app-auth.bicep`): Declares `Microsoft.App/containerApps/authConfigs@2024-03-01` named `current`. Configures `platform.enabled=true`, `unauthenticatedClientAction=RedirectToLoginPage`, `redirectToProvider=azureactivedirectory`, and the Entra ID identity provider with `openIdIssuer`, `clientId`, and `clientSecretSettingName`.
- **Opt-in gating**: `enableAuth bool = false` + `authClientId string = ''`. The auth module only deploys when `enableAuth && !empty(authClientId)`. A plain `azd up` is unaffected.
- **Secret handling**: `@secure() param authClientSecret` flows from `azd env` → Bicep → Container App secret `aad-client-secret`. No real value in source or parameter files. The auth config references the secret by name only (`clientSecretSettingName`). Alternatively, operators can provision the secret out-of-band via `az containerapp secret set`.
- **Parameters wired**: `main.parameters.json` maps `AZURE_AUTH_ENABLED`, `AZURE_AUTH_CLIENT_ID`, `AZURE_AUTH_TENANT_ID`, `AZURE_AUTH_CLIENT_SECRET` with safe empty/false defaults.
- **Documentation**: Added full "Enable Entra ID Authentication" section to `DEPLOY.md` covering app registration, service principal creation, `appRoleAssignmentRequired`, user assignment, client secret, `azd env set` commands, and verification steps.
- **Validation**: `az bicep build` zero errors (warnings only — all pre-existing except expected `no-hardcoded-env-urls` for `login.microsoftonline.com`). `ruff check .` clean. 368 tests pass + 1 pre-existing failure (`test_default_voice_is_coral` — expects "coral", config defaults to "shimmer"; unrelated to infra changes).


- **WS transport investigation, infra findings (2026-09-22, read-only)**
  - Transport path: Envoy ingress → EasyAuth sidecar (`http-auth`) → aiohttp. Chromium's `permessage-deflate` offer reached aiohttp intact; the proxies pass extensions through.
  - Container app `capps-backend-axgpampkq3yfa`: min 1 / max 5 replicas, http scale at 20 concurrent, **no sticky sessions**. The Dockerfile runs gunicorn **`--workers 2`**.
  - **Alert:** at inspection time the latest revision `--0000004` was serving `containerapps-helloworld` with 100% of traffic, while azd revision `azd-1790113609` had 0%. A provision probably overwrote the image. Nothing was changed; this needs a redeploy by the owner.
  - The aiohttp fix for #13274 is unreleased. Re-enable `connection.ws_compression` only after a release that contains aio-libs/aiohttp#13302 and a green `test_ws_transport`.

- **azd `exists` wiring fix: root cause of the helloworld revision (2026-09-22, `fix/ws-transport`)**
  - `infra/main.parameters.json` mapped `webAppExists` to `${SERVICE_WEB_RESOURCE_EXISTS=false}`. The azd service in `azure.yaml` is `backend`, so azd only ever sets `SERVICE_BACKEND_RESOURCE_EXISTS`.
  - As a result `exists` was always false. `container-app-upsert.bicep` never read the running image, and `container-app.bicep` fell back to `containerapps-helloworld:latest` on every `azd provision`.
  - Fix: map it to `${SERVICE_BACKEND_RESOURCE_EXISTS=false}`. This was the only `SERVICE_WEB_` reference in the repo.
  - Guard: `app/backend/tests/test_azd_service_wiring.py` (3 tests):
    - every `SERVICE_*_RESOURCE_EXISTS` variable names a real azure.yaml service;
    - every containerapp service has a mapping;
    - `azd-service-name` tags in main.bicep are declared services.
  - Mutation-checked. Reverting to `SERVICE_WEB_` fails 2 tests; changing the tag to `web` fails 1.
  - `az bicep build`: 0 errors, and the same pre-existing warnings as before.

- **Order resume infra (2026-09-22, `feat/order-resume` step 0)**
  - `app/Dockerfile` and the start scripts now run gunicorn `--workers 1`.
  - Backend Container App ingress uses `stickySessions.affinity: 'sticky'` via the `stickySessionsAffinity` param in `container-app.bicep` and the upsert. The app is in single revision mode.
  - `APP_SESSION_SECRET` is a Container App secret (`app-session-secret`), mapped to env through secretRef. It defaults to newGuid()+newGuid() and can be pinned with `azd env set APP_SESSION_SECRET`.
    - `APP_SESSION_SECRET_FINGERPRINT` forces a new revision when the secret changes.
    - An out-of-band `aad-client-secret` is preserved via listSecrets.
  - Guard: `tests/test_infra_resume.py` (parses the Dockerfile and bicep). 5 mutations, all killed.
  - `az bicep build`: 0 errors, with the baseline warnings.
  - Live-only checks: the affinity cookie on the WS upgrade through the EasyAuth sidecar, and the secrets PUT semantics.

- **Conformance CI workflow (2026-09-23, `feat/conformance-harness`, #11)**
  - Added `.github/workflows/conformance.yml`: `pull_request` to `dev`/`main` + `workflow_dispatch`, three independent jobs (`python-tests`, `frontend-tests`, `conformance`), `ubuntu-latest`.
  - `conformance` job runs `dotnet test tests/conformance` with `CONFORMANCE_BACKEND=python` against a repo-root `.venv`; matrixed on `backend: [python]` so S2 adds `dotnet` with a one-line diff.
  - Required Beth's cross-platform fix first: `RepoPaths.PythonExecutable()` was Windows-only (`.venv/Scripts/python.exe`); GitHub-hosted Linux runners need `.venv/bin/python`.
  - Actions pinned at major-version tags, `permissions: contents: read`, concurrency-cancel on superseded runs, caching (pip/npm built-in via setup actions, explicit `actions/cache` for `~/.nuget/packages`), failure-only log/results artifact upload on every job.
  - Confirmed no changes needed for public-registry CI access: `package-lock.json` already 0 internal hosts (grep-verified), no repo-level `NuGet.Config` forcing the corporate proxy.
  - YAML validated via `PyYAML`'s `yaml.safe_load` (caught and quoted the `on:` boolean-coercion gotcha) — `actionlint` not available without an out-of-policy binary fetch, so schema correctness was reasoned manually against documented `actions/*` inputs.
  - Not yet empirically verified (can't run Actions locally): `actions/setup-dotnet` resolving the exact prerelease SDK string `11.0.100-rc.1.26425.128` on a fresh Linux runner — flagged for the first real PR run.
  - Commit: `d1d0720`. Decision logged: `.squad/decisions/inbox/squanchy-conformance-ci.md`.

- **Conformance CI Stage A fix (2026-09-24, `feat/conformance-harness`, Rick's PR #22 review, #7/#11)**
  - Root cause of red CI: `app/backend/static` is gitignored and only exists after `npm run build`; the coordinator's local "8/8" had only passed because that machine already had a stale built frontend. GitHub-hosted runners start clean, so both `python-tests` and `conformance` jobs failed the moment the backend (or the harness's Python launcher) needed `static/index.html`.
  - Fixed as item 1 (implemented alongside Beth's harness-side fail-fast message, committed together as `1e33e5f`): `.github/workflows/conformance.yml` now runs `npm ci && npm run build` in `app/frontend` before both the `python-tests` and `conformance` jobs consume the backend, so `app/backend/static` exists by the time either suite starts.
  - Verified the harness's own defense-in-depth: with `app/backend/static` moved aside locally (simulating a clean runner before the frontend-build step would have run), `dotnet test tests/conformance` fails immediately with a clear, actionable message (`'...\static\index.html' does not exist...`) instead of the previous opaque "backend exited early" — so even if a future workflow edit reorders steps incorrectly, CI will fail loud and clear rather than silently.
  - No other CI job changes needed for Stage A; items 8–17 (Stage B) are out of scope until the coordinator pushes and confirms this round is green.

- **Conformance R4 de-flake (2026-09-25/26, `squad/55-62-conformance-flakes`, PR #66 round-3 review, #55/#62)**
  - `Fixture_level_M1_late_error_from_scenario_A_is_attributed_to_A` was timing-dependent: it scheduled scenario A's late error at a fixed 300ms and relied on scenario B's 500ms quiescence window (measured from an earlier line) still being open when the error arrived — about 200ms of margin for the Python sleep, the pipe, and ThreadPool dispatch to fit inside. A loaded CI runner blew through it (`Assert.NotNull() Failure: Value is null`, run 36218817741). Local repro: 450ms passed, 550ms reproduced the exact failure.
  - **Lesson — a heuristic's window is not a test oracle.** #66 M1(b) exists precisely because a drain's idle window can miss an in-flight line by design; a test that assumes the window will always catch one is testing the wrong contract, no matter how generous the window looks in isolation. The fix (from Rick's review snippet, generalized to a `[Theory]` over lagMs = 0, 550, 2000): make scenario A's own `EndScenario` check run and pass *before* the late-error command is even sent to the child process, so it's structurally impossible for it to observe the error — then wait on the real `CountUnhandledErrors()` value itself (event-driven, re-checked after every `Append`/`ScanLine`) with a generous cap, instead of any wall-clock margin. This pattern — assert a boundary happened before the event that must be "late" to it, then poll the real downstream signal rather than a derived text match — generalizes to any future test in this file that needs a "definitely landed after X" guarantee.
  - **Mutation-check gotcha confirmed again:** disabling `ChargeStrandedErrorsToPreviousScenario` (return `null` unconditionally) turned all 3 theory cases red immediately — cheap, fast confirmation that the rewritten test still exercises the real attribution path, not just its own plumbing.
  - **Worktree venv:** a plain copy of the repo-root `.venv` into a worktree works without any relinking — `pyvenv.cfg`'s `home` path points at the system Python interpreter, unaffected by the venv directory being copied elsewhere. No junctions/symlinks needed.
  - Validated: 15/15 green unloaded, 15/15 green under 24 CPU-busy processes (one per core, confirmed 100% CPU via `Get-Counter`, all stopped by PID after). 35/35 harness unit tests, 3 consecutive 458/458 full conformance suite runs, `pytest` 875 passed/125 subtests, `ruff check .` clean. PR CI all 6 checks green at `086654b`.
  - Commit: `086654b`. Decision logged: `.squad/decisions/inbox/squanchy-pr66-r4.md`. Commented on PR #66 addressed to Rick. Not merged.
