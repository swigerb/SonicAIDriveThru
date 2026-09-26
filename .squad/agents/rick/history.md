# Project Context

- **Owner:** Brian Swiger
- **Project:** Sonic AI Drive-Thru Voice Assistant — AI-powered voice ordering experience using Azure OpenAI GPT-4o Realtime, Azure AI Search, and Azure Container Apps
- **Stack:** Python backend (aiohttp, WebSockets, Azure OpenAI Realtime, Azure AI Search, Azure Speech SDK), React/TypeScript frontend (Vite, Tailwind CSS, shadcn/ui), Bicep IaC, Docker, azd CLI
- **Created:** 2026-03-19

## Learnings

- **Sonic Rebrand Scope (2026-03-20)**: Identified ~100+ Dunkin-specific references across frontend UI, backend prompts, menu data, docs, and team context. Critical changes needed in system prompts (`app.py`/`rtmt.py`), frontend components (`App.tsx`/`order-summary.tsx`), menu data files, and logo asset. No changes required in infrastructure, tests (logic remains), or upstream attribution. Scope documented in `.squad/decisions/inbox/rick-sonic-rebrand-scope.md`. Recommended 2–4 dev-days with team parallelization.
- **Team Orchestration (2026-03-19T04-06)**: Morty completed frontend rebrand (13 tests pass), Summer completed backend rebrand (69 tests pass), Birdperson created verification tests (12 tests pass). All decisions merged to decisions.md.
- **Performance Audit (2026-03-21)**: Full end-to-end latency audit of voice pipeline (mic → WS → middleware → Azure OpenAI Realtime → AI Search → response → audio playback). Key changes: (1) Regex-based type extraction in rtmt.py to skip json.loads on ~95% of hot-path messages (audio deltas, input_audio_buffer.append), (2) Set max_response_output_tokens=150 to force concise voice responses, (3) Reduced AI Search KNN from 50→15 and top from 5→3 to cut search latency, (4) Trimmed system prompt from ~170 words to ~100 words reducing per-turn token processing, (5) Cached order summary JSON in order_state to avoid repeated Pydantic serialization, (6) Pre-serialized static WS messages (greeting, response.create), (7) Tightened frontend VAD (threshold 0.7→0.6, silence 500→400ms, prefix padding 300→200ms) and greeting timing (drain wait 2500→2000ms, safety timeout 5s→3.5s, removed 150ms post-drain delay). All 100 backend + 13 frontend tests pass. Ruff clean.
- **Performance Audit Orchestration (2026-03-19T13-21)**: Team completed full-stack performance sprint with 5 agents. Rick lead: 8 fixes across JSON parsing, token cap, search params, system prompt, JSON caching, VAD timing, and response filtering. Summer: 10 fixes for race conditions, hot-path fast-returns, search caching, compression, gzip, logging, memory. Morty: 9 fixes for AudioContext reuse, zero-alloc buffers, memoization, lazy loading, vendor chunking. Squanchy: 6 infrastructure fixes for Gunicorn async, health probes, auto-scaling, Docker caching. Birdperson: 28 performance tests validating latency, memory, thread safety, production readiness. All decisions documented in decisions.md. Orchestration logs written per-agent.
- **Echo Suppression Review (2026-03-21)**: Reviewed coordinated Summer+Morty fix for audio feedback loop (AI hearing its own speech, transcribing phantom input, self-conversation loop). Previous fix (mute on `response.audio.delta`) was too late. New fix: defense-in-depth with (1) frontend gain-node muting on `response.created` (earliest event), (2) backend `ai_speaking` flag gating `input_audio_buffer.append`, 300ms cooldown after `response.audio.done`, and `input_audio_buffer.clear` flush. Double-clear is idempotent. Per-connection state isolation correct (function-local vars in asyncio). Barge-in preserved via `speech_started` on both sides. No race conditions (asyncio single-thread + JS single-thread). Verdict: APPROVED. Key insight: for real-time audio systems, defense-in-depth (frontend+backend suppression) is the right architecture — silent failures in one layer are caught by the other.
- **Demo Bug Fix Review (2026-03-22)**: Reviewed Summer's changeset fixing two demo-blockers: (1) tools not called — greeting fired before `session.updated` confirmation, so OpenAI hadn't loaded tool defs; fix: trigger greeting on `session.updated` event. (2) Barge-in broken — three-way deadlock between frontend gain=0, backend dropping audio, and OpenAI unable to fire `speech_started`; fix: AnalyserNode on raw mic stream (before gain) detects user voice → unmutes → sends `response.cancel`. Also fixed: `reset_order` missing `session_id`, `reset_order` using `TO_CLIENT` instead of `TO_BOTH`, frontend only handling `update_order` tool responses. Verdict: **APPROVED**. Root causes correct. AnalyserNode approach is the right call — it's the only way to detect speech on a muted stream without polling the server. Not over-engineered; it's the minimum viable fix for the deadlock. The 0.08 RMS threshold and 100ms polling interval are conservative and appropriate. All ancillary fixes (reset_order args, TO_BOTH routing, frontend tool dispatch) are correct and necessary.
- **Greeting Regression Triage (2026-03-22)**: Coordinated with Summer and Morty on greeting-before-session.update debugging. Issue: AI asked for items but never called tools. Root cause: `from_client_to_server()` fired greeting before forwarding `session.update`. Solution requires reordering + fallback tool registration + diagnostic logging. Status: Resolved with Summer's fix and Morty's barge-in handler updates.
- **Architectural Review — Prompt Externalization & Hardening (2026-03-25)**: Brian flagged prompts-in-code as tech debt. Full audit confirmed: system prompt (app.py:127-249, ~3500 chars), greeting (rtmt.py:141-150), tool schemas (tools.py), upsell hints, error messages, and combo hints all hardcoded across 4+ files. Recommended YAML-based prompt externalization under `app/backend/prompts/` with version fields. Also cataloged: 17+ hardcoded config values needing externalization (temperature, tax rate, cache TTL, quantity limits, echo cooldown, etc.) → proposed `config.py` module. Key findings: rtmt.py is a 751-line god file needing split; zero tests on rtmt.py; no CORS/auth on WebSocket; token provider has no refresh mechanism; OrderState singleton has no session limits; `azurespeech.py` and `azure_speech_gpt4o_mini.py` are dead code. Performance and infra are solid (previous sprint). 10 areas rated (2 🟢, 6 🟡, 1 🔴). Full analysis in `.squad/decisions/inbox/rick-arch-review.md`. Brian preference: prompt versioning is top priority for demo iteration velocity.
- **Phase 4 Security Scope Review (2026-03-25T13-11)**: Analyzed Phase 4 demo-safe security scope. Confirmed no app registration needed — demo doesn't authenticate users, just protects WebSocket server from abuse. Proposed 4-tier strategy: (1) async token refresh every 5 min (eliminates ~200-500ms per-connection blocking), (2) session limits 10 max concurrent + 5 min idle timeout (prevents runaway), (3) origin validation (CSRF prevention), (4) HMAC session tokens disabled by default (can be enabled for production without code changes). Demo impact: zero until `require_session_token: true` is set. All decisions documented in decisions.md (Decisions #29-33). Commit 348da2d.

- **Order resume after reconnect: plan only (2026-09-22)**
  - Plan written to the session-state `order-resume-plan.md`, outside the repo.
  - **Design:**
    - Detach instead of delete, with a grace TTL (120s) and an LRU cap.
    - A server-minted, rotating 256-bit resume id delivered over the websocket and kept in sessionStorage. It is sent as the first frame `extension.resume`, never in the URL, and bound to the EasyAuth principal.
    - Re-attach the order, push it to the ticket, and rehydrate the new upstream with one system `conversation.item.create` (order JSON plus the last N transcript turns). Suppress the greeting; an optional welcome-back goes through the existing `session.updated` gate.
    - Idle closes default to *not* resumable.
  - **Hard prerequisite:** gunicorn `--workers 1` plus ACA sticky sessions. Today 2 workers × up to 5 replicas with no affinity would defeat any in-memory resume. Redis was rejected for the demo.
  - Estimate: ~5.5–6 dev-days. 8 open decisions for Brian.

- **Order resume — Stage 1 architecture sign-off (2026-09-22, `feat/order-resume`)**
  - Brian's decisions replaced parts of the plan:
    - Idle closes are never resumable, and the idle clock keeps running while the guest is detached (hold = min(120s, remaining idle)).
    - No principal binding and no `sid` claim in the HMAC token.
    - No welcome-back line: the carhop stays silent, with one 30s nudge through the `session.updated` gate.
    - One worker plus sticky ingress; maxReplicas unchanged.
  - The wire protocol for Stage 2 is in `docs/order_resume.md`: `extension.resume` is the first frame; the replies are `extension.session_resumed` / `extension.resume_rejected` (always followed by metadata); `extension.end_session` closes with 1000; stale sockets get 4002; 4000 is final.
  - Metadata is now deferred until the resume decision (first frame, or 2s). Old frontends see it up to 2s later.
  - The known limit is that resume only works within one replica. A lost replica means a fresh order, which is acceptable for the demo.

- **Order resume — Stage 2 review (2026-09-22)**
  - Protocol is as documented, with two deliberate browser-side additions:
    - After the 1000 `session_ended` close, the hook opens a FRESH socket, not a resume. Frames sent between `endSession()` and that close go to the new session.
    - After a resume the browser re-sends its `session.update`, which restores its VAD 0.7/500. The backend's `greeting_sent` gate keeps it silent.
  - The hook no longer uses react-use-websocket's keep=true queue at all, which removes the "queued ahead of resume" hazard structurally.
  - Known limits:
    - A reload restores the ticket but needs a tap for the mic, because a new document's AudioContext starts suspended.
    - A duplicated tab shares the id; whichever resumes first wins, and the other gets 4002 or a rejection and starts fresh.

## 2026-09-23 — feat/round3

- **Round 3 review:**
  - R1 composes with resume without a shared mutable flag: the nudge asks `recovery.busy`, the retry bypasses the idle clock, detach cancels. One failure is never handled twice (in-flight errors defer to that response's `response.done`).
  - R2's strict transcript check is the real value: the old smoke passed while the model was answering instead of echoing.
  - R3 guard scans source as well as locales, so a leftover can't come back through a component.
  - Left alone deliberately: the internal `voicerag` logger name in `setup_search_index.py`, the VoiceRAG attribution in README / `voice_rag_README.md`, unused `groundingFiles.*` keys.
  - Deploy-only: real Azure rate-limit error shape and retry hint; a live retry regenerating the answer; clip autoplay on devices; the postdeploy smoke hook under azd.

## 2026-09-25 - P1 persona architecture (#19, #51)

- **Spike output:** ADR-001 and `docs/persona-architecture.md` on `squad/19-persona-architecture`, as a draft PR into `dev`. Proposed only; Brian reviews before any P2 work.
- **Key finding:** of 51 brand differences across the three repos, 31 are persona data, 12 are shared code, 7 are dropped, and only 1 (McD meal-number lookup) needs a named strategy. Data-first packs in `personas/<id>/` beat per-brand code plug-ins because every plug-in would be written twice (Python and C#).
- **Switching:** per session (`/realtime?persona=`) inside a per-deployment allow-list. It is cheaper (one app, not three) and it forces the per-session `Persona` object that the C# port needs anyway.
- **#51:** check the golden table against the data; never generate it from the data, or a wrong field becomes its own oracle. Off-menu rules become ordered first-match data; a "no side-slot rule" loader check makes the PR #50 revenue rule structural.
- **Watch-outs for P2:** the "no Dunkin words" guards must be inverted; the siblings share the free Search service's three indexes with the unified app until cutover; McD's `modify` action is dormant because the YAML tool schema wins over the inline one.
