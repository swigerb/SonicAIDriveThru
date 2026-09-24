# Project Context

- **Owner:** Brian Swiger
- **Project:** Sonic AI Drive-Thru Voice Assistant — AI-powered voice ordering experience using Azure OpenAI GPT-4o Realtime, Azure AI Search, and Azure Container Apps
- **Stack:** Python backend (aiohttp, WebSockets, Azure OpenAI Realtime, Azure AI Search, Azure Speech SDK), React/TypeScript frontend (Vite, Tailwind CSS, shadcn/ui), Bicep IaC, Docker, azd CLI
- **Created:** 2026-03-19

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->
- **Phase 5 Test Coverage (2026-03-25):** Wrote 141 new tests covering RTMT and tool calling pipelines. `test_rtmt.py` (75 tests): SessionManager creation/cleanup/concurrency/greeting/idle-timeout, ContextMonitor thresholds, EchoSuppressor state machine (audio delta/done/cooldown/barge-in/greeting suppression), TYPE_RE regex validation, pre-serialized messages, ToolResult/ToolResultDirection/Tool/RTToolCall value objects, HMAC token create/validate, RTMiddleTier init/attach/config, message processing (session.update injection, passthrough audio, session.created stripping, tool execution with TO_BOTH routing, error logging, malformed JSON), WebSocket handler (origin rejection, token rejection). `test_tool_calling.py` (66 tests): search query formatting/sizes/empty/errors/cache (TTL/eviction/clear/case-insensitive), OOS annotations, update_order add/remove/quantity limits (per-item max 10/total max 25/incremental), get_order/reset_order, tax calculation, upsell hints (burger→combo, drink→addon, side→drink, combo→upgrade), combo validation hints, menu_utils normalize_size/infer_category, extras validation, edge cases (special chars, duplicate items, empty cart). Total suite: 337 tests (196 pre-existing + 141 new). All Azure calls mocked. Commit: ac27ba9.
- **EchoSuppressor Async Requirement:** `EchoSuppressor.on_audio_done()` uses `asyncio.ensure_future()` internally, so tests must run inside async context (`asyncio.run()`) — plain sync unittest won't work. Document for future maintainers.
- **Pre-existing INVALID_MODS Dead Code:** Referenced in `tools.py:112` but never defined. Would raise `NameError` if any item with parenthesized mods hit `validate_customization()`. Flagged for Summer to review in future sprint.
- **Test Formatting Detail:** `get_grouped_order_for_readback()` format uses plain decimals ("6.46"), not "$6.46" — tests must match actual output.
- **Phase 4 security tests added** (`test_security.py`): 31 tests covering session limits (max concurrent acceptance/rejection, close-and-reopen, idle timeout cleanup, active-session survival), origin validation (same-origin, missing, foreign, allowed_origins list, trailing slash normalization), HMAC session tokens (valid, expired at 15min TTL, malformed, empty, require_session_token flag, signature tampering, payload tampering, wrong secret, exact-expiry boundary), and config.yaml security section verification. Uses lightweight stub classes for session limiter, origin validator, and HMAC token gen/verify to decouple from Summer's in-progress implementation — stubs match expected interface contracts so tests will validate the real code once merged.
- **Prompt loader tests added** (`test_prompt_loading.py`): 37 tests covering PromptLoader YAML loading (valid files, system prompt assembly, greeting, tool schemas, error messages, hints), missing file error paths (brand dir, manifest, system_prompt, greeting, tool_schemas), validation errors (empty sections, missing keys, malformed YAML, non-dict YAML), Jinja2 template rendering (variables, numeric, unknown key fallback, delta templates), and production smoke tests against real `prompts/sonic/` files. Uses pytest fixtures with `tmp_path` for isolated temp brand directories and `patch("prompt_loader._PROMPTS_DIR")` for clean redirection. Total test count now 196 (68 new).
- **Pre-existing flake**: `test_search_formatting_empty_results` in `test_performance.py` intermittently fails with 18ms > 10ms threshold — timing-sensitive, not a real regression.
- **Rebrand verification tests added**(`test_rebrand_verification.py`): 12 tests scan every source file for forbidden terms ("dunkin", "crew member", "coffee-chat"). Excludes `.squad/`, `.git/`, `node_modules/`, `__pycache__/`, `voice_rag_README.md` (attribution), and itself. Targeted checks verify README title, index.html `<title>`, and backend system prompt. Pre-rebrand run: 5 pass, 7 fail — exactly right. Tests report file + line number for every violation.
- Existing test files (`test_app.py`, `test_models.py`, `test_order_state.py`, `test_extras_rules.py`, `test_tools_search.py`) contain zero Dunkin/crew-member/coffee-chat references — no updates needed there.
- The backend system prompt in `app.py` was already rebranded to Sonic before these tests ran, so those 3 targeted prompt tests pass immediately.
- **Team Orchestration (2026-03-19T04-06)**: Rick provided scope analysis, Morty completed frontend rebrand (13 tests pass), Summer completed backend rebrand (69 tests pass), Birdperson created verification tests (12 tests pass).
- **Performance test harness added** (`test_performance.py`): 28 tests covering latency benchmarks (order_state <5ms, search formatting <10ms, JSON serialization <2ms), memory efficiency (add/remove cycle <1MB delta, 100-item order <2MB peak), thread safety (10 concurrent writers, 8 concurrent session lifecycles), session isolation, production readiness (app startup, static files, health endpoint, CORS wildcard check), and Pydantic model serialization speed. All tests use mocks — zero real Azure calls. Full suite now at 100 tests.
- OrderState is a singleton with no locking; thread safety tests pass because GIL serializes Python bytecode, but under true parallelism (e.g., multi-process) this would need a lock. Worth noting for future scaling.
- `aiohttp.test_utils.TestClient/TestServer` is the canonical way to test aiohttp endpoints without starting a real server — used for health and CORS checks.
- **Performance Audit Orchestration (2026-03-19T13-21)**: Team completed full-stack performance sprint with 5 agents. Rick lead: 8 fixes across JSON parsing, token cap, search params, system prompt, JSON caching, VAD timing, and response filtering. Summer: 10 fixes for race conditions, hot-path fast-returns, search caching, compression, gzip, logging, memory. Morty: 9 fixes for AudioContext reuse, zero-alloc buffers, memoization, lazy loading, vendor chunking. Squanchy: 6 infrastructure fixes for Gunicorn async, health probes, auto-scaling, Docker caching. Birdperson: 28 performance tests validating latency, memory, thread safety, production readiness. All decisions documented in decisions.md. Orchestration logs written per-agent.
- **Phase 5 RTMT + Tool Calling tests added** (`test_rtmt.py` + `test_tool_calling.py`): 141 new tests. `test_rtmt.py` (75 tests): SessionManager creation/cleanup/concurrency/greeting/idle-timeout, ContextMonitor thresholds, EchoSuppressor state machine (audio delta/done/cooldown/barge-in/greeting suppression), TYPE_RE regex validation, pre-serialized messages, ToolResult/ToolResultDirection/Tool/RTToolCall value objects, HMAC token create/validate, RTMiddleTier init/attach/config, message processing (session.update injection, passthrough audio, session.created stripping, tool execution with TO_BOTH routing, error logging, malformed JSON), WebSocket handler (origin rejection, token rejection). `test_tool_calling.py` (66 tests): search query formatting/sizes/empty/errors/cache (TTL/eviction/clear/case-insensitive), OOS annotations, update_order add/remove/quantity limits (per-item/total/incremental), get_order/reset_order, tax calculation, upsell hints (burger→combo, drink→addon, side→drink, combo→upgrade), combo validation hints, menu_utils normalize_size/infer_category, extras validation, edge cases (special chars, duplicate items, empty cart). All Azure calls mocked. Total suite: 337 tests (196 pre-existing + 141 new).
- `EchoSuppressor.on_audio_done()` uses `asyncio.ensure_future()` internally, so tests that call it must run inside an async context (`asyncio.run()`) — plain sync unittest won't work.
- `INVALID_MODS` is referenced in `tools.py:112` but never defined — pre-existing dead code. `validate_customization()` would raise `NameError` if any item with parenthesized mods hit that path. Noted but not fixed (not in scope).
- The `get_grouped_order_for_readback()` format doesn't include `$` prefix for totals — it uses plain decimal like "6.46". Tests should match actual format, not assume `$`.
- **Combo conversion mods regression tests (2026-03-26):** Added 7 tests to `test_order_state.py` covering the mod-in-combo-name bug and ensuring symmetric normalization. Key insight: `combo_base` must strip parenthesized mods before comparison with `existing_base` (which already strips via `.split("(")[0]`). Tests cover: (1) combo arrives with mods in name, (2) no-mods regression, (3) different mods on standalone vs combo, (4) mod carry-forward from standalone, (5) multiple standalones with selective removal, (6) quantity>1 decrement, (7) ® symbol normalization. Summer's fix (stripping parens from combo_base before comparison) was already applied — all 7 pass. Total suite: 354 tests.
- **Combo conversion mods regression tests (2026-03-25):** Added 7 tests to `test_order_state.py` covering the mod-in-combo-name bug. Key insight: `combo_base` must strip parenthesized mods before comparison with `existing_base` (which already strips via `.split("(")[0]`). Tests cover: (1) combo arrives with mods in name, (2) no-mods regression, (3) different mods on standalone vs combo, (4) mod carry-forward from standalone, (5) multiple standalones with selective removal, (6) quantity>1 decrement, (7) ® symbol normalization. Summer's fix (stripping parens from combo_base before comparison) was already applied — all 7 pass. Total suite: 354 tests.
- **Flaky time-dependent test fix (2026-08-06):** `test_extras_rules.py::test_allow_extra_when_slush_present` and `test_tool_calling.py::test_tax_on_multiple_items` failed daily 14:00–16:00 CDT because `is_happy_hour()` applied a 50% drink discount during that window, making hardcoded total assertions wrong. Fix: patched `order_state.is_happy_hour` to `False` in `setUp`/`tearDown` for classes with price assertions involving drink items (`ExtrasRuleTests`, `TaxCalculationTests`). Added explicit `HappyHourPricingTests` class (5 tests) and `test_tax_on_multiple_items_during_happy_hour` for positive coverage of the discount path — both discounted and non-discounted totals are now deterministically tested. Verified patch bites by confirming discounted vs full-price totals under each state. Full audit: no other tests in the suite depend on wall-clock time for pricing (combo tests use non-drink items or absorbed items with $0 contribution; `test_performance.py` uses `time.perf_counter()` for benchmarks only; `test_security.py` uses `time.time()` for token expiry logic, not pricing). Suite: 360 passed (354 + 6 new), ruff clean, deterministic under both HH=True and HH=False global patches.
- **Guard test false-negative fix (2026-08-06):** Found and fixed a defect in my own `test_rebrand_verification.py`. Root cause: `EXCLUDED_DIRS` was over-broad — it excluded `.devcontainer`, `.github`, `.vscode`, and `.copilot` from scanning, which meant `.devcontainer/devcontainer.json` containing `"name": "Coffee Chat"` (the old pre-rebrand repo name) survived undetected while the test reported green. This is the exact failure mode the guard test exists to prevent. Fix: (1) narrowed `EXCLUDED_DIRS` to only `.git`, `node_modules`, `__pycache__`, `.venv`/`venv`/`env`, and `.squad` (the latter kept deliberately since it holds legitimate historical rebrand records — added a comment explaining why); (2) removed `.devcontainer`, `.github`, `.vscode`, `.copilot` from exclusions so config/CI dirs are now scanned; (3) fixed the `devcontainer.json` violation (`"Coffee Chat"` → `"Sonic AI Drive-Thru"`, Node `"20"` → `"22"` to match Dockerfile and CI); (4) added `.sh` to `SCAN_EXTENSIONS` and `SCAN_FILENAMES = {"Dockerfile"}` for extensionless file coverage — this surfaced real violations in `deploy.sh` and `docker-build.sh` (`coffee-chat-app`/`coffee-chat-assistant` references), which were also fixed; (5) verified `.ipynb` not worth scanning (too noisy — outputs are generated artifacts, not authored code; team handles notebook cleanup separately). Proof-of-failure test: reintroduced `"Coffee Chat"` into `devcontainer.json`, confirmed test now fails with `[coffee-chat (old repo name)] .devcontainer\devcontainer.json:4`, then restored the fix and confirmed 12/12 pass. Lesson: guard tests are only as good as their scan scope — over-excluding directories defeats the purpose. Full verification: backend 354 pass, frontend build + 13/13 tests pass, `squad doctor` 11/0. Docker build blocked by corporate SSL proxy (environment issue, not code — `node:22-slim` stage started successfully confirming Node upgrade works).
- **Silent tool-call regression test (2026-09-22):** Added `tests/test_session_bootstrap.py` for the $0.00 Carhop Ticket incident.
  - It drives the real middle tier end to end against a fake GA realtime server. The fake enforces the two service rules involved:
    - server VAD auto-responds to mic audio;
    - a `session.update` with a different voice after assistant audio is rejected *wholesale* (`cannot_update_voice`).
  - Key insight: the earlier suite only unit-tested payload shape, and the payload was valid. The bug was *ordering*: browser audio reached an unconfigured session. Only an end-to-end fake with real service semantics can catch that.
  - Mutation-checked:
    - reverting rtmt.py fails 6;
    - removing the bootstrap fails 5;
    - removing the voice strip fails 3;
    - an unconditional picker send fails 1.
  - Also added `GARealtime21SurfaceTests`: reasoning fields pass through but are never sent by default, and the picker offers exactly the 10 documented GA voices (allow-list mutation fails 1).
  - Watch-out: `test_rebrand_verification` flags the sibling brand's name in source comments, so refer to that repo generically.

- **WS transport regression tests (2026-09-22, `fix/ws-transport`)**
  - Backend `tests/test_ws_transport.py`:
    - The handshake does not negotiate permessage-deflate.
    - The exact production framing (server PING → client PONG → deflated data frame) keeps the session alive and the `session.update` reaches the upstream.
    - Idle close delivers 4000/"idle_timeout" and deletes the order session.
    - The config default is off.
    - The upstream `ws_connect` passes `compress=0`.
  - Frontend `hooks/__tests__/useRealtime.test.tsx` (6) mocks `react-use-websocket` and captures the url/options/connect arguments; `status-message` gained 2 tests.
  - Mutations, each of which fails at least one test:
    - Drop `compress=` → 2 fail, with the real 1002.
    - Drop upstream `compress=0` → 1 fails.
    - Idle `ws.close()` default → `1000 != 4000`.
    - Config set to true → 3 fail.
    - `shouldReconnect: () => true` → 1 fails.
    - No `setShouldConnect(false)` on idle → 2 fail.
    - Keep=true on audio → 1 fails.
    - No token gating → 1 fails.
    - No `onReconnectStop` → 1 fails.
    - Stale token on reconnect → 1 fails.
    - `StatusMessage` ignores the notice → 2 fail.
  - The heartbeat test patched to 0.2s passed 10/10 in a flake loop.
### 2026-09-22 — session.update self-healing tests + mutation check
- `tests/test_session_bootstrap.py`: `FakeGARealtime` can now reject updates in configurable ways:
  - `reject_keys`: reject any update carrying the given GA session keys;
  - `reject_every_update`;
  - `echo_event_id=False`, which mimics gpt-realtime-1.5 rejecting `reasoning` with `event_id=None` and `param=None`;
  - it also rejects two unrelated client events: `conversation.item.delete` (echoes the event_id, `param=item_id`) and `input_audio_buffer.commit` (no event_id).
- New `SessionUpdateFallbackTests`, run end to end through the real middle tier:
  - every session.update carries a unique event_id;
  - (a) a rejected bootstrap gets exactly ONE fallback whose session keys are exactly {type, instructions, tools, tool_choice}. Tools are registered, the browser never sees the error, and the next VAD response has the tools;
  - the no-event_id variant of (a) also sets `_reasoning_rejected`, so later updates omit `reasoning`;
  - (b) the fallback being rejected too causes no loop, with and without an echoed event_id. There are exactly 2 updates, and the fallback's error reaches the browser once. A new original gets its own single fallback;
  - (c) the unrelated errors trigger no fallback and are forwarded.
- New `SessionUpdateGuardTests` cover the correlation rules and one-fallback-per-original.
- New `ReasoningAndTranscriptionConfigTests` cover:
  - reasoning is sent only when configured and only on reasoning deployments, and never on 1.5, gpt-realtime, the dated snapshot, mini, or 4o;
  - a client cannot inject `reasoning`;
  - the env/config precedence matrix;
  - the shipped `config.yaml` is rollback-safe.
- **Mutation check:**
  - `_recover_rejected_session_update` returning False fails 4 tests. They are (a) ×2 and (b) ×2, and all time out waiting for a fallback.
  - Removing the one-fallback loop guard fails both (b) tests with `187 != 2` / `180 != 2` updates, i.e. a runaway loop.
  - (c) is the negative control and correctly still passes.
- Backend: 431 passed (baseline 412).

## 2026-09-22 — feat/voice-reasoning verification

- Fallback mutation checks, run on `test_session_bootstrap.py`:
  - Fallback disabled: 4 failed (bootstrap minimal fallback, no-loop, no-loop without event_id, no-event_id recovery).
  - Loop guard removed: 2 failed.
  - Foreign event_id correlated: 2 failed.
  - Reasoning switch ignored: 6 failed.
  - Everything restored: all pass.
- `smoke_realtime.py`: 1.5 answered the TTS phrase instead of reading it aloud. The instructions are now firmer and an empty transcript fails. PASS on 2.1 and 1.5.
- `benchmark_reasoning.py`:
  - Repaired: stub search with the real result format, real order tools, stricter add counts, and realistic size-change history.
  - New: median/p90 output, a `--summarize` mode, and `--resume` for chunked runs.
- Final gate: pytest 434 passed; ruff clean; frontend build OK with 16/16 tests; `az bicep build` 0 errors.

- **Order resume Stage 1 tests (2026-09-22, `feat/order-resume`)**
  - `tests/test_order_resume.py` (42 tests) and `tests/test_infra_resume.py`.
  - They use a FakeClock plus a per-connection fake GA upstream, reusing the `_RealtimeHarness` `fake_class` hook, with no real sleeps over 1s.
  - Coverage:
    - Grace-then-delete; idle deletes immediately and a later resume is rejected; grace capped by the idle budget.
    - LRU cap; the concurrency cap ignores detached sessions.
    - Valid resume keeps the same sid and order; wrong, expired, reused and malformed ids are rejected, and the guest gets a fresh session.
    - Resume is honoured as the first frame only; 4002 goes to the stale socket.
    - Upstream order: bootstrap → rehydration (with the order) → no greeting.
    - The nudge fires once and only after session.updated; it is cancelled by speech, a transcript, or a guest response; 0 disables it.
    - The resume id never appears in logs (caplog at DEBUG plus verbose logging).
  - Mutation checks: 51 mutations (steps 0–3), all killed. Three step-2 survivors were killed after adding tests.
  - Final: pytest 496 passed (baseline 442), ruff clean.

- **Order resume — Stage 2 tests (2026-09-22)**
  - vitest suites:
    - `useRealtime.test.tsx`, 25 tests: resume first frame and never queued, id storage and rotation, each close code's reconnect/clear semantics, end_session plus the fresh socket.
    - `App.resume.test.tsx`, 12 tests: resumed → ticket, mic restart, no reset; gesture fallback; rejected; idle; superseded; gave-up; New order; a fast tap after New order; a tap on a resumed session never waits for a greeting.
    - `recorder.test.ts`, 3 tests, and the StatusMessage notices.
  - Mutations: 16 on the hook, 31 on the app/recorder/notices/ending, and 8 on the e2e. All killed, except two equivalents (`shouldReconnect` duplicated by `setShouldConnect`; an `ended` branch unreachable after the refactor). Survivors A14, A18 and X3 were killed after adding tests; A18 also needed a fix.
  - `scripts/e2e_order_resume.py`:
    - Setup: headless Edge, built frontend, the real RTMiddleTier and real order tools, and a fake GA upstream. 42/42 checks in ~37s.
    - Scenarios: 1011 drop → same ticket and sid, bootstrap → rehydration → no greeting, auto mic, one nudge after the shortened 4s. Also the gesture fallback, a tap while reconnecting, a reload, idle 4000, strict autoplay, and resume ids absent from URLs and logs.

## 2026-09-23 — feat/round3

- Mutation checks (all scripts kept outside the repo):

  | item | mutants | killed |
  |---|---|---|
  | R1 backend | 20 + 4 follow-ups | all; M10 killed after adding a `silent` fake mode; M11/M21/M22 were dead code and removed; M17 killed via `RecoveryUnitTests` |
  | R1 frontend | 15 | 15 |
  | R1 clips | 11 | 11 |
  | R2 smoke | 27 | 27 (a 0.97 threshold first survived; added ~0.95 cases) |
  | R3 locales | 17 | 17 |
  | dz | 5 | 5 |

- Counts: backend 496 → 568 (+61 subtests); vitest 65 → 116.
- `ResumeInteractionTests` covers retry vs nudge (no double response either way), retry not refreshing idle, and detach cancelling a pending retry.

## 2026-09-23 — feat/conformance-harness (#7)

- Designed and stood up `tests/conformance/`: a black-box, language-neutral .NET 11 xUnit v3 suite (`Conformance.slnx`, hand-authored — `dotnet sln add` has a CLI bug on `.slnx` this SDK build) talking to the backend only over HTTP/WebSocket. Three projects: `Conformance.Fakes` (FakeRealtimeUpstreamServer with GaSessionValidator/FrameLog/RealtimeScript; FakeSearchServer answering from `menuItems.json`), `Conformance.Harness` (PythonBackendLauncher, BackendEnvironment, BackendLauncherFactory, RealtimeBrowserClient, RepoPaths, NetworkUtils), `Conformance.Tests` (5 test classes, 8 tests).
- Scenarios: smoke (bootstrap `session.update` first frame, 4 tools + `tool_choice=auto`, greeting waits for `session.updated`), `/health` 200 (JSON-parsed, not substring), `/api/auth/session` token, deflate not negotiated, plus 3 fake-only scripting tests (audio deltas, function calls, rate-limited `response.done`).
- Mutation-checked the smoke scenario against `rtmt.py`: removing the bootstrap send fails both smoke tests with clear timeout messages ("Bootstrap session.update never arrived"); restoring is green again (8/8).
- Green 3× in a row at three points in the work (initial, after Summer's test-hook changes, after Beth's cross-platform fix) — deterministic, no sleeps, `FrameLog.WaitForAsync`-style timeout waits throughout.
- `CONFORMANCE_BACKEND=dotnet` (S2 placeholder) skips cleanly: 5 skip, 3 fake-only pass, 0 fail.
- Decision logged: `.squad/decisions/inbox/birdperson-conformance-suite-design.md`.

## 2026-09-24 — feat/conformance-harness Stage A (Rick's PR #22 review, #7/#11)

- Rick reviewed PR #22 and requested changes; CI was confirmed red because `app/backend/static` (gitignored, built by the frontend) doesn't exist on a clean runner — the coordinator's local "8/8" only passed because that machine had a stale built frontend. Lesson applied: re-validated everything this round from a genuinely clean state (moved `static` aside, confirmed the suite fails with the intended clear message, restored it, then re-ran green).
- Owned/drove the scenario design for items 2, 3, 4, 5:
  - **Item 5** — replaced the shared server-level frame log with a per-connection `FakeRealtimeConnection` (id, own `ReceivedFrames`, api-key, model) plus a `ConnectionRegistry` (`WaitForNextConnectionAsync`, `WaitForNoOpenConnectionsAsync`). Rewrote `SmokeSessionBootstrapTests` to assert only on its own captured connection and to fail if a connection is already open at test start.
  - **Item 4** — new scenario `Voice_cannot_be_changed_after_assistant_audio_has_been_sent`: sets voice pre-audio (accepted), drives a full scripted response through, then asserts a second voice change is rejected with `cannot_update_voice` echoing the request's `event_id`.
  - **Item 2** — new `ResponseDoneRoundTripTests`: waits *past* the greeting for `extension.round_trip_token` on the browser and asserts no `"Traceback"` in captured backend stderr. Mutation-checked by removing `output`/`usage` from the fake's `response.done` body — reproduced the exact `rtmt.py` `KeyError: 'output'` traceback that silently kills the connection (root cause of the missing round-trip token); restoring is green again.
  - **Item 3** — new `UpdateOrderToolCallTests`: scripts a real `update_order` function call through the full GA item lifecycle, sends a raw client `response.create`, asserts the resulting `function_call_output` reaches the upstream fake for the exact `call_id` and `extension.middle_tier_tool_response` reaches the browser, with no backend traceback.
- Full suite: 8 → 14 tests across the 9 Stage-A commits. Green 3× in a row from the final clean state (14/14 each run, ~1s).
- Final clean-state validation: `dotnet test` 3×green (14/14), `pytest app/backend/tests -q` 586 passed/61 subtests, `ruff check .` clean, `npm test` in `app/frontend` 116 passed, `git status` clean, no `bin/`/`obj/` tracked (`tests/conformance/.gitignore` confirmed via `git check-ignore -v`).
- Stopped after Stage A per instruction — Stage B (items 8–17) waits for the coordinator to push and confirm CI green.
- Decision logged: `.squad/decisions/inbox/birdperson-stage-a-review-response.md`.

## 2026-09-24 — feat/conformance-s1-3 issue #9 (with Summer)

- Black-box ordering scenarios in `tests/conformance/tests/Conformance.Tests/Scenarios/Ordering/` (new sub-folder per the S1 fan-out rules): `update_order` add/remove/modify incl. merge-by-quantity, partial vs full removal, per-item and whole-order quantity caps, zero/negative-price rejection, and all five Route 44 size aliases (`rt44`/`rt 44`/`route 44`/`44`/`44oz`) displaying as "Route 44 <item>"; combo scenarios incl. component absorption for all 9 golden `test_combo_orders.py` cases plus all 10 real combo menu items from `menuItems.json`; tax/totals to the cent at the four happy-hour boundary instants (13:59:59/14:00:00/15:59:59/16:00:00 store-local) via dedicated `FixedClock` fixtures, plus a non-drink item proven unaffected; `search` against `FakeSearch` incl. the field-name-fallback 400 path; `get_order`/`reset_order`; and the "tool errors, session survives" scenario (both the graceful application-level case and the genuinely-unhandled-exception case).
- Golden data ported from `test_order_state*.py`, `test_tool_calling.py`, `test_combo_orders.py`, `menuItems.json`, and `config.yaml` into one shared `tests/conformance/testdata/golden-order-pricing.json` + `GoldenOrderPricingData.cs` loader, so S4's future C# backend can assert the same cent-accurate cases.
- Two additive harness changes (own `harness:` commits): `FakeSearchServer.RejectSelectFieldOnce` (one-shot select-field-mismatch 400 simulation, harness follow-up #23) and `RepoPaths.GoldenOrderPricingJsonPath`.
- Found and fixed a harness bug while triage-ing the first run (18/55 failing): `OrderScenarioHelpers.CallToolAsync`'s browser-frame wait only filtered by `tool_name`, and `FrameLog.WaitForAsync` always returns the *first* matching frame in its history (no per-caller cursor) rather than the newest — so a second call to the same tool silently re-matched the first call's stale frame. Fixed with a browser-frame-count watermark captured before each call. This alone fixed 14 of the 18 failures.
- Found two genuine Python bugs, both empirically confirmed by running the scenario unskipped against the live backend (not just by reading source), both `[Fact(Skip = "Known Python bug: ...")]` with full tracebacks — `app/backend` is untouched:
  1. `rtmt.py`'s `response.output_item.done` handler has no try/except around `await tool.target(...)`; an unhandled exception in a tool (e.g. a scripted call omitting a required arg, raising `KeyError`) propagates to `_forward_messages`'s connection-wide catch-all, which tears the whole WebSocket down instead of returning a model-visible error. Confirmed: `KeyError: 'item_name'` at `tools.py:325` → `rtmt.py:817` → `rtmt.py:1344` → `rtmt.py:1357` → `Session ... detached (client close code=None)`, no `function_call_output` ever sent.
  2. `tools.py::search`'s try/except (206-248) only wraps the initial *lazy* `search_client.search(...)` call; the actual HTTP fetch (and any `HttpResponseError`) happens later in `async for record in search_results:` (line 251), outside the try/except — making the "Could not find a property named" fallback-retry branch (218-229) dead code. Confirmed: the harness's injected 400 was received and parsed correctly, but the retry was never sent and the connection died via the same rtmt.py mechanism as bug 1.
- Fixed 4 test-design flakes of my own making (not Python bugs): several new tests used a drink item under the ambient `ConformanceCollection` (real wall-clock), which happened to fall inside the real 14:00-16:00 happy-hour window during this run. Swapped one drink item for a non-drink one (matching the existing `UpdateOrderToolCallTests` precedent) and moved two test classes/a combo scenario onto explicit off/on-happy-hour `FixedClock` collections, filtering by the golden data's `happyHour` flag.
- Mutation-checked 13 representative behaviors across every scenario category directly in `app/backend/*.py` (scratch edits, never committed, `git checkout --` restore verified after each): quantity merge, partial/full removal, Route 44 size mapping, whole-order quantity cap, combo absorption slot math, combo-conversion mods carried, happy-hour boundary comparison, tax-rate application, `get_order`/`search` TO_BOTH vs TO_SERVER routing, `reset_order` clearing, and the graceful price-rejection guard — all 13 broke their scenario when mutated and passed again once reverted; `git status -- app/backend` confirmed clean after every cycle.
- Validation: `dotnet test tests\conformance` green 3× in a row (146 total, 142 passed, 4 skipped — the 2 new Python-bug skips plus 2 pre-existing), `pytest app/backend/tests -q` 604 passed/61 subtests (baseline unchanged, `app/backend` untouched), `ruff check .` clean, `git status` clean, no `bin/`/`obj/`/`TestResults/` tracked.
- 10 commits: 2 `harness:`-prefixed (FakeSearchServer, RepoPaths) + 8 scenario/data commits in logical groups (golden data+loader, shared helpers+fixtures, update_order, combos, happy-hour+tax, search, get_order/reset_order, tool-error-survives).
