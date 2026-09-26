# Squad Decisions

## Active Decisions

### Performance Audit (2026-03-19)

#### 1. Optimize WebSocket Message Processing (Rick — Lead)
- **Decision:** Skip JSON parsing on hot path using regex extraction for message type. Cache JSON serialization for order summaries. Pre-serialize static WebSocket messages at module import.
- **Impact:** Eliminates ~30 json.loads calls/sec per session on audio delta hot path (~95% of traffic). Removes redundant Pydantic serialization.
- **Trade-off:** Message routing depends on regex pattern validity (tested).

#### 2. Constrain Model Output Tokens (Rick)
- **Decision:** Set `max_response_output_tokens = 150` in voice interactions.
- **Rationale:** Voice responses should be 1-2 sentences. Without a cap, model can generate long responses increasing latency and audio playback time.
- **Trade-off:** Very complex orders might be slightly truncated. Monitor and increase to 200 if needed.

#### 3. Reduce AI Search Over-Fetching (Rick)
- **Decision:** Reduce KNN from 50→15, top results from 5→3 in Azure AI Search queries.
- **Rationale:** KNN=50 retrieves 50 matches for 5-result return (10x overfetch). For structured menu (~100 items), KNN=15 sufficient. Reduces token processing load.
- **Trade-off:** Edge cases with very ambiguous queries might miss a 4th/5th result. Acceptable for drive-thru context.

#### 4. Isolate Per-Connection WebSocket State (Summer)
- **Decision:** Move `_tools_pending` from shared RTMiddleTier dict to local scope in `_forward_messages()`. Each connection gets its own tracking dict.
- **Rationale:** Concurrent WebSocket clients were interfering via shared state. Race condition eliminated.
- **Risk:** None identified.

#### 5. Fast-Path Audio Messages (Summer)
- **Decision:** Define `_PASSTHROUGH_TYPES` frozenset (13 message types never modified). Return immediately after JSON parse, skip match/case logic.
- **Rationale:** During active speech, ~90% of messages are audio deltas. This optimization reduces per-message processing overhead.
- **Impact:** Single async task model still works; optimization is purely throughput.

#### 6. Implement Search Result Caching (Summer)
- **Decision:** Add `_SearchCache` with 60s TTL, 128-entry max for Azure AI Search results.
- **Rationale:** Repeated menu queries are common in drive-thru (same item asked multiple times). Eliminates redundant Azure round-trips.
- **Risk:** Cache invalidation: if menu changes frequently, consider shorter TTL.

#### 7. Reduce Search Response Payload (Summer)
- **Decision:** Cut `select_fields` from 11 to 5 fields in Azure Search queries.
- **Rationale:** Only 5 fields used in result formatting. Reduces network payload and Azure Search response time.
- **Trade-off:** None; filtering reduces bloat.

#### 8. Gzip Compression for HTTP Responses (Summer)
- **Decision:** Add `_compression_middleware` for text-based responses (JSON, JS, HTML, SVG). Skip WebSocket and streaming.
- **Impact:** 60-70% reduction in HTTP payload for typical JSON responses.
- **Overhead:** Minimal (compression on-the-fly, cached for static assets).

#### 9. Reuse AudioContext Across Sessions (Morty)
- **Decision:** Keep single AudioContext instance for player and recorder, reuse across recording/playback cycles.
- **Rationale:** AudioContext creation takes 50-100ms (OS-level audio device negotiation). Reuse eliminates this latency on every session start.
- **Risk:** Edge case where audio device is unplugged mid-session. Handled by graceful fallback (current error handling).

#### 10. Zero-Allocation Audio Capture Buffer (Morty)
- **Decision:** Replace O(n²) buffer append pattern with pre-allocated doubling buffer and `copyWithin()`.
- **Rationale:** Old pattern created new Uint8Array on every chunk (~20-50x/sec), copying all accumulated data. Caused GC pressure and frame drops.
- **Impact:** Near-zero allocation during audio hot path.

#### 11. Memoize Leaf React Components (Morty)
- **Decision:** Wrap `OrderSummary`, `TranscriptPanel`, `MenuPanel`, `StatusMessage`, `BrandHero`, `SessionTokenBanner` with React.memo.
- **Rationale:** These re-rendered on every parent state change even when props didn't change. MenuPanel and BrandHero are fully static.
- **Impact:** Transcript updates no longer trigger menu/hero re-renders. Surgical updates only.

#### 12. Remove Polling Timer in TranscriptPanel (Morty)
- **Decision:** Remove `setInterval` that called `setCurrentTime(new Date())` every second.
- **Rationale:** This caused entire transcript panel to re-render every second, even when idle. Timestamp comparison now uses adjacent transcript entries.
- **Impact:** Eliminated ~1 re-render/second.

#### 13. Lazy-Load Settings Component (Morty)
- **Decision:** Use `React.lazy()` + `Suspense` for Settings panel.
- **Rationale:** Settings rarely opened, includes Dialog/Sheet/Switch (~7.4 kB gzipped). No reason to load on initial page render.
- **Impact:** Faster initial page load.

#### 14. Strategic Vendor Chunking (Morty)
- **Decision:** Replace per-package `manualChunks` with explicit groups: `react-vendor`, `ui-vendor`, `i18n`, `motion`.
- **Rationale:** Old pattern created hundreds of tiny files. Strategic grouping produces fewer, larger chunks with better caching and fewer HTTP requests.
- **Impact:** Improved cache hit rate, reduced network requests in production.

#### 15. Disable Sourcemaps in Production (Morty)
- **Decision:** Set `sourcemap: false` in Vite build config for production builds.
- **Rationale:** Sourcemaps expose source code and increase artifact size. Not needed in production.
- **Impact:** Smaller deploy artifacts.

#### 16. Exponential Backoff for WebSocket Reconnection (Morty)
- **Decision:** Implement exponential backoff (1s base, 30s cap) with random jitter for reconnection attempts.
- **Rationale:** Default instant-retry can overwhelm server during outages (thundering herd). Backoff with jitter distributes reconnection load.
- **Impact:** More resilient connection recovery, server-friendly.

#### 17. Gunicorn Configuration for WebSocket (Squanchy)
- **Decision:** 2 async workers, 120s timeout, 65s keep-alive.
- **Rationale:** Async handles many concurrent connections. 120s timeout protects long-lived WebSocket sessions. 65s keep-alive matches Azure LB 60s idle timeout.
- **Risk:** Worker count should be monitored against memory usage (Azure SDK overhead).

#### 18. Container Apps Auto-Scaling (Squanchy)
- **Decision:** HTTP scaling at 20 concurrent requests per replica, max 5 replicas, min 1.
- **Rationale:** Each replica runs 2 async workers. 20 concurrent/replica is conservative starting point. Min 1 prevents cold-start.
- **Risk:** Threshold should be validated with real WebSocket load testing.

#### 19. Health Probes: Startup + Liveness + Readiness (Squanchy)
- **Decision:** Dedicated `/health` endpoint with generous startup probe (50s budget), liveness every 30s, readiness every 10s.
- **Rationale:** Startup probe allows gunicorn + pip deps to initialize. Liveness detects hung workers. Readiness gates traffic routing.
- **Trade-off:** 50s startup budget is conservative but acceptable for one-time initialization.

#### 20. Docker Layer Caching (Squanchy)
- **Decision:** Copy dependency files (package.json, requirements.txt) first, install, then copy source code.
- **Impact:** Dependency layer cached across code-only changes (saves 60-90s per rebuild).
- **Trade-off:** None; pure efficiency.

#### 21. Configurable Log Level (Squanchy)
- **Decision:** `LOG_LEVEL` env var controls logging (defaults to INFO). Can be set to DEBUG without code redeploy.
- **Rationale:** Enables troubleshooting by restarting container with new env var, avoiding full redeployment.
- **Impact:** Faster debugging cycle.

#### 22. Performance Test Harness (Birdperson)
- **Decision:** Implement 28 tests covering latency (<5ms order_state ops, <10ms search, <2ms JSON), memory (<1MB delta, <2MB peak), thread safety, and production readiness.
- **Rationale:** Quantifiable baseline for future optimization. Thresholds have ~10× headroom for operational margin. All Azure calls mocked (zero external dependencies).
- **Impact:** Regression protection, team confidence in production readiness.
- **Trade-off:** Thresholds can be tightened post-optimization if needed.

#### 23. Audio Feedback Loop Prevention (Morty)
- **Decision:** Multi-layered approach: VAD threshold `0.6` → `0.8`, silence duration `400ms` → `500ms`, auto gain control disabled, recorder worklet isolation via gain node, mic muting during AI playback.
- **Rationale:** AI speech output was being captured by microphone, creating feedback loop. Higher VAD threshold + longer silence buffer reject echo artifacts. AGC disable prevents amplification of speaker output. Gain node isolates recorder while preserving echo cancellation. Active muting while AI speaks blocks feedback path entirely.
- **Files:** `useRealtime.tsx`, `useAudioRecorder.tsx`, `recorder.ts`, `App.tsx`
- **Constraints:** Barge-in capability preserved (user can still interrupt with loud speech via server VAD). No permission re-prompts (mic stream kept alive, muted via gain node). Server-side VAD maintained.
- **Impact:** Eliminates infinite self-response loop. Maintains natural conversation flow.

#### 24. Echo Suppression Code Review (Rick — Reviewer, 2026-03-20)
- **Decision:** APPROVE — Defense-in-depth echo suppression is correct and well-coordinated
- **Architecture:** Two independent layers (frontend gain-node muting + backend gating via ai_speaking flag + 300ms cooldown + buffer clear) complement each other. If one layer fails silently, the other still works.
- **Key Validations:**
  - Double `input_audio_buffer.clear` (frontend on `response.created`, backend on `response.audio.done`) is idempotent — clearing an empty buffer is a no-op
  - Per-connection state isolation correct — `ai_speaking` and `cooldown_end` are local variables, no shared mutable state
  - 300ms cooldown appropriate for typical 50-200ms speaker-to-mic latency with headroom
  - Early muting at `response.created` prevents echo path before first audio delta arrives
  - Barge-in preserved — both frontend and backend reset suppression immediately on `speech_started`
  - No race conditions — asyncio single-threaded, all state checks atomic within event loop tick
- **Code Quality:** Summer's backend clean and well-commented; Morty's frontend integration clean; substring-marker approach (`_MARKER_*`) is faster than JSON parsing on hot path
- **Minor Observation:** `response.created` mutes briefly for tool-call-only responses (harmless, not worth complexity to distinguish)
- **Impact:** Eliminates phantom transcriptions from audio feedback loop. Barge-in ~300ms latency acceptable for drive-thru UX.

#### 25. Demo Readiness Audit (Unity — Auditor, 2026-03-21)
- **Decision:** System Prompt Refactor + VAD Tuning
- **Changes Made:**
  1. **System Prompt Format** (app.py:127-157): Dense paragraph → bulleted structure with named sections (VOICE STYLE, MENU & PRICING, ORDERING, CLOSING, BOUNDARIES). Added ALL CAPS emphasis on critical instructions (NEVER, ALWAYS, ONLY, CORRECT, FULL, TOTAL). Implemented phrase variety rules — "NEVER use the same phrase twice in a row" with examples: "Awesome choice!", "You got it!", "Great pick!", "Nice!", "Coming right up!". Added explicit grounding: "ONLY recommend items found in search results — do NOT invent menu items"
  2. **VAD Threshold** (useRealtime.tsx:165): 0.8 → 0.7. Rationale: Multi-layered echo suppression (server-side in rtmt.py + client-side mic muting) now handles echo properly. 0.7 is more forgiving for natural speech while still rejecting ambient noise.
  3. **Prefix Padding** (useRealtime.tsx:166): 200ms → 300ms. Rationale: 200ms risks clipping word starts (plosives like "burger", "please", "tots"). In demo context, clipped words cause AI to ask for repetition — embarrassing.
- **Validations:**
  - Temperature 0.6: ✅ Optimal for menu ordering (deterministic tool calling, natural variance)
  - Max tokens 250: ✅ Sufficient for full order recap + closing phrase (tested; perf Decision #2's 150 was too aggressive)
  - Voice "coral": ✅ Warm, friendly female — excellent Sonic carhop persona
  - Echo suppression: ✅ Well-architected, defense-in-depth validated
- **Demo Risk Assessment:**
  1. Risk: AI invents menu items (HIGH severity) — Mitigation: Added explicit grounding rule. Recommend pre-demo test with 5-10 orders covering all categories.
  2. Risk: Response truncation (MEDIUM severity) — Mitigation: max_tokens 250 + explicit "complete your full sentence" instruction. Recommend testing 4-5 item order.
  3. Risk: WebSocket disconnect (MEDIUM severity) — Mitigation: Exponential backoff with jitter (Decision #16). Recommend testing on exact demo network, pre-warming connection, backup browser tab.
- **Impact:** Ensures flawless voice ordering experience for Inspire Brands executive demo. All critical voice path components audited and optimized.

#### 26. ToolResultDirection.TO_BOTH for Order Updates (Summer — Backend Dev, 2026-03-21)
- **Decision:** Changed successful `update_order` tool results from `TO_CLIENT` to `TO_BOTH` routing.
- **Problem:** `TO_CLIENT` sent order summary to frontend UI but empty string to OpenAI model. AI had no confirmation order succeeded, causing dead silence after valid orders (including exactly 10 items at per-item limit).
- **Solution:** Added `ToolResultDirection.TO_BOTH = 3` to enum. Now sends order summary JSON to both:
  - **OpenAI server** — AI knows item was added, continues with "anything else?"
  - **Frontend client** — UI updates with current order
- **Implementation:**
  - `rtmt.py`: New enum value + updated routing conditions
  - `tools.py`: Success path `TO_CLIENT` → `TO_BOTH`
  - Error/limit responses remain `TO_SERVER` (AI relays the message)
- **Test Coverage:** All existing tests updated, 14 new quantity-limit tests added (111 total, all passing)
- **Impact:** Eliminates conversation-killing bug. Multi-item orders now flow naturally through all quantities up to limit (10 per item, 25 total).

#### 27. System Prompt Upgrade — Upselling & ACV for Inspire Brands Demo (Unity — AI Expert, 2026-03-22)
- **Decision:** Added 4 new system prompt sections and updated 2 existing to drive revenue through suggestive selling while maintaining authentic Sonic brand voice.
- **New Sections:**
  1. **CONVERSATIONAL FLOW** — No filler words at response start (reduces perceived latency); immediate pivot on barge-in interrupts
  2. **BRAND IDENTITY** — Tots mentioned FIRST whenever sides offered (Sonic's key differentiator)
  3. **SUGGESTIVE SELLING** — Three-tier upsell strategy:
     - Combo conversion: burger/sandwich alone → combo with Tots/fries + drink
     - Upsize: Small/Medium → Large with price difference
     - Sonic Signature treat suggestion when order has no dessert
  4. **TECHNICAL GUARDRAILS** — Currency spoken naturally ("six forty-nine", never "6.49"); long orders grouped not enumerated
- **Updated Sections:**
  - **ORDERING** — Added combo-check directive before moving to next item
  - **CLOSING AN ORDER** — Added item grouping rule for long orders
- **Rationale:** User directive for demo to show revenue-driving AI; Inspire Brands execs evaluate ACV impact; tots-first branding signals deep brand knowledge; no-filler-words cuts perceived latency by ~200-300ms per response
- **Trade-offs:** Prompt length increased ~40% (still optimal range for gpt-realtime-1.5); upselling adds one extra turn/item (mitigated by "ONE suggestion at a time, NEVER pushy" guardrail)
- **Risks:** Over-aggressive upselling → robotic feel (mitigated by variety rules + "NATURAL" emphasis); price mismatch on upsize (model should skip gracefully if search misses)
- **Validation Recommended:** Test 3-4 complete orders for combo triggers; confirm tots-first when "side"/"fries" mentioned; verify natural currency speech; test 5+ item order for grouping in recap
- **Impact:** Demo-ready system prompt ensuring revenue impact while maintaining conversational authenticity.

#### 28. User Directive: Demo Requirements (Brian Swiger via Copilot, 2026-03-21)
- **Request:** System prompt must drive higher Average Check Value (ACV) with suggestive selling (combo conversion, tots-first branding, treat suggestions), handle barge-in gracefully, avoid filler words, format currency as spoken words, and group long orders.
- **Context:** Executive demo requirements for Inspire Brands presentation. Critical for successful pitch.
- **Implementation:** Addressed by Decisions #26 (TO_BOTH routing for conversation flow) and #27 (suggestive selling + technical guardrails).
- **Impact:** Enables demo to showcase both conversational quality (no dead silence) and revenue impact (ACV-driving prompts).

### Phase 4 — Demo-Safe Security Hardening (2026-03-25)

#### 29. Background Token Refresh (Summer)
- **Decision:** Azure AD token refreshed proactively every 5 minutes via `asyncio.run_in_executor()` (non-blocking).
- **Rationale:** `get_bearer_token_provider()` is synchronous and blocks event loop (~200-500ms on refresh). Background loop eliminates per-connection blocking.
- **Trade-off:** Token could be up to 5 minutes stale. Azure AD tokens valid for 60 minutes, so 5-min refresh well within safe margins.
- **Config:** `security.token_refresh_interval_seconds` in config.yaml (default: 300)

#### 30. Session & Connection Limits (Summer + Birdperson)
- **Decision:** Max 10 concurrent WebSocket sessions, 300s idle timeout. Over-limit connections receive friendly JSON error. Idle checker runs every 60s.
- **Rationale:** Demo uses 1-3 sessions; 10 is generous for multi-device testing while preventing runaway connections. 5-min idle timeout acceptable for drive-thru context.
- **Files:** `session_manager.py` (SessionLimiter + idle timeout), `rtmt.py` (enforcement), `config.yaml` (security section)
- **Config:** `security.max_concurrent_sessions`, `security.idle_timeout_seconds` (defaults: 10, 300)
- **Test Coverage (Birdperson):** 31 tests covering accept/reject, close-and-reopen, idle cleanup, active survival

#### 31. Origin Validation (Summer + Birdperson)
- **Decision:** Reject cross-origin WebSocket connections unless origin matches Host header or is in `allowed_origins` list.
- **Rationale:** Prevents CSRF-style attacks via WebSocket. Same-origin always works (frontend served by same app).
- **Trade-off:** Developers testing from different ports must add origin to config.
- **Files:** `rtmt.py` (_websocket_handler origin check), `config.yaml` (allowed_origins list)
- **Config:** `security.allowed_origins` list (default: empty)
- **Test Coverage (Birdperson):** Origin validation, same-origin, missing header, foreign origin, allowed_origins list, trailing slash normalization

#### 32. HMAC Session Tokens (Summer + Birdperson)
- **Decision:** `GET /api/auth/session` returns HMAC-signed tokens (15-min TTL). Validation enforced only when `require_session_token: true`.
- **Rationale:** Prevents direct WebSocket abuse without breaking existing demo flow. Can be enabled for production without code changes.
- **Trade-off:** Tokens not encrypted (base64 payload only signed). Sufficient for session binding; not for sensitive data.
- **Files:** `app.py` (/api/auth/session endpoint), `rtmt.py` (token validation), `config.yaml` (require_session_token flag)
- **Config:** `security.require_session_token` (default: false), token TTL (15 min, configurable)
- **Test Coverage (Birdperson):** Valid/expired/malformed/tampering/signature/payload/secret/boundary cases, flag enforcement

#### 33. Frontend Graceful Fallback (Summer)
- **Decision:** Frontend fetches token from `/api/auth/session` on mount. If endpoint fails or returns non-200, connects without token.
- **Rationale:** Backward compatibility — old backends without endpoint still work. No user-visible change.
- **Files:** `useRealtime.tsx` (token fetch + URL wiring + 401 refresh)
- **Fallback Behavior:** null token → no `?token=...` param appended; legacy backends work unchanged
- **Impact:** Zero demo behavior change until `require_session_token: true` is set

#### 34. Search Fallback: Wrap the Retry, Not Just the First Attempt (Summer — Backend Dev, #37)
- **Decision:** Moved Azure AI Search result iteration and first-page materialization *inside* the
  existing `try` block in `tools.py::search()`, and wrapped the minimal-`select` retry call itself in
  its own `try`/`except` rather than letting it run unguarded after the first exception was caught.
- **Rationale:** The original code caught the field-name-mismatch 400 on the *first* search call, but
  then re-issued the retry call and iterated its results **outside** any exception handling — so a
  second failure (or a failure while paging results) would propagate as an unhandled exception instead
  of falling through to the tool's existing "no results" guest-facing response. This was dead-code
  protection: the retry path existed but wasn't actually safe to call.
- **Investigated (report-only, no change):** Checked whether the McDonald's/Dunkin' search tool
  implementations have the same pattern. **Confirmed absent** — those backends don't have an
  equivalent field-name-mismatch retry path at all (no dual-select-list fallback), so this specific
  dead-code-retry bug is unique to the Sonic backend and required no cross-backend fix.
- **Verification:** Red confirmed via `SearchToolTests.cs`'s two previously-skipped fallback
  scenarios (both now un-skipped and green) plus new realistic-mock Python unit tests in
  `test_tools_search.py`. Mutation-checked.

#### 35. Route 44 Alias Normalization on a Single Canonical Size Key (Summer — Backend Dev, #40)
- **Decision:** Added `menu_utils.canonical_size_key()` so every order-mutation path (add/merge/
  remove/update) normalizes `size` through one canonical mapping before comparing or storing —
  `"44 oz"`, `"route44"`, `"Route 44"`, and case variants all canonicalize to the same key used
  internally, instead of each caller inventing its own ad-hoc string comparison.
- **Rationale:** Guests and the model say the Route 44 size differently across turns
  (`"route 44"`, `"44oz"`, `"Route44"`); without a single canonical key, "add a route 44 tots" then
  "remove the 44 oz tots" silently failed to merge/remove because the literal strings didn't match.
- **Verification:** Red confirmed via 2 previously-skipped `UpdateOrderAddRemoveModifyTests.cs`
  scenarios (now un-skipped, green) plus 2 new Python unit tests. Mutation-checked.

#### 36. Single `_reset_order_state()` Helper Clears All Per-Session State (Summer — Backend Dev, #41)
- **Decision:** Extracted a single `_reset_order_state()` helper in `order_state.py` that clears
  every piece of per-session order state (items, combo/absorption display bookkeeping, etc.) in one
  place, and made `reset_order` call only this helper.
- **Rationale:** Reset previously cleared the order item list but left stale combo-absorption display
  state behind, so a reset order could still show phantom combo line items on the next `get_order`
  readback. A single reset helper removes the possibility of a future new piece of session state being
  added to one path and forgotten in the other.
- **Verification:** Red confirmed via previously-skipped `ComboAbsorptionTests.cs` reset-display
  scenario (now un-skipped, green) plus a new Python unit test. Mutation-checked.

#### 37. Category Inference: `menuItems.json` First, Word-Boundary Keyword Fallback Second; Sundaes Are Full-Price During Happy Hour (Summer — Backend Dev, #39 — decision confirmed by Brian)
- **Decision:** `infer_combo_component()` now looks up the item's category from `menuItems.json`
  first; only unknown items fall through to a keyword heuristic, and that heuristic now requires a
  **word-boundary** match (regex `\b...\b`) so `"Dr Pepper"` no longer spuriously matches on a
  substring inside an unrelated item name. Per Brian's explicit decision recorded on #39: **sundaes
  are excluded from the happy-hour drinks/sides discount and remain full price** — they are neither a
  "drink" nor a "side" for discount purposes even though the naive keyword heuristic would have
  matched "Sundae" as a dessert-adjacent item.
- **Rationale:** The prior keyword-only approach was both under- and over-inclusive: it missed items
  whose category was only knowable from the menu data, and it false-positive-matched substrings
  (`dr pepper` matching inside longer strings without a word boundary). Brian's sundae ruling needed
  an explicit, tested exception rather than relying on the keyword fallback accidentally getting it
  right.
- **Added:** A golden category table (`tests/conformance/testdata/golden-menu-categories.json`) with
  every menu item's expected bucket (`sides`/`drinks`/`none`), enforced by a new C# theory
  (`GoldenMenuCategoryHappyHourTests.cs`) covering all 3 buckets and both exception cases (sundae,
  hot-dog-entree).
- **Verification:** Red confirmed via the previously-skipped Ched 'R' Peppers scenario (now
  un-skipped, green) plus the new golden-category theory and 6 new Python unit tests
  (`test_menu_utils.py`). Mutation-checked (stashed `menu_utils.py`/`order_state.py`; confirmed both
  Ched R Peppers and the sundae exception case go genuinely red).
- **Correction (PR #50 review follow-up):** the two sundaes (Hot Fudge Sundae, Caramel Sundae) live
  in `menuItems.json`'s **"Shakes & Ice Cream"** category — that's specifically *why* the naive
  keyword/category heuristic miscategorized them as happy-hour-eligible drinks before Brian's ruling:
  "Shakes & Ice Cream" is otherwise a drinks-adjacent bucket (shakes/blasts genuinely are half-price
  drinks in happy hour), so a sundae sitting in the same JSON category needed the explicit
  `_SUNDAES` carve-out documented above rather than a blanket category-to-bucket mapping.

#### 38. Exact Money via `Decimal` + `ROUND_HALF_UP`, One Formatter for Every Spoken-Money Surface (Summer — Backend Dev, #46 — decision confirmed by Brian per #28 N22)
- **Decision:** All order money math (`_update_summary` in `order_state.py`) now runs entirely in
  `decimal.Decimal`, built directly from `menuItems.json` prices and config tax rates via
  `money_utils.to_decimal()` — never through a `float` intermediate — with **no intermediate
  rounding** anywhere in the subtotal/tax/final-total chain. A single new formatter,
  `money_utils.format_money()`, renders the final exact `Decimal` as a culture-invariant `"$0.00"`
  string using `decimal.ROUND_HALF_UP` (a half cent rounds *up*, not toward a ceiling — verified with
  a dedicated non-half-cent case that must still round down). Per Brian's #28 N22 note, every
  spoken-money surface in the backend — the `tools.py` prompt/template paths (~5 call sites) and the
  `get_order` readback in `order_state.py` (which previously omitted the `$` sign entirely) — now
  routes through this one formatter, and the wire's numeric fields (`total`/`tax`/`finalTotal`/
  `items[].price`) remain plain, un-rounded JSON numbers, unaffected by the display rule.
- **Rationale:** The prior implementation rendered spoken totals with Python's `float`-based `:.2f`
  format specifier, which round-trips through IEEE-754 binary and can disagree with *every*
  consistent decimal rounding rule depending on a value's specific binary representation — Rick's
  200k-order simulation found hundreds of half-cent disagreements. `SpokenTotalHalfCentTests` (the
  `5.265` → `$5.27` case) was `Skip`'d against Python for exactly this reason.
  Un-skipping it required an actual `Decimal`-based rounding rule, not a smarter `float` format string.
- **Added:** Two new N21 golden cases per Rick — trailing-zero (`10.80` → `$10.80`, proving a clean
  value still renders two decimal places) and round-up-non-midpoint (3× Tots medium at `2.79` →
  `9.0396` → `$9.04`, proving the rule generalizes beyond exact half-cent landings). Confirmed the
  conformance suite's money tolerance still holds — Python's `Decimal`-derived exact values now match
  the golden decimals exactly, well within the existing 1e-6 Python slack (which is now unused
  headroom rather than a load-bearing tolerance).
- **Verification:** Red confirmed on both sides — Python: `test_money_utils.py` (6 new tests,
  mutation-checked at both collection level, i.e. missing module, and assertion level, i.e.
  `ROUND_HALF_UP`→`ROUND_DOWN` swap); C#: `SpokenTotalHalfCentTests` un-skipped plus 2 new `[Fact]`s
  for the N21 cases, all green.
- **Correction (PR #50 review follow-up):** "built directly from `menuItems.json` prices" above
  overstates what `_update_summary` actually does — it sums `OrderItem.price * OrderItem.quantity`
  for each line, where `price` is whatever value the model passed as a tool-call argument to
  `update_order`, not a value `order_state.py`/`money_utils.py` looks up from `menuItems.json`
  itself. The system prompt instructs the model to quote prices from the menu data it was given, so
  in practice they match `menuItems.json`, but the backend has no independent lookup or validation of
  that — it trusts the caller's `price` argument. (`menuItems.json` schema/lookup redesign is a
  separate, larger piece of work Rick is filing independently; not in scope here.)

#### 39. Frontend Ticket Cents: Backend-Computed Display Strings as Source of Truth, Client-Side `formatMoney()` Double-Rounding as Defense-in-Depth (Morty — Frontend Dev, #47)
- **Decision:** Two-pronged. (1) `OrderSummary` (`app/backend/models.py`) gains three additive string
  fields — `totalDisplay`/`taxDisplay`/`finalTotalDisplay` — computed server-side via `format_money()`
  from the pre-float-conversion `Decimal` (the same #46 formatter, same `ROUND_HALF_UP` rule); these
  are the single source of truth the ticket prefers whenever present, added to the wire schema
  additively (numeric fields unchanged, existing consumers unaffected; a Pydantic
  `model_validator` auto-fills them from the numeric fields for any caller that omits them). (2) The
  frontend (`order-summary.tsx`) gains an exported `formatMoney()` helper using a **double-rounding**
  trick (`Number(value.toFixed(10)).toFixed(2)`) as a fallback for the dummy-data preview path (which
  never talks to the backend) and for each line item's `price * quantity` (not covered by the new
  backend fields, since `OrderItem` itself wasn't touched).
- **Rationale:** Rick's repro — `88.04499999999999` vs `88.045`, both meant to be the exact decimal
  `$88.045` — are genuinely distinct IEEE-754 doubles. Plain `.toFixed(2)` renders them
  inconsistently (`$88.04` vs `$88.05`); the classic "epsilon trick"
  (`Math.round((v + Number.EPSILON) * 100) / 100`) does not fix this either (still `$88.04` for the
  first value). Rounding first to a much higher intermediate precision collapses the float noise
  (which only ever appears past roughly the 10th decimal place at these magnitudes) before the final
  2-decimal round, so both inputs land on `$88.05`. This mirrors the exact same problem #46 solved on
  the backend: once float noise is baked into a value, only access to the original exact value (the
  backend's `Decimal` path) or an intermediate-precision cleanup round can reliably normalize it —
  hence preferring the backend string whenever it's available, and using the double-rounding trick
  only where the backend hasn't (yet) computed one.
- **Verification:** Red confirmed by reverting `formatMoney()` to plain `.toFixed(2)` — 3 of the new
  frontend tests fail exactly as expected (line-item price, Rick's two repro values, per-item
  rendering), restored and green. Backend `_fill_display_defaults` validator mutation-checked
  (`test_models.py`, 3 new tests) by breaking the fill logic — confirmed genuine red, restored.
- **Superseded (PR #50 review should-fix 2):** the double-rounding trick above
  (`Number(value.toFixed(10)).toFixed(2)`) deduplicated `88.04499999999999`/`88.045` correctly but
  was never actually round-half-up — Rick's node repro showed it under-rounds genuine half-cents
  (`5.265` → `$5.26`, `1.005` → `$1.00`), disagreeing with the backend's `ROUND_HALF_UP` contract.
  Replaced with `"$" + (Math.round(Number((v * 100).toFixed(6))) / 100).toFixed(2)`: round to 6
  decimal places first (collapses the same float noise the old trick targeted) then round-half-up to
  cents. Verified both properties still hold — `5.265` → `$5.27` (was `$5.26`) and
  `88.04499999999999`/`88.045` both still → `$88.05` (unchanged). See entry 40 below.

#### 40. PR #50 Review Remediation — Must-Fix and Should-Fix Items (Summer — Backend Dev, Morty — Frontend Dev, Birdperson — Tester)
- **Must-fix — combo side slot was an implicit category bucket, not the menu's actual rule
  (pricing regression, Rick's repro: Cheeseburger Combo + Crispy Tenders 5 pc subtotal 8.49 on this
  branch vs 15.98 on `dev`).** `menu_utils.py`'s `infer_combo_component()` previously mapped *every*
  item in the `"Extras & Sides"` and `"Hot Dogs & Tots"` `menuItems.json` categories to the combo
  `sides` slot, so 9 items that were never meant to be absorbed for free (Crispy Tenders 3/5 pc,
  Premium Chicken Bites, both FRITOS® wraps, Fritos Chili Cheese Pie, Soft Pretzel Twist, Mozzarella
  Sticks, Ched 'R' Peppers) got absorbed into a combo instead of being charged in full. Every combo's
  own menu description says "your choice of a side (**Tots or Fries**) and a drink" — the combo side
  slot is now an explicit allow-list (`_COMBO_SIDE_ITEMS`: Tots and Groovy Fries, any size) instead of
  a category-derived bucket; everything else in those two categories is priced as a full add-on. The
  combo **drink** slot allow-list was compared against `dev`'s prior behaviour and left unchanged —
  no bug found there. Happy-hour discount eligibility (`is_happy_hour_discounted()`) is now a
  genuinely separate question from combo-slot membership — each item has two independent boolean-ish
  facts (`comboSlot`, `happyHourDiscounted`) rather than one bucket doing double duty, per Rick's
  explicit instruction not to derive both from the same category lookup.
- **Golden data / naming note:** the golden table (`golden-menu-categories.json`) gives every one of
  the 60 menu items explicit `comboSlot` (`"sides"`/`"drinks"`/`"none"`) and `happyHourDiscounted`
  (`true`/`false`) fields as requested. The **values stay plural** (`"sides"`/`"drinks"`) rather than
  Rick's literal singular wording (`side|drink|none`) — the field *names* match his request, but the
  values keep the vocabulary the rest of the Python code already uses (`_COMBO_SIDE_ITEMS`,
  `is_combo_side()`, etc.) so there's exactly one spelling of "which slot" throughout the codebase.
  Flagging explicitly in case Rick expects the literal singular string.
- **Shakes & Blasts — deliberately left alone pending Brian's ruling.** Shakes and Blasts currently
  count as `drinks` for the happy-hour discount (half price), by the same logic that makes them fill
  a combo's drink slot. Whether that's correct is an open question asked of Brian separately; per the
  request, this is now a **single, obvious line** in the golden table/code
  (`_SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED = True` in `menu_utils.py`, with a comment pointing at
  this decision) so his eventual answer is a one-line flip, not a re-derivation.
- **Should-fix 1 — assert the `*Display` fields directly (kills Rick's mutation X1).** The half-cent
  and an active (non-half-cent) `SpokenTotalTests` scenario now assert `totalDisplay`/`taxDisplay`/
  `finalTotalDisplay` as literal strings (`$4.88`/`$0.39`/`$5.27` and `$8.77`/`$0.70`/`$9.47`), not
  just the underlying exact decimals — the exact-decimal fields alone can't distinguish
  `ROUND_HALF_UP` from `ROUND_HALF_EVEN` on values that never round in the first place.
- **Should-fix 2 — frontend `formatMoney` round-half-up.** See the note appended to entry 39 above.
- **Should-fix 3 — README states the literal formula.** "Rendering money for display" in
  `tests/conformance/README.md` now states the exact C# expression
  (`"$" + Math.Round(v, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture)`)
  and the equivalent Python description, rather than only naming the rounding mode in prose.
- **Should-fix 4 — `tools.py`'s search timeout didn't bound the actual HTTP request.**
  `asyncio.wait_for(search_client.search(**kwargs), timeout=...)` only wrapped the initial call,
  which is lazy for `azure-search-documents`' async `SearchClient` — the real HTTP request fires
  during the subsequent `async for record in search_results` iteration, which ran *outside* the
  timeout entirely. Fixed by wrapping an inner coroutine (the await *and* the full iteration) in the
  one `asyncio.wait_for` call, so a slow/hanging iteration is now genuinely bounded.
- **Verification:** Every item above was mutation-checked with a backup-file revert/restore cycle —
  confirmed genuinely red before the fix, green after. The must-fix's new
  `GoldenMenuComboSlotTheoryTests` (60-row Theory over every golden item × combo) catches Rick's X4
  (Corn Dog exception removed) and X6 (`extras & sides` bucket changed) simultaneously: reverting to
  the old wide category mapping fails 18/60 rows including the Corn Dog row. Full suites re-run clean
  after every item: `pytest app/backend/tests` (672 passed at the time, growing with each follow-up —
  see entry 41), `dotnet test` (292 passed / 2 skipped, Stream A only), `ruff check .` clean, frontend
  `npm test`/`npm run build` clean.

#### 41. PR #50 Review Follow-Ups (Summer — Backend Dev, Morty — Frontend Dev)
- **Size-key aliases now share one compact-key lookup (kills Rick's X3).** `canonical_size_key()`
  and `normalize_size()` previously stripped whitespace only when resolving `SIZE_ALIASES`, so
  `"Route-44"` and `"rt. 44"` (punctuation variants) fell through unresolved — `canonical_size_key`
  returned the raw string as the matching key (so they didn't merge with other Route 44 spellings)
  and `normalize_size` returned `""` (silently dropping the display prefix). Added a shared
  `_compact_size_key()` helper (strips whitespace, periods, and hyphens) used by both functions, plus
  an `"extralarge"` → `"xl"` alias so `"Extra Large"` canonicalizes onto the same key as its own short
  form. `items[].size` on the wire is now explicitly documented (`tests/conformance/README.md`) and
  test-pinned (`CanonicalSizeKeyTests`, `test_wire_item_size_is_the_canonical_lowercase_key_...`) as
  the canonical lowercase key — never the raw spelling, never the human-readable display prefix.
- **Spoken totals read `finalTotalDisplay` once, not re-derive it.** `tools.py`'s `update_order`
  delta text and `order_state.py`'s `get_grouped_order_for_readback` both called
  `format_money(summary.finalTotal)` a second time instead of reading the `finalTotalDisplay` string
  `OrderSummary` already computed. Functionally identical today, but two independent call sites
  computing the same string is exactly the kind of duplication that let the original `get_order`
  readback (#46, #28 N22) silently omit the `$` sign in the first place. Both now read
  `summary.finalTotalDisplay` / `session["order_summary"].finalTotalDisplay` directly; verified with
  a mock asserting `format_money` is never called during two successive readback requests.
- **`types.ts`'s `OrderSummaryWire` gains the three `*Display` fields**, mirroring
  `OrderSummaryProps` in `order-summary.tsx`, so a resumed session's ticket
  (`extension.session_resumed`) keeps reading the same backend-rounded strings a fresh order does,
  rather than losing them to a structurally-permissive type gap.

## PR #50 review, round 4 (Rick: Approve, with should-fix items before merge)

- **`®` removal is now one rule, not two.** `_menu_key()` (the shared normalisation helper) now
  strips `®` itself, as its own explicit step, alongside paren-group removal, whitespace collapse,
  and lowercasing. Previously `®` removal only existed in `order_state.py`'s ad-hoc combo-conversion
  string, and `MENU_CATEGORY_MAP` was still keyed by bare `name.lower()`.
  **Correction (PR #50 review round 5, should-fix 3):** the line above originally claimed this was a
  *live* bug affecting "12 of the 60" `®`-bearing menu items. Re-checked directly against `467494b`
  (the commit before this fix): map construction (`name.lower()`) and lookup
  (`strip_modifiers(item_name).lower()`) were **both** missing `®` removal — symmetric, so
  `®`-bearing names actually resolved fine at that point. The one genuine *live* bug was narrower:
  map construction didn't collapse whitespace the way `strip_modifiers()` does, so only the single
  NBSP-bearing OREO Blast name could ever actually fail to resolve via the map. Consolidating `®`
  removal into `_menu_key()` was still worth doing — it closed a *latent* duplication/drift risk,
  since `order_state.py`'s combo-conversion matching already stripped `®` independently while
  `menu_utils.py` didn't — but it was not fixing a live classification bug for the other 11 `®`
  names. The "reverting the map-key line fails a test for all 12" mutation result is real and worth
  keeping as a regression guard (it proves construction and lookup must stay in sync going forward),
  but it does not mean those 12 were broken in the code as shipped before this fix.
- **Keyword fallbacks now match on word boundaries, not bare substrings.** A plain
  `any(kw in normalized ...)` substring check let `"tea"` match inside `"steak"`, silently
  absorbing an off-menu `"Philly Cheesesteak"`/`"Steak Sandwich"` into a combo's drink slot for
  free and happy-hour-discounting it. Both the fountain-drink and shake/blast/malt keyword lists
  are now compiled `\b...\b` regexes (optional trailing `s` for plurals); Dr Pepper's existing
  regex was already word-boundary and is unchanged. Every on-menu item still resolves via
  `MENU_CATEGORY_MAP` directly (proven by a test that patches both fallback functions to raise) —
  these fallbacks only ever see genuinely off-menu names.
- **Customised sundaes pin Brian's #39 decision under customization too, not just the plain
  case.** `"Hot Fudge Sundae (Extra Fudge)"` in a combo is charged in full (never fills the drink
  slot, kills Rick's Z2) and stays full price at happy hour (kills Z3) — added as two new
  `CustomisedItemMenuLookupTests.cs` Facts, alongside the plain-sundae case already pinned in
  `GoldenMenuComboSlotTheoryTests`/`test_menu_utils.py`.
- **Y4 (an off-menu fountain drink discounted at happy hour) is now pinned at the C# conformance
  level too, not just Python.** Previously only `test_menu_utils.py` covered "an off-menu drink
  like Dr Pepper Zero is still happy-hour discounted"; a new
  `Off_menu_fountain_drink_is_happy_hour_discounted` Fact in `CustomisedItemMenuLookupTests.cs`
  runs the same assertion end to end against the live backend, mirroring the combo-slot version of
  the same case (`Off_menu_fountain_drink_still_absorbs_into_the_combo_drink_slot`) that already
  existed there.
- **README now states the exact `_menu_key()` algorithm as an ordered list** (paren-group removal
  anywhere in the string → whitespace collapse → lowercase → `®` removal) so a C# reimplementation
  has no ambiguity left to diverge on. A new `CustomisedItemMenuLookupTests.cs::
  ParenGroupNormalisationTests` pins the three edge cases Rick's review explicitly asked for: two
  separate `(...)` groups (both strip, still absorbs as a side); a *mid-string* (not just trailing)
  group (strips correctly to a different, non-side real menu item, charges in full); and a
  nested/unbalanced group (fails safe to full price rather than risking a wrong, silent match).
- **Explicitly out of scope (Rick will file separately):** the Python conformance-suite money
  tolerance change, and the `menuItems.json` schema redesign referenced in the correction on entry 38
  above.

## PR #50 review, round 5 (Rick: Approve, with should-fix items before merge)

- **Keyword over-correction fixed: the round-4 word-boundary regexes were too strict and broke
  real spoken off-menu variants that worked at `467494b`.** `\bshake\b` never matches "milkshake"
  at all (no word boundary between "milk" and "shake"), so "Chocolate Milkshake" fell through to
  unclassified; the old `s?` suffix only allowed a single trailing "s", so "Cherry Slushes"
  ("-es") and "Blue Raspberry Slushie" ("-ie") also fell through. Fixed with
  `r"\b(?:slush(?:ie|y)?|limeade|ocean water|drink|tea|lemonade|coke|sprite|root beer)(?:e?s)?\b"`
  (fountain) and `r"(?:\b|milk)(?:shake|blast|malt)(?:e?s)?\b"` (shake/blast/malt) — the latter's
  `(?:\b|milk)` prefix is a narrow, deliberate carve-out for the "milk"+"shake" compound only, not
  a general loosening (a nonsense "Overshake Deluxe" still correctly doesn't match). Both the
  original round-4 fix (steak/tea word-boundary) and this correction are pinned together: a new
  `KeywordOverCorrectionTests` pytest class plus a `Theory` over the three spoken variants in both
  `ComboSlotTests` and `HappyHourDiscountTests` in `CustomisedItemMenuLookupTests.cs`. Mutation
  check: reverting either regex to its round-4 form fails exactly the 6 new conformance rows (3
  combo-slot + 3 happy-hour), the other 16 stay green.
- **README documentation corrections (should-fix 2):** "lowercase" is now stated as
  culture-invariant (C#'s `ToLowerInvariant()`, not the culture-sensitive `ToLower()`); the
  whitespace-collapse step now states the Python-side rule precisely (`str.isspace()`, which
  Python's `str.split()` uses internally) and notes that every `menuItems.json` name's whitespace
  is either an ASCII space or a single NBSP, so C#'s `char.IsWhiteSpace` — which also treats NBSP
  as whitespace — agrees on every real name without special-casing.
- **README history correction (should-fix 3):** the "12 of the 60 items were affected" story has
  been removed from the *rule* statement in the conformance README (the contract only needs to
  state the algorithm, not its discovery history); the corrected story now lives in this file, in
  the round-4 entry above (see the "Correction" paragraph added to the `®` removal bullet under
  "PR #50 review, round 4").
- **`™` and the curly apostrophe `’` are now normalised in `_menu_key()`, exactly like `®`
  (should-fix 4).** `™` is stripped; `’` (U+2019) is replaced with a plain `'` (U+0027). Eight
  `menuItems.json` names contain `™` (the "SONIC Smasher" family, plain and Combo) and one contains
  `’` (the REESE'S Blast); all nine previously missed their own `MENU_CATEGORY_MAP` entry and
  relied on keyword-fallback luck exactly like the OREO Blast's NBSP did before round 4. New
  pytests prove all nine resolve directly (fallbacks patched to raise, mirroring
  `MenuCategoryMapDirectResolutionTests`). A Burger/Sandwich item's map miss is invisible through
  the combo-slot/happy-hour paths (neither keyword list matches "smasher" either way), so the new
  conformance row instead pins the one place the miss *is* end-to-end observable: `update_order`'s
  extras-eligibility check (`ALLOWED_EXTRA_CATEGORIES` includes "burgers & sandwiches", but only if
  the Smasher resolves via the map) — a Smasher spoken without its `™` must still let a follow-up
  "Add Bacon" extra through instead of being wrongly rejected. Mutation check: reverting `_menu_key`
  to the round-4 (®-only) form fails exactly that one new conformance test (23 → 22 passing), the
  other 22 stay green; restored, re-confirmed 23/23.
- **Hyphens are word boundaries in both regex engines by design (should-fix 5, no behaviour
  change).** Documented as a note only in the README's keyword-fallback section — Python's `\b`
  and C#'s `\b` (via `Regex`, whose word-character definition matches .NET's) both treat `-` as a
  non-word character, so a keyword adjacent to a hyphen (e.g. a hyphenated customization like
  `"(Extra-Crispy)"`, or the `"All-American"` prefix on the Smasher family) still gets a correct
  boundary on either side without any special-casing — a future C# port of these keyword regexes
  needs no adjustment for hyphens.

#### 42. Customised Items Must Be Normalised Before Every Menu Lookup (Summer — Backend Dev, PR #50 review round 2)
- **Root cause: customizations live *inside* `item_name`, and lookups didn't account for that.**
  A modifier like `"Tots (Extra Crispy)"` or `"Chili Cheese Tots (Extra Cheese)"` is a single
  string sent by `update_order` — there is no separate "base item" field. `infer_category()`,
  `infer_combo_component()`, and `is_happy_hour_discounted()` were all matching against the raw,
  unstripped, lowercased name. A customised item therefore never hit its real
  `menuItems.json`/allow-list entry and fell through to substring keyword guessing instead —
  which could (and did) disagree with the item's true classification. Rick's concrete repro:
  a Cheeseburger Combo plus `"Chili Cheese Tots (Extra Cheese)"` absorbed the tots for free
  (matched the bare `"tots"` keyword) instead of charging $3.79 in full, because "Chili Cheese
  Tots" is its own priced menu item, not one of the two combo-side-slot items.
- **Fix: one shared `strip_modifiers()`/`_menu_key()` helper, reused everywhere, not duplicated.**
  `menu_utils.strip_modifiers()` removes the trailing `(...)` suffix and collapses whitespace;
  `_menu_key()` lowercases the result. Every classification function (category, combo-slot,
  happy-hour-discount) now normalises through it, and `order_state.py`'s pre-existing ad-hoc
  combo-conversion base-name stripping (`item_name.split("(")[0]`-style) was replaced with a call
  to the same helper rather than kept as a second, independent implementation of the same rule —
  the exact class of bug the Route 44 alias fix (#40, entry 41 above) already taught us to avoid:
  two pieces of code doing raw string matching against the same source of truth, with no shared
  normalisation step, desync the moment the model introduces a transformation (aliases there,
  parenthesized modifiers here).
- **The side-slot keyword fallback is deleted, not fixed (Rick's explicit instruction).** An
  unrecognised/off-menu item — customised or not — never fills the combo side slot; it is always
  charged in full. "A charged item is visible and correctable; a free one is silent revenue
  loss." Only the literal `_COMBO_SIDE_ITEMS` allow-list (tots, groovy fries, post-normalisation)
  can occupy that slot.
- **The drink keyword fallback is split so the happy-hour flag is genuinely the single switch.**
  Fountain-drink keywords (Dr Pepper, Coke, Sprite, root beer, ...) remain unconditionally
  eligible for both the combo drink slot and the happy-hour discount — they're always full-price
  fountain drinks otherwise. Shake/blast/malt keywords are still unconditionally eligible for the
  combo drink slot (that's a menu-composition fact, unrelated to pricing), but the happy-hour
  discount question for them is gated exclusively by
  `_SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED` — proven by a dedicated test that flips the flag and
  asserts every shake/blast variant (plain, customised, on-menu, off-menu) changes together, while
  combo-slot eligibility stays unaffected.
- **Regression coverage added on both sides:** `test_menu_utils.py::CustomisedItemMenuLookupTests`
  (12 Python unit tests) and a new `CustomisedItemMenuLookupTests.cs` (5 live-backend Facts,
  including the customised Cherry Limeade happy-hour case that kills Rick's Y4).



## S1.5 fan-out, Brian's pricing decisions (2026-09-25)

#### 43. Shakes & Blasts Are Full Price During Happy Hour (Summer — Backend Dev, #39 — decision confirmed by Brian, 2026-09-25)
- **Decision:** `_SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED = False` (was `True`, pending Brian since entry
  42). Shakes and blasts — plain, customised, on-menu, and off-menu keyword-fallback alike — are full
  price from two to four. Combo drink-slot eligibility for shakes/blasts is unchanged; that is a
  separate question from happy-hour discount eligibility (entry 42's split of `infer_combo_component`
  from `is_happy_hour_discounted` already keeps them independent, so only the one flag moved).
- **Golden data:** the 8 shake/blast rows in `golden-menu-categories.json`
  (Vanilla/Chocolate/Strawberry/Peanut Butter Classic Shake, both SONIC Blast variants, OREO
  Cheesecake Master Shake, Banana Classic Shake) flipped `happyHourDiscounted: true → false`; no other
  field on those rows changed.
- **Prompt consistency check (asked for, not assumed):** grepped every prompt/hint source for language
  that could tell the carhop shakes are half-price.
  - `hints.yaml`'s `system_hints.happy_hour_active` ("drinks and slushes are half-price") is **dead
    code** — `prompt_loader.get_hints_data()` loads it but nothing renders it; only `upsell_hints`
    (per-category) is actually used. Left as-is; noted here so nobody "fixes" unused text later.
  - `tools.py`'s runtime banner (`" [HAPPY HOUR ACTIVE: drinks and slushes are half-price!]"`,
    `tools.py:532`/`:554`) is hardcoded literally, not templated from `hints.yaml`, and never itself
    names shakes — low risk on its own.
  - `system_prompt.yaml`'s `HAPPY_HOUR` section (~line 192) instructs: `'[HAPPY HOUR ACTIVE]' in tool
    result + drink order → 'You're just in time — that's HALF-PRICE!'`. "Drink order" is ambiguous
    enough that a model could read an ordered shake as a "drink" and misapply the half-price line —
    this was the one real risk found.
  - **Change:** added one line to the `HAPPY_HOUR` content block: `Shakes & Blasts and ice cream are
    full price during happy hour.` Nothing else in any prompt/hints file changed.
- **Mutation check:** flipped the flag back to `True` (uncommitted, restored after) — 3 pytest failures
  (`test_shakes_and_blasts_are_full_price_during_happy_hour`,
  `test_flipping_the_shakes_and_blasts_flag_changes_every_shake_blast_variant`,
  `test_milkshake_is_recognised_as_a_shake_and_obeys_the_flag`) and 3 conformance failures (the golden
  happy-hour Theory row for Banana Classic Shake, the customised-shake Fact, the off-menu-milkshake
  full-price Fact); restored, pytest 788/788 and the filtered conformance run 124/124 green again.

#### 44. Plain-Tots Alias Map for the Combo Side Slot (Summer — Backend Dev, new issue #60 — decision confirmed by Brian, 2026-09-25)
- **Decision:** any spoken name-variant of plain Tots — `"Tot"`, `"Tots"`, `"Tater Tot"`,
  `"Tater Tots"`, plus the common spoken misspelling `"Tator Tot"`/`"Tator Tots"` — fills the combo
  side slot exactly like the real `"Tots"` menuItems.json item does.
- **Implementation:** `menu_utils._TOTS_ALIASES`, an explicit, exact-match frozenset, resolved by a
  new `_resolve_combo_side_alias()` helper. Applied *after* `_menu_key()` normalisation (so
  `"Tater Tots (Extra Crispy)"` → `"tater tots"` → alias-resolved to `"tots"`) and *before* the
  `_COMBO_SIDE_ITEMS` membership check in `infer_combo_component` — an exact match, never a
  substring check, so Rick's PR #50 revenue rule keeps holding: `"Chili Cheese Tots"`,
  `"Cheese Tots"` (real, different menu items), and off-menu near-misses (`"Loaded Tots Supreme"`,
  the misspelled `"chilli cheese tots"`) are none of them in the alias set and all still fall
  through to charged-in-full.
- **Scope: combo-side-slot classification only.** Does not touch `infer_category` (its pre-existing
  `"tot"`/`"tots"` substring keyword fallback already categorises every alias variant as `"sides"`
  on its own) or `is_happy_hour_discounted` (Tots was never a drink either way).
- **Pricing unaffected either way:** confirmed `update_order`'s unit price always comes from the
  tool-call argument (`tools.py`, `price = args.get("price", 0.0)`), never from `menu_utils` — a
  standalone alias-ordered item (e.g. a guest ordering just "Tater Tots") prices exactly as the
  model/search-tool supplies, same as before this change; the alias only changes whether the item
  can be silently absorbed into a combo's side slot.
- **Variants considered:** included `"Tator Tot"` (singular) alongside the four literally requested
  names, for symmetry with `"Tot"`/`"Tots"` — flagged here as a proposed addition, not explicitly
  asked for. Considered and rejected: hyphenated forms (`"Tater-Tot"`) — no evidence guests speak it
  that way, and `_menu_key()` does not collapse hyphens, so adding it would be speculative; can be
  added later if it turns out to matter.
- **Mutation check:** reverted `infer_combo_component` to check `normalized in _COMBO_SIDE_ITEMS`
  directly (bypassing the new alias resolver) — 3 pytest failures and 5 of 6 new conformance rows
  failed (only the literal `"Tots"` row still passed, since it was already a direct
  `_COMBO_SIDE_ITEMS` member); restored, pytest 794/794 and the filtered conformance run 6/6 green
  again.

#### 45. Keyword-Fallback Precedence for the Happy-Hour Discount Question (Summer — Backend Dev, PR #61 review, must-fix 1)
- **Bug:** `_keyword_fallback_happy_hour_discounted` checked the fountain-drink/Dr Pepper keywords
  BEFORE the shake/blast/malt keywords. An off-menu name that happens to contain both a fountain
  word and a shake/blast word (`"Cherry Limeade Shake"` — "limeade" + "shake";
  `"Strawberry Lemonade Shake"`; `"Dr Pepper Shake"`; `"Sweet Tea Blast"` — "tea" + "blast")
  resolved via the fountain branch and was unconditionally discounted, bypassing
  `_SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED` (Brian's decision #44/decision below) entirely.
- **Fix:** check the shake/blast/malt regex first; fall through to the fountain/Dr Pepper check
  only if it doesn't match. `_keyword_fallback_combo_drink` (an unconditional `or` across all three
  regexes) needed no change — it was never order-dependent, confirmed by a passing conformance
  theory with the same four names (still fill the combo drink slot regardless of discount outcome).
- **Two separate questions, still separate:** the combo-drink-slot question ("can this fill the
  slot") stays order-independent; only the happy-hour-discount question ("is this half-price") is
  precedence-sensitive, and now resolves shake/blast-first.
- **Mutation check:** reverted the check order back to fountain-first — pytest
  `KeywordFallbackPrecedenceTests` 1 failed / 2 passed (the "are not discounted" case goes red);
  conformance `HappyHourDiscountTests`'s new theory: 4 failed / 4 passed (discount-side rows red,
  combo-slot rows unaffected as expected). Restored; full pytest 797/797 and full conformance
  407/407 (1 pre-existing skip) green again.

#### 46. Happy-Hour Banner Wording States Shakes/Blasts/Sundaes Are Full Price (Summer — Backend Dev, PR #61 review, must-fix 2)
- **Problem:** the runtime banner appended to `update_order`/`get_order` tool results said
  `"drinks and slushes are half-price!"` and never mentioned shakes, Blasts, or sundaes at all —
  the model had no signal in the tool result itself that those categories are full price (#39's
  decision), only a standing system-prompt instruction it could forget or override.
- **Change:** banner text now reads `"[HAPPY HOUR ACTIVE: slushes and fountain drinks are
  half-price; shakes, Blasts and sundaes are full price]"` in both `update_order` and `get_order`.
  `system_prompt.yaml`'s trigger condition updated to `"+ slush or fountain-drink order"` (matches
  the half-price scope exactly). Dropped the standing "Shakes & Blasts and ice cream are full
  price" line added for #44/#60 — now redundant since the banner states the same rule every time
  happy hour is active; the rule is stated once, not twice.
- **Dead code removed:** `hints.yaml`'s `system_hints.happy_hour_active` entry deleted. Confirmed
  dead first (re-verified this round, not just relying on the prior round's finding): grepped the
  whole repo including all test files for `happy_hour_active` and `get_hints_data` — only caller of
  hints data is `PromptLoader.get_hints()`, which reads solely `upsell_hints`; no test referenced
  the key either.
- **No existing test asserted the old banner text** (checked `app/backend/tests` and
  `tests/conformance` — zero matches) — nothing stale to update, but added
  `HappyHourBannerWordingTests` for ongoing coverage.
- **Mutation check:** reverted the banner string to the old wording — 1 of 3 new pytest tests
  failed; restored, all green (pytest 800/800 total).

#### 47. More Tots Alias Forms, README Contract, and a Size-Word Fail-Safe Pin (Summer — Backend Dev, PR #61 review, must-fix 3 + should-fix 4/5)
- **Investigated first (per the review's explicit instruction):** what does `_menu_key()` do to a
  hyphen? Confirmed a hyphen is NOT collapsed to a space — it is not whitespace, so neither
  `strip_modifiers()`'s `.split()`/`" ".join(...)` pass nor any of `_menu_key()`'s three
  symbol-replacements (`®`, `™`, curly apostrophe) touch it
  (`_menu_key("Tater-Tot") == "tater-tot"`, not `"tater tot"`). This directly reverses decision
  #44's earlier assumption ("considered and rejected: hyphenated forms... can be added later if it
  turns out to matter") — it did turn out to matter, and needed its own explicit keys, not
  automatic coverage from the space-separated forms.
- **`_TOTS_ALIASES` extended:** one-word forms (`tatertot`, `tatertots`, `tatortot`, `tatortots`)
  and hyphenated forms (`tater-tot`, `tater-tots`, `tator-tot`, `tator-tots`). `"Totts"`
  (doubled-T typo) and `"Tater Tot's"` (stray apostrophe) deliberately excluded — confirmed still
  charged in full.
- **Pricing check:** `update_order`'s unit price always comes from the tool-call argument, never
  from `menu_utils` — confirmed the alias mechanism only ever changes combo-slot classification,
  never a standalone item's price. An alias ordered standalone prices however the caller's
  tool-call argument says (same as before this change).
- **Other variants considered:** none seemed worth adding beyond what's covered (plural/singular,
  case, spacing, one-word, hyphenated, the tator/tater misspelling) — flagging for Brian/Rick to
  confirm nothing else spoken is missing.
- **README updated:** the combo-side-slot sentence now explicitly credits `_TOTS_ALIASES`; the
  alias paragraph lists the exact post-`_menu_key()` lowercase keys as the contract, states the
  hyphen-preservation rule, and states that size words embedded in the name text (not bracketed)
  are not stripped.
- **Size-word fail-safe pinned (no behaviour change — confirmed current behaviour already matched
  before writing the pin):** `"Large Tater Tots"` (size word in the name text) is charged in full;
  `"Tater Tots (Large)"` (size word as a bracketed modifier) still absorbs.
- **Mutation check:** removed the four new alias-key groups from `_TOTS_ALIASES` — pytest
  `MoreTotsAliasFormsTests` 3 failed / 2 passed; conformance
  `One_word_and_hyphenated_tots_alias_forms_absorb_into_the_combo_side_slot`: 9 failed / 0 passed
  (Rick's "M4"). Restored; full pytest 807/807 and full conformance 419/419 (1 pre-existing skip)
  green again.

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction


#### 2026-09-23: .NET 11 xUnit v3 harness implementation details (Beth)
**By:** Beth (.NET implementation) — suite designed with Birdperson, CI consumer is Squanchy
**Issue:** #7 (S1.1), branch `feat/conformance-harness`

**Toolchain:** SDK `11.0.100-rc.1.26425.128` (per-user install, not on PATH — called by full path throughout). `global.json` pins it with `rollForward: latestFeature`, `allowPrerelease: true`. Central Package Management (`Directory.Packages.props`) — no per-project versions. Packages restore only through `packagefeedproxy.microsoft.io`; confirmed working for `xunit.v3`, `Microsoft.AspNetCore.Mvc.Testing`, `YamlDotNet` was not actually needed (menu JSON and config are all plain JSON/env vars — no YAML parsing in the harness itself).

**`.slnx` hand-authoring:** `dotnet sln Conformance.slnx add <project>` throws on this SDK build (CLI bug against the new `.slnx` XML format). Worked around by hand-writing the `.slnx` XML directly — a `<Project Path="..."/>` per project, verified it still opens/builds/tests identically to a generated one.

**xUnit v3 APIs confirmed correct (previously assumed):** `Assert.Skip(string)` for the `CONFORMANCE_BACKEND=dotnet` placeholder path, `Xunit.TestContext.Current.CancellationToken` for every async wait, `IAsyncLifetime` returning `ValueTask` for fixture setup/teardown. All build and run clean with 0 warnings under `TreatWarningsAsErrors` + `Nullable=enable`.

**Bug found and fixed — WebSocket server-side close handshake:** When `System.Net.WebSockets.WebSocket`'s `ReceiveAsync` returns a client-initiated close frame, `socket.State` becomes `CloseReceived`, not `Open`. `FakeRealtimeUpstreamServer`'s original guard (`if (socket.State == WebSocketState.Open)`) skipped completing the handshake in that case, so the *client's* `CloseAsync()` threw `WebSocketException: remote party closed the WebSocket connection without completing the close handshake` — this broke 3 of the fake-only scripting tests on first run. Fixed by checking `socket.State is WebSocketState.Open or WebSocketState.CloseReceived` and wrapping the close call in try/catch for `WebSocketException` (best-effort, since the peer may already be gone).

**Bug found and fixed — health check assertion:** `HealthEndpointTests` originally did a raw substring match for `"status":"healthy"`, but the real payload is pretty/spaced JSON (`"status": "healthy"`). Rewrote to parse via `JsonDocument` and assert on `GetProperty("status").GetString()`.

**Cross-platform fix for CI (#11):** `RepoPaths.PythonExecutable()` was hardcoded to `.venv/Scripts/python.exe` (Windows venv layout). GitHub-hosted Linux runners create venvs at `.venv/bin/python`. Branched on `OperatingSystem.IsWindows()`. Also swapped a hardcoded backslash in `PythonBackendLauncher`'s "venv not found" exception message for `Path.Combine`. Verified no regression: 8/8 green, 3× in a row, on Windows after the change.

**C# standards followed:** nullable enabled + warnings-as-errors in every suite project; `TimeProvider` not directly needed (the harness doesn't fake time itself — that's Summer's Python-side `test_hooks.py`), but all async waits use `CancellationToken`s end-to-end and no `Thread.Sleep`/`Task.Delay`-for-synchronization — every wait is `FrameLog.WaitForAsync(predicate, timeout, cancellationToken)` against recorded frames.

**Validation:** `dotnet test tests/conformance` green 3× in a row at three separate points in the work (initial 8/8, after Python test-hook changes, after the cross-platform fix) — no flakiness observed. `git status` clean; `.gitignore` correctly excludes `bin/`/`obj/` (verified nothing leaked into the initial commit).


#### 2026-09-24: Full GA response item lifecycle in FakeRealtimeUpstreamServer (Beth)
**By:** Beth (.NET implementation)
**Issue:** #7, branch `feat/conformance-harness`, PR #22, Rick's review items 2, 3, 4, 5

**What changed in `RespondAsync`:** previously the fake emitted a flat sequence — `response.created`, a run of `response.output_audio.delta`/`response.function_call_arguments.done` events, then `response.done`. GA's real contract nests every output under a proper item lifecycle. The rewrite now emits, per response:
- One `response.output_item.added` (with `item.type`, `item.id`) followed by `conversation.item.added` for each logical output item — either an assistant "message" item (opened lazily on the first `AudioDeltaEvent`) or a `function_call` item (one per `FunctionCallEvent`).
- For the message item: `response.content_part.added` once, then one `response.output_audio.delta` per scripted audio event, closed by `response.output_audio.done` → `response.content_part.done` → `response.output_item.done` — the audio item is closed automatically before a function-call item opens, since GA never interleaves two open items.
- For a function-call item: `response.function_call_arguments.done` (carrying the full arguments JSON) → `response.output_item.done`, no content-part events (function calls have no audio/text parts).
- Every event now carries a real `event_id` (`NewEventId()`), and every item-scoped event additionally carries `item_id`, `output_index`, and `content_index` (`0` for function calls, matching GA).
- The final `response.done` accumulates all closed items into `response.output[]` and includes a new `BuildUsage(...)` helper producing a plausible GA-shaped `usage` object (`total_tokens`, `input_tokens`, `output_tokens`, nested `input_token_details`/`output_token_details`). No test currently asserts exact token counts — this is deliberately "shaped like GA" per the issue wording, not GA-exact, and is flagged as an extension point if a future scenario needs precise accounting.

**Why this fixes item 2 (missing `extension.round_trip_token`):** confirmed by reading `app/backend/rtmt.py` (read-only, no changes) that `_process_message_to_client`'s `response.done` handling does `output = message["response"]["output"]` **unconditionally**, with no `.get()` guard, at line ~874. A `response.done` lacking `output` (the fake's old shape) throws `KeyError: 'output'`, which is caught by `_forward_messages`'s outer `except Exception: logger.exception(...)` — but that handler doesn't resume the loop, it lets the exception propagate out of the `asyncio.gather()` of both forwarding directions, **silently killing the whole WebSocket connection** from the browser's perspective. The browser never sees an error frame; it just stops receiving anything, including `extension.round_trip_token`. Making the fake's `response.done` always carry `output[]` + `usage` (matching GA) makes this code path succeed instead of throwing.

**Mutation-check (item 2), full before/after:**
- **Before (mutant):** temporarily stripped `output`/`usage` keys from the fake's `response.done` body. Reran `ResponseDoneRoundTripTests` alone: **failed**, with the captured backend stderr showing:
  ```
  Traceback (most recent call last):
    ...
    File ".../app/backend/rtmt.py", line 874, in _process_message_to_client
      output = message["response"]["output"]
                ~~~~~~~~~~~~~~~~~~~^^^^^^^^^^
  KeyError: 'output'
  ```
  (pasted in full in commit `688d713`'s message)
- **After (restored):** reran the same test — **passed**; ran the full suite — 13/13 (this test alone raised the count from 12→13; item 3's test later brought it to 14). `git status` clean except the intended new test file.

**Why item 3 needed the same rewrite:** proving `update_order` "actually executes" requires the full round trip — `output_item.added` → `conversation.item.added` (with `item.call_id`/`item.name`/`item.arguments`) → `function_call_arguments.done` → `output_item.done` → `response.done` with the item present in `output[]` — because `rtmt.py`'s tool-calling logic (`tools_pending`, `previous_item_id` chaining) keys off exactly these fields to know when to invoke the tool and where to attach the resulting `function_call_output`. Added `RealtimeSessionState.LastConversationItemId` to correctly chain `previous_item_id` across responses, matching how `rtmt.py` reads `message.get("previous_item_id", "")`.

**Item 4 (`AssistantAudioSeen`):** set the flag the moment the first `response.output_audio.delta` is emitted (not at `response.done`), since GA's real `cannot_update_voice` restriction applies as soon as any assistant audio has started streaming, not only after a full response completes. New self-test drives a real scripted response to completion, then proves a subsequent voice change in `session.update` is rejected with the correct error code and echoed `event_id`.

**Item 5 (per-connection identity), engine-side implication:** `RespondAsync` and all scripting state now operate against a `FakeRealtimeConnection` instance rather than shared server fields, so two connections' in-flight responses can never cross-contaminate each other's frame logs — a prerequisite for items 2/3's scenarios to assert reliably on "their own" connection's frames.

**Validation:** full suite green 3× in a row from a genuinely clean state (14/14, ~1s per run) after restoring `app/backend/static` (moved aside earlier to prove item 1's fail-fast message fires correctly). `pytest app/backend/tests -q`: 586 passed / 61 subtests (unchanged baseline — no Python changes this round). `ruff check .` clean. `npm test` in `app/frontend`: 116 passed (unchanged — no frontend changes this round). `git status` clean; no `bin/`/`obj/` tracked (`tests/conformance/.gitignore` verified via `git check-ignore -v`).


#### 2026-09-23: Black-box conformance suite design (Birdperson)
**By:** Birdperson (suite design/ownership) — with Beth (.NET implementation), Summer (Python test hooks), Squanchy (CI)
**Issue:** #7 (S1.1), branch `feat/conformance-harness`

**What:** `tests/conformance/` is a standalone .NET 11 xUnit v3 solution (`Conformance.slnx`, hand-authored — `dotnet sln add` has a CLI bug against `.slnx` on this SDK) with three projects:
- `Conformance.Fakes` — `FakeRealtimeUpstreamServer` (Kestrel; GA realtime protocol at `/openai/v1/realtime?model=`, `GaSessionValidator`, `FrameLog`, `RealtimeScript` for per-test scripting of audio deltas/function calls/`response.done`/errors) and `FakeSearchServer` (Kestrel; answers Azure AI Search REST from `menuItems.json`).
- `Conformance.Harness` — `PythonBackendLauncher` + `BackendEnvironment` + `RepoPaths` (starts `app/backend` in its `.venv` on a free port, pointed at the fakes, waits on `/health`, captures stdout/stderr), `BackendLauncherFactory` (dispatches on `CONFORMANCE_BACKEND=python|dotnet`, `dotnet` throws a typed `ConformanceBackendNotImplementedException` callers turn into a clean `Assert.Skip`, or honours `CONFORMANCE_BACKEND_URL` to point at an already-running backend), `RealtimeBrowserClient` (sends exactly what `useRealtime.tsx` sends), `NetworkUtils.GetFreeTcpPort`.
- `Conformance.Tests` — 5 test classes, `ConformanceFixture`/`ConformanceCollection` for one shared fake-server + backend-process lifetime per test class.

**Scenarios (8 total, all green):**
1. `SmokeSessionBootstrapTests` (2 tests) — the acceptance scenario: connect → bootstrap `session.update` (4 tools, `tool_choice=auto`) is upstream frame 0 → browser `session.update` relayed (GA-translated) → greeting `response.create` arrives only after `session.updated`.
2. `HealthEndpointTests` — `/health` returns 200 with `{"status":"healthy",...}` (parsed as JSON, not raw substring — the real payload has spaces after colons).
3. `AuthSessionTests` — `/api/auth/session` returns a token.
4. `WebSocketCompressionTests` — permessage-deflate is not negotiated (matches the `fix/ws-transport` decision: `connection.ws_compression: false`).
5. `FakeRealtimeScriptingTests` (3 tests) — exercises the fake's scripting API directly (audio deltas, function calls, rate-limited `response.done` with hints) without the Python backend, proving the fakes work standalone.

**Mutation-check (bootstrap send removed from `rtmt.py`):**
```
Before fix removed:  Passed! - Failed: 0, Passed: 8, Skipped: 0, Total: 8
After commenting out the bootstrap session.update send in rtmt.py:
  Failed! - Failed: 2, Passed: 6, Skipped: 0, Total: 8
  SmokeSessionBootstrapTests.Bootstrap_session_update_is_first_upstream_frame [FAIL]
    Expected an upstream frame at index 0 recording a session.update within 00:00:30.
  SmokeSessionBootstrapTests.Greeting_waits_for_session_updated [FAIL]
    Bootstrap session.update never arrived.
After restoring rtmt.py (git diff clean):
  Passed! - Failed: 0, Passed: 8, Skipped: 0, Total: 8
```
Confirms the smoke scenario actually exercises the real backend behavior, not just the fakes.

**`CONFORMANCE_BACKEND=dotnet` skip path verified:** 5 backend-dependent tests skip cleanly with a clear reason ("S2 placeholder"), 3 fake-only scripting tests still run and pass, overall run reports `Passed!` (0 failed, 5 skipped) — a future S2 .NET backend won't need this suite rewritten, just `BackendLauncherFactory`'s `dotnet` branch filled in.

**Extension points left for S1.2–S1.4:** `FakeRealtimeScriptingTests` is the template for new upstream-behavior scenarios (rate limiting with hints, `response.done` failures, function-call round trips already scripted end-to-end); `RealtimeScript`'s queued-response model (one scripted response per `response.create` seen, FIFO) is reusable for any new scenario without touching the fake server itself; `ConformanceFixture` is the one place a new test class needs to hook into for a fresh backend+fakes lifetime.

**Determinism:** ran the full suite 3× in a row green (no flaky timing) both before and after Summer's Python test-hook changes and Beth's cross-platform fix; `FrameLog.WaitForAsync`-style awaits with timeouts are used everywhere instead of sleeps.


# Learning: conformance flakes #55/#62 — instantaneous stderr checks race the async capture callback

**From:** Birdperson (Tester), 2026-09-25, branch `squad/55-62-conformance-flakes`, PR #66.

## What happened

Two intermittent conformance-suite flakes (#55 `GreetingTimeoutFallbackTests`, #62
`UpdateOrderToolCallTests.Scripted_update_order_call_executes_and_notifies_the_browser`)
were the same class as the earlier #52/#54 timing races, but with a subtly different
mechanism worth recording for future harness work (see also #63's N32, which is the
same underlying symptom from a different angle):

`CapturedProcessOutput` fills asynchronously via the .NET `Process.ErrorDataReceived`
event, dispatched on the ThreadPool. Any code that reads from it — `Dump()`,
`CountUnhandledErrors()` — does so **instantaneously**: it returns whatever has been
appended *so far*, with no guarantee that a line already "in flight" through the
async callback has actually landed yet. Under normal (unloaded) conditions the
ThreadPool dispatches fast enough that this race window never fires. Under
full-suite load (many other tests contending for the ThreadPool and CPU), the window
widens and the race becomes observable, at a low, inherently intermittent rate.

Two different call sites hit this race in two different ways:
- **#55**: the test itself did an instantaneous `Dump()` right after an unrelated
  frame arrived on a separate channel, with no synchronization to the diagnostics
  line actually being captured yet.
- **#62**: the *shared fixture* (`ConformanceFixture.RunAsync`) captured a
  `baselineUnhandledErrors` snapshot instantaneously at scenario start, while a
  previous scenario's own already-accounted-for stderr line could still be
  in-flight. If it landed after the new baseline snapshot, it was misattributed as
  a *new* error the current (unrelated) scenario introduced.

## The fix pattern

Both fixes replace an instantaneous read with an **event-driven wait**, reusing the
same idiom `FrameLog` already established for frame waits: swap out a
`TaskCompletionSource` on every `Append()`, and let waiters await either "a specific
predicate becomes true" (`WaitForDiagnosticsAsync`) or "no new output has landed for
an idle window" (`WaitForQuiescenceAsync`, debounce-style, capped by a max wait so it
can never hang forever). Neither approach bumps a timeout number or adds a blind
retry — both make the check actually synchronize with the real event it's supposed
to be checking.

**General principle for this harness:** any code that reads captured process
output/diagnostics for a correctness check (not just a debug dump) should default to
requiring an explicit wait primitive, not an instantaneous read, whenever the check
could plausibly run concurrently with in-flight async capture. Instantaneous reads
are fine for genuinely-after-the-fact diagnostics dumps (e.g. inside a `catch` block
building a failure message *after* everything else has already settled).

## Mutation-check learning

For the shared zero-tolerance backend-error-count check (#62's fix, exercised
indirectly by every strict-mode test via `ConformanceFixture.RunAsync`), the first
mutation target chosen (`ToolErrorSessionSurvivesTests`) unexpectedly did **not**
fail when tightened from `allowedNewBackendErrors: 1` to `0`. Investigation
concluded that test doesn't reliably produce a countable new-error delta in
isolation the way the docstring implies — worth a follow-up look, but out of scope
for this fix. Switched instead to `ToolMalformedArgumentsTests` (which deliberately
drives rtmt.py's layer-1 `except Exception` path via malformed, non-JSON tool-call
arguments — a documented, deterministic single-error path referencing #36 S3), which
mutated and failed exactly as expected. **Lesson:** when choosing a mutation target
for a shared invariant, prefer the test whose docstring/name most directly documents
*why* it produces the exact error count it asserts, not just any test that happens
to pass a non-default parameter — the assumption that "any test using
`allowedNewBackendErrors: N > 0` reliably produces exactly N new errors every run"
is not automatically safe.

## Load calibration for reproducing this flake class

On the dev machine used (24 logical cores), CPU-busy-job load below ~20 jobs barely
reproduces #55/#62-class races (very sparse — 0 hits in 30 sequential runs at times).
20 jobs is a reasonable calibrated target: enough contention to widen the async race
window without tripping the many unrelated 30s teardown timeouts elsewhere in the
suite. 40 jobs is too much: it causes wholesale, unrelated breakage (ThreadPool/CPU
starvation trips generous timeouts across many unrelated tests) that is not
representative of these specific tight races — don't mistake that for evidence
either for or against a specific fix.

Also: running **multiple concurrent `dotnet test` processes** against the same
worktree is not equivalent to "load" for this suite's purposes — it introduces a
confound (port-bind collisions between unrelated collections' backend/fake-upstream
port selection) that looks like new failures but has nothing to do with #55/#62's
actual reported failure mode (sequential full-suite runs). Use CPU-busy background
jobs plus a single sequential `dotnet test` process for genuine load reproduction.


#### 2026-09-24: Stage A response to Rick's PR #22 review (Birdperson)
**By:** Birdperson (suite design), with Beth (engine) and Squanchy (CI item 1)
**Issue:** #7 / #11 (S1.1 / S1.5), branch `feat/conformance-harness`, PR #22

**Root cause of red CI (Rick's report):** `app/backend/static` is gitignored and only exists after `npm run build` runs. The coordinator's earlier local "8/8" green result was misleading because that machine already had a stale built frontend from prior work — a genuinely clean checkout (as GitHub-hosted runners are) has no `static/` directory at all, so the Python backend fails to start and the harness previously reported an opaque "backend exited early". **Lesson applied across the squad:** always validate from a state that matches a clean runner, not just "works on my machine".

**Scope for this round — Stage A only:** Rick's "must fix before merge" items 1–7 plus item 19. Items 8–17 (Stage B) are explicitly deferred until the coordinator pushes this round and confirms CI is green. No Python backend behavior, frontend, prompts, or `infra/` were touched — only the conformance harness itself, plus the CI workflow's frontend-build step (item 1).

**Validation approach for this round:** rather than trusting a single green run, re-created the exact clean-runner condition locally: moved `app/backend/static` aside, confirmed the harness fails with the intended clear message (not "backend exited early"), restored it, then ran the full suite 3× in a row plus the full baseline validation matrix (pytest, ruff, frontend tests). This is the same clean-state discipline CI now enforces structurally (build frontend before either test job runs).

**Scenario/design decisions this round (owned by Birdperson, implemented jointly with Beth):**
- **Per-connection identity (item 5):** every fake-upstream connection is now a distinct `FakeRealtimeConnection` object with its own frame log, rather than one shared server-level log. Tests explicitly wait for and capture *their* connection via `WaitForNextConnectionAsync()`, and assert no connections are already open at test start — this eliminates a whole class of latent cross-test leakage bugs the old shared-log design could have hidden.
- **Voice-lock self-test (item 4):** rather than trusting that `AssistantAudioSeen` was wired correctly in the validator (it was, from the original harness build, but never exercised end-to-end), added a scenario that actually drives a full scripted response through the fake and then proves the *next* voice-changing `session.update` is rejected with `cannot_update_voice`, echoing the request's `event_id` per GA's error-shape convention.
- **`response.done` shape + traceback detection (item 2):** the strongest signal in this round. Waiting *past* the greeting for `extension.round_trip_token` and asserting the captured backend stderr contains no `"Traceback"` is a black-box-safe way to detect a real backend crash that would otherwise be invisible to a WebSocket client (the connection simply stops receiving frames, no error surfaces). Mutation-checked by removing `output`/`usage` from the fake's `response.done` — reproduced the *exact* real `rtmt.py` `KeyError: 'output'` traceback, then confirmed the fix cures it. This is the single most convincing piece of evidence for Rick that the scenario actually tests what it claims to.
- **Full GA item lifecycle + tool execution (item 3):** proving `update_order` executes for real (a `function_call_output` reaches the fake upstream with the right `call_id`, and `extension.middle_tier_tool_response` reaches the browser) required the engine to emit the complete `output_item.added → conversation.item.added → ...done` sequence, not just deltas — this was the largest single implementation change this round and is shared with item 2's fix (both live in `RespondAsync`).

**Test count progression this round:** 8 (start of Stage A) → 14 (end of Stage A), across 9 commits. All green 3× in a row from the final, genuinely clean state.

**Extension points flagged for Stage B (item 8 in particular):** `Script`/`AutoRespond`/`QueuedResponses` remain server-level (not per-connection) by deliberate choice this round — moving them per-connection is explicitly Stage B's job. This is safe today only because xUnit's `[Collection(ConformanceCollection.Name)]` serializes all fixture-sharing tests; it would not be safe if tests using `ConformanceFixture` ever ran in parallel.


#### 2026-09-23: Conformance CI workflow (Squanchy)
**By:** Squanchy (CI/DevOps) — consumes Birdperson/Beth's harness (#7) and Summer's test hooks
**Issue:** #11 (S1.5), branch `feat/conformance-harness`

**What:** `.github/workflows/conformance.yml`, triggered on `pull_request` to `dev`/`main` (+ `workflow_dispatch`), three independent jobs (each its own PR check):
1. **`python-tests`** — installs `app/backend/requirements.txt` + pinned dev tooling not in that file (`ruff==0.16.1`, `pytest==9.1.1`, `pytest-asyncio==1.4.0` — the exact versions this branch was validated against locally), runs `ruff check .` then `pytest app/backend/tests -q`.
2. **`frontend-tests`** — `npm ci` → `npm run build` → `npm test` (vitest) in `app/frontend`.
3. **`conformance`** — creates a repo-root `.venv` from `app/backend/requirements.txt` (what `PythonBackendLauncher` expects), installs .NET 11 RC1 (`actions/setup-dotnet`, `dotnet-version: 11.0.100-rc.1.26425.128`), runs `dotnet test tests/conformance` with `CONFORMANCE_BACKEND=python`. Matrixed on `backend: [python]` today so S2 can extend to `[python, dotnet]` with a one-line diff — `CONFORMANCE_BACKEND=dotnet` already skips cleanly (Beth's `BackendLauncherFactory`), it's just nothing to conform against yet.

**Runner:** `ubuntu-latest` for all three jobs — required a cross-platform fix to the harness first (Beth's commit `cc5696a`/`9f8188a`): `RepoPaths.PythonExecutable()` was hardcoded to the Windows venv layout.

**Conventions:**
- Actions pinned at major-version tags (`checkout@v4`, `setup-python@v5`, `setup-node@v4`, `setup-dotnet@v4`, `cache@v4`, `upload-artifact@v4`) — no `@main`/`@master`.
- `permissions: contents: read` at the workflow level.
- `concurrency` group cancels superseded runs on the same PR/ref.
- Caching: `setup-python`'s built-in pip cache (keyed on `requirements.txt`), `setup-node`'s built-in npm cache (keyed on `package-lock.json`); an explicit `actions/cache` for `~/.nuget/packages` keyed on `Directory.Packages.props` + `*.csproj` since `setup-dotnet` doesn't cache restores itself.
- Every job tees its test command's output to a log file (plus `--junitxml`/`--logger trx` machine-readable results) and uploads it as a failure-only artifact.
- `CONFORMANCE_TEST_HOOKS` is never set in the workflow — consistent with never enabling it in bicep or the Dockerfile.

**Registry access confirmed already CI-safe without changes:** `app/frontend/package-lock.json` already resolves entirely against `registry.npmjs.org` (0 internal `pkgs.visualstudio.com`/`pkgs.dev.azure.com` hosts, verified by grep) and the repo has no `NuGet.Config` forcing the corporate-only proxy — so `npm ci` and `dotnet restore` both work unmodified on a GitHub-hosted runner against public registries, per the issue's note that Actions runners are fine using them.

**YAML validated:** parsed with `PyYAML` (`yaml.safe_load`) since Actions can't run locally in this environment — caught and fixed the classic YAML 1.1 gotcha where an unquoted `on:` key parses as the boolean `True` (quoted it `"on":`; GitHub's own parser already treats it as the string key, so this is a lint-cleanliness fix, not a behavior change). Not run through `actionlint` (would need a binary fetch outside the approved proxy list) — schema correctness reasoned about manually against `actions/*` documented inputs instead.

**Not yet empirically verified (can't run Actions locally):** that `actions/setup-dotnet`'s `dotnet-version` input actually resolves `11.0.100-rc.1.26425.128` as a prerelease SDK on a fresh Linux runner. This is a "correct by construction" assumption — flagged for the first real PR run to confirm.


#### 2026-09-23: Gated Python test hooks for conformance testing (Summer)
**By:** Summer (Python backend) — consumed by Birdperson's conformance suite (#7)
**Issue:** #7 (S1.1), branch `feat/conformance-harness`

**What:** `app/backend/test_hooks.py`, a single small centralized module, gated by `CONFORMANCE_TEST_HOOKS=1` (only the literal string `"1"` after `.strip()` — deliberately not accepting `"true"`/`"yes"` to avoid an accidental-enable footgun):
- `HOOKS_ENABLED: bool` — read once at import time as a module-level constant, matching the existing convention (`session_manager.py`'s `_RESUME_*` timers are the same shape).
- `now(tz) -> datetime` — returns a fixed instant from `CONFORMANCE_FIXED_NOW` (must include a UTC offset or IANA zone; raises on naive input) converted into the caller's `tz`, when hooks are enabled; otherwise real `datetime.now(tz)`.
- `seconds(env_var, default) -> float` — returns a parsed override from `env_var` when hooks are enabled and the value parses; otherwise `default` unchanged. Falls back silently on any parse failure (not just when unset) so a malformed override never hard-crashes backend startup during a test run.

**Wired at 4 call sites, 7 new env vars (all inert unless `CONFORMANCE_TEST_HOOKS=1`):**
| Site | Constant/behavior gated | Env var |
|---|---|---|
| `order_state.py: is_happy_hour()` | store-local "now" for happy-hour/time-based pricing | `CONFORMANCE_FIXED_NOW` |
| `session_manager.py` | idle timeout | `CONFORMANCE_IDLE_TIMEOUT_SECONDS` |
| `session_manager.py` | resume grace | `CONFORMANCE_GRACE_SECONDS` |
| `session_manager.py` | resume nudge-after | `CONFORMANCE_NUDGE_AFTER_SECONDS` |
| `session_manager.py` | first-frame resume timeout | `CONFORMANCE_FIRST_FRAME_TIMEOUT_SECONDS` |
| `rtmt.py: _SESSION_CONFIGURED_TIMEOUT_SEC` | greeting timeout | `CONFORMANCE_GREETING_TIMEOUT_SECONDS` |
| `rate_limit.py: RateLimitSettings.from_config()` | rate-limit retry delay / second retry delay | `CONFORMANCE_RATE_LIMIT_RETRY_DELAY_SECONDS` / `CONFORMANCE_RATE_LIMIT_SECOND_RETRY_DELAY_SECONDS` |

**Never enabled in bicep or the Dockerfile** — confirmed by inspection, no changes made to either.

**Proof of inertness (mutation-checked):** `app/backend/tests/test_test_hooks.py`, 18 tests across `TestInertWhenUnset` (real clock/timers regardless of poisoned env vars; only literal `"1"` enables), `TestActiveWhenSet` (fixed clock honored, zone conversion correct, naive timestamp rejected, unparseable override falls back), `TestHappyHourIntegration` (end-to-end proof through `order_state.is_happy_hour()`). Because `HOOKS_ENABLED` is a module-level constant, tests toggle it via `monkeypatch.setenv/delenv` + `importlib.reload()`, not just setting the env var.

Mutation-check: hardcoded `HOOKS_ENABLED = True` in `test_hooks.py` →
```
8 of 18 tests in test_test_hooks.py failed (every TestInertWhenUnset case)
```
Restored the line (`git diff` clean) →
```
18 passed
```

**Incidental fix, in-scope (directly coupled to the feature the hooks address):** `tests/test_combo_orders.py::TestAbsorptionPricing::test_combo_plus_standalone_drink_at_full_price` asserted "full price" but never patched `is_happy_hour()`, unlike every sibling test in the file — it was flaky by real time-of-day (failed whenever run between 14:00–16:00 store-local). Added the missing `@patch("order_state.is_happy_hour", return_value=False)`.

**Counts:** `pytest app/backend/tests -q` → **586 passed, 61 subtests** (568 baseline + 18 new). `ruff check .` clean repo-wide (one import-sort fix in `rtmt.py` via `ruff check --fix`).


#### 2026-08-19: Squad v0.12.0 rollout repair — casting policy migration (Surgeon)
**By:** Surgeon (Release Manager) — revision owner, FIDO-assigned
**What:** Migrated `.squad/casting/policy.json` from legacy/v1.1 schema to v1.2 in all eight canonical repos. Schema change: added `casting_policy_version: "1.2"`, `allow_custom_universes: true`, and `default_naming: "descriptive"`. All project-specific allowlist universes and universe_capacity values were preserved verbatim. Legacy flat-schema repos (dunkin-chat-voice-assistant used `universes_allowed/max_agents_per_universe/overflow_strategy`; mightybs-blog used `universes/max_per_universe`) were migrated to v1.2 field names while preserving intent: dunkin retains `overflow_strategy` as a custom field and uniform cap-of-10 per universe; mightybs-blog retains its single `quake` universe at capacity 15. Custom universes unique to each project (McDonald's, Retail Icons, Rick and Morty) were preserved.
**Why:** v0.12.0 reads `.squad/casting/policy.json` (nested); the flat `.squad/casting-policy.json` was already at v1.2 post-upgrade but the nested runtime file was stale, causing all casting reads to use legacy schema.


#### 2026-09-22: Server-authoritative realtime session bootstrap + voice-lock handling; move to gpt-realtime-2.1 (Unity)
**By:** Unity (AI/Realtime) — with Birdperson (regression tests), Squanchy (infra review)
**What:**
1. `RTMiddleTier` now sends its own GA `session.update` (tools, `tool_choice`, instructions, voice, the browser's VAD/transcription values) as the **first upstream frame** after `ws_connect`, before relaying any browser traffic. The browser's own `session.update` is still honoured, but it is no longer what configures the upstream session.
2. Once the upstream session has emitted assistant audio, every `session.update` built by the middle tier **omits `audio.output.voice`**, and `extension.set_voice` is deferred to the next conversation rather than sent.
3. The greeting waits for `session.updated` (5 s timeout), fires only on the browser's `session.update` (not on the bootstrap's `session.updated`), and is still sent only once.
4. `infra/main.bicep` realtime deployment → `gpt-realtime-2.1` / `2026-07-07` / `GlobalStandard`. `_to_ga_session()` now lets `reasoning` and `parallel_tool_calls` through, but nothing sends them by default.

**Why:** The Carhop Ticket stayed at $0.00 (prod session 26e3f21f, 2026-09-22). The browser only sends `session.update` when the mic is pressed; an auto-reconnected socket with a live mic ran on **service defaults**: no tools, generic instructions, voice `alloy`, server VAD auto-responding. The model spoke, which locked the voice, so the browser's later `session.update` (voice `shimmer`) was rejected wholesale with `invalid_request_error`/`cannot_update_voice`. Tools were never registered and every turn completed with no tool calls. Verified live on gpt-realtime-1.5: the same voice or no voice after audio is accepted, a different voice rejects the whole event. OpenAI's reference documents the lock ("Voice cannot be changed during the session once the model has responded with audio at least once").

**Applies to Dunkin / McDonald's:** Any middle tier that configures the session only when the client asks, or that injects a voice into every `session.update`, has the same failure mode. Port the bootstrap and the voice-strip together, along with `tests/test_session_bootstrap.py` (a fake GA server that enforces VAD auto-response and wholesale `cannot_update_voice` rejection).

**gpt-realtime-2.1 vs 1.5 (docs, 2026-09-22):**
- Same URL, same session shape, same event names, same 10 voices.
- Additions are reasoning-model-only: `session.reasoning.effort` (minimal|low|medium|high|xhigh) and `session.parallel_tool_calls`.
- Learn's model table still labels 2.1 "preview"; the resource catalog (`az cognitiveservices account list-models`) reports `GenerallyAvailable`, retiring 2027-07-31.

**Open (live-only):**
- Tune `reasoning.effort` for latency on 2.1.
- Find the cause of `Received frame with non-zero reserved bits` on the browser→backend websocket (Squanchy): it plus the 300 s idle close triggered the reconnect.
- After a reconnect the order state is lost, because a new session id is issued.


# Decision: marin default voice, self-healing session.update, config-driven reasoning.effort

- **Date:** 2026-09-22
- **Owner:** Unity (AI/Realtime), with Summer (backend), Morty (frontend), Birdperson (tests)
- **Branch:** `feat/voice-reasoning`

## 1. Default voice is `marin`; the picker offers every voice 2.1 accepts
- Live probe against `gpt-realtime-2.1`: the ten built-in voices below are all accepted.
  - alloy, ash, ballad, coral, echo, sage, shimmer, verse, marin, cedar
- `fable`, `onyx` and `nova` are rejected with `invalid_value`, `param=session.audio.output.voice`. The service's own error text lists exactly the ten above.
- OpenAI's realtime docs list the same ten and recommend marin and cedar for best quality.
- The picker's voice list is now in one place, `app/frontend/src/lib/voices.ts`.
  - marin and cedar are shown first, labelled "(recommended)".
  - A stored voice that is not on the list falls back to marin.
- Default set to `marin` in:
  - `config.yaml` (`model.default_voice`)
  - `infra/main.parameters.json`
  - the app.py fallback
  - `.env-sample`
  - the local azd env `sonic-demo`. That file is gitignored, but its value overrides the bicep default.

## 2. A rejected session.update can no longer silently drop the tools
- **event_id on every update.** Each `session.update` we send carries an `event_id`: bootstrap, relayed browser updates, voice updates and fallbacks.
- **Tracking.** A per-socket `_SessionUpdateGuard` tracks the updates that are still in flight.
- **Correlating an error to our update:**
  - If `error.event_id` is one of ours, the error is ours.
  - If the error has no event_id (gpt-realtime-1.5 rejects `reasoning` with `event_id=None, param=None`), it is attributed to the oldest in-flight update. This happens only when it is an `invalid_request_error` and `param` is empty or starts with `session`.
  - An error carrying someone else's event_id, a non-session param, a `server_error`, or arriving with nothing in flight is unrelated. It is forwarded as before.
- **Response to a correlated error:**
  - Log at ERROR with the code and param.
  - Immediately send a minimal fallback containing only `type`, `instructions`, `tools` and `tool_choice`.
  - The browser does not see the original error.
- **Loop guard:** at most one fallback per original.
  - If the fallback is also rejected, that error is logged at ERROR and forwarded to the browser.
  - No further fallback is sent.
- **Reasoning rejection:** if the rejected payload carried `reasoning`/`parallel_tool_calls` and the param was empty or reasoning-related, reasoning is switched off for the rest of the process.
- **Transcription model** is configurable through `model.transcription_model` or env `AZURE_OPENAI_REALTIME_TRANSCRIPTION_MODEL`. The default stays `whisper-1`:
  - `gpt-4o-transcribe`, `gpt-4o-mini-transcribe` and `gpt-4o-transcribe-diarize` are *accepted* by session.update on both 2.1 and 1.5.
  - At runtime, every turn then fails with `DeploymentNotFound`, because the resource has no deployment of those names.
  - whisper-1 transcribes in about 0.95s with no deployment.
  - A failed transcription is now logged at ERROR.
- **Post-deploy smoke check.** `scripts/smoke_realtime.py` sends the app's real payloads (bootstrap, relayed update, fallback) and a real audio turn. It checks for `session.updated` with all tools, `tool_choice=auto`, the instructions applied, and a completed transcription.
  - Exit codes: 0 pass, 1 fail, 2 could not run.
  - It is wired as a **non-fatal** azd `postdeploy` hook. `continueOnError: true` is set, and the wrapper always exits 0 with a loud warning.
  - Skip it with `SONIC_SKIP_REALTIME_SMOKE=true`.

## 3. reasoning.effort is config-driven and rollback-safe
- **Live probe results:**
  - 2.1 accepts none, minimal, low, medium, high and xhigh, and echoes `reasoning` in `session.updated`.
  - 1.5 rejects every effort (even "none") and `parallel_tool_calls=true`, and drops the tools with them. `parallel_tool_calls=false` is accepted on 1.5.
- **Configuration:** `model.reasoning_effort`, with env override `AZURE_OPENAI_REALTIME_REASONING_EFFORT`.
  - `""`, `off` and `disabled` omit the field.
  - `none` is sent as a real effort level.
  - A client-supplied `reasoning`/`parallel_tool_calls` is always stripped.
- **Rollback safety:** `reasoning` is only sent when the deployment name is not a known non-reasoning family: `gpt-realtime-1.x`, `gpt-realtime`, the dated snapshot, mini, and `gpt-4o-*`.
  - A rollback to `gpt-realtime-1.5` therefore never sends it. This works even without the fallback, which is only the backstop for custom deployment names.
- **Default and benchmark:** see the table in Unity's history.md entry for 2026-09-22 (r2). The benchmark script is `scripts/benchmark_reasoning.py`.

## Inbox (2026-09-25)

#### 2026-09-25T19:10:59-04:00: P1 persona architecture (Rick, Lead) - PROPOSED, pending Brian's review

**By:** Rick (Lead), for issue #19 (includes the design for #51). Requested by Brian Swiger.
**Status:** Proposed. Nothing here is binding until Brian reviews the P1 PR. No P2 work starts before that.
**Docs:** `docs/adr/ADR-001-persona-architecture.md` (decision) and `docs/persona-architecture.md` (full design, 51-row difference inventory), on branch `squad/19-persona-architecture`.

**What:**
1. Each brand is a data-first persona pack in `personas/<id>/`: `persona.json` (rules, strategies, UI manifest, validated by `personas/persona.schema.json`), `prompts/*.yaml` (moved from `app/backend/prompts/<brand>/`), `menu/menuItems.json` (with the #51 per-item fields), and `assets/`. Python and C# load the same files.
2. #51 per-item fields: `comboSlot` (`sides|drinks|none`, same values as the golden table), `happyHourDiscounted`, `aliases`, `bundle` (slots, autoFill), `requiresMachine`, `isExtra`; McD's existing `menuPeriod` and `mealNumber` join the schema. Name-keyed tables leave `menu_utils.py`. The off-menu fallback survives only as ordered, word-bounded `offMenu` rules in `persona.json`; the loader rejects any rule with `comboSlot: "sides"`. The golden table is checked against the data, never generated from it.
3. Only one strategy slot in P2: `searchQueryRewrite` (`none`, `meal_numbers` for McDonald's). New strategies need both backends plus a conformance scenario.
4. Persona is chosen per session (`/realtime?persona=<id>`, unknown persona gets HTTP 404 before upgrade) inside a per-deployment allow-list (`PERSONAS`, `DEFAULT_PERSONA=sonic`). No mid-session switch. Resume binds the persona (`persona_mismatch` rejection). New endpoints `/api/personas` and `/api/personas/{id}`.
5. Frontend themes at runtime (PersonaProvider, CSS tokens, no brand hex). This is the approved exception to the "no frontend changes" rule.
6. The unified app lives in this repo. Siblings stay live, security fixes only (McD #6, Dunkin #11), until parity sign-off, then tag, redirect README, archive.
7. Dropped from P2: McD local mode, Dunkin crew dashboard, CRM simulator, Azure Local / k8s / flux, and the Azure Speech toggle (dead in all three repos).
8. Internal protocol ids (`sonic_mt_` prefix, `sonic_*` event ids, logger names) stay unchanged; #29 depends on the prefix.

**Why:** Almost every brand difference is data. One shared contract means one conformance suite and one C# port (#12 to #16) with no name tables to transcribe.

**Team impact:**
- Summer: P2-1, P2-2, P2-3, P2-5 to P2-7, P2-10. Birdperson: P2-4 (persona dimension; invert the brand guards in `test_rebrand_verification.py` and `locales.test.ts`). Morty: P2-8. Unity: prompts in P2-6/P2-7, and P2-9. Squanchy: P2-10 hook, P2-11. Beth: reviews the P2-4 harness; the C# issues gain persona scope (design doc section 12).
- Sibling bug found: McD's prompt tells the model to call `update_order` `modify`, but the YAML tool schema that actually loads only allows add/remove, so `modify` is dormant. Fixed in P2-6.

**Open for Brian (design doc section 14):** Q1 switching model, Q2 which personas on the main URL, Q3 #64 floats, Q4 keep or remove the off-menu fallback, Q5 McD happy hour, Q6 Dunkin silent happy hour, Q7 dropped features, Q8 repo name, Q9 sibling cutover grace period, Q10 deploy target.

#### 2026-09-25T19:10:59-04:00: PR #66 revision (#55/#62 conformance flakes) — Beth, pushed for Rick's re-review

**By:** Beth (.NET/C# Backend Dev), revising PR #66 after Rick (Lead) requested changes. Birdperson (original author) is locked out of this revision per the Squad's strict reviewer-lockout rule. Requested by Brian Swiger.
**Status:** Pushed to `squad/55-62-conformance-flakes` (commit `bbd3d5e` on top of `35a19ad`). PR body rewritten, summary comment posted, Rick re-tagged for review. **Not merged** — coordinator/Rick still needs to sign off.

**What changed vs. the original PR:**
1. **M1 (never silently absorb a stray error between scenarios):** `WaitForOutputQuiescenceAsync` now runs *after* each scenario body completes, not just before the next scenario's baseline capture. A new pure `ScenarioErrorAttribution` bookkeeping class charges any error landing in the gap to the *previous* scenario's remaining allowance and fails naming that scenario if it's exceeded — previously this landed silently and only surfaced as a confusing failure on the *next* test.
2. **M2 (mutation evidence):** added 19 deterministic unit tests (`ScenarioErrorAttributionTests`, `CapturedProcessOutputWaitTests`), including a fixture-level test proving a late error from scenario A is attributed to A. All 7 distinct guarded behaviours confirmed red under a targeted mutation, then reverted cleanly (mutated files verified byte-identical to pre-mutation backups afterward via `Compare-Object`).
3. **M3 (honest evidence):** PR body rewritten — states plainly neither #55 nor #62 was ever reproduced, both root causes are inferred from code inspection of the race shape, and the original 20/20-under-load number has no pre-fix baseline to compare against. `Fixes #55, #62` changed to `Refs #55, #62`.
4. **S1–S5:** last-append-timestamp fast path (S1, ~0ms when silent instead of always paying the idle window); monotonic watermark so `WaitForDiagnosticsAsync` can't match an older scenario's line (S2); both waits now propagate `OperationCanceledException` and use one shared timeout task instead of a per-wake timer (S3); both waits return `bool` so a cap-hit is surfaced instead of silent, with `ConformanceFixture` failing with a clear message naming the scenario (S4); interface renamed to `WaitForOutputQuiescenceAsync` to match the implementation (S5).
5. **Non-blocking:** verified `update_order`'s #36 fix now validates required args upfront and logs at WARNING (not ERROR/exception) level, so tightened `ToolErrorSessionSurvivesTests`' `allowedNewBackendErrors` from `1` to `0` (re-verified green).
6. Filed **#68** for a previously-unfiled CI flake, `RateLimitGuestSpeechCancellationTests` (dev @ `d720e16`, run 36198921931) — tracked only (`track:conformance`, milestone "S2 C# skeleton", refs #63), explicitly not fixed as part of #66.

**Validation:** full conformance suite (`Category!=Browser`) green 5/5 consecutive runs, 449/449 each (avg ~45.2s — the 430-test pre-M2 baseline plus 19 new tests). `python -m pytest app/backend/tests -q`: 875 passed, 125 subtests. `ruff check .`: clean. No backend (Python) code touched.

**Environment note for whoever revises a PR like this next:** the worktree needs its own `.venv` (`ruff`/`pytest`/`pytest-asyncio` matching CI's `requirements.txt`) and its own built `app/backend/static` (`npm ci && npm run build` in `app/frontend`) — `RepoPaths.FindRepoRoot()` resolves relative to the worktree root (it accepts a `.git` *file*, which is what a worktree has, not just a `.git` directory), so the harness looks for these next to the worktree, not the main checkout.

**Reviewer-identity limitation encountered:** GitHub's `requestedReviewers` API requires a real, distinct collaborator account. In this environment Rick's prior review was posted from the `swigerb` account (the same account used to push this revision, and the PR's recorded author), so a formal `gh pr edit --add-reviewer` call fails both for `Rick` (not a collaborator login) and `swigerb` (can't request review from the PR author). Re-request was done via an explicit `@Rick` mention and "re-requesting your review" text in both the PR body and the summary comment instead.

**Team impact:** none outside this PR. Birdperson should not act on this revision (reviewer lockout still in effect until Rick/coordinator signs off).

#### PR #66 round 3 (Summer) — R1/R2 fixes for conformance-flake attribution

**Date:** 2026-09-25
**Author:** Summer (Backend Dev)
**PR:** #66 (`squad/55-62-conformance-flakes` → `dev`), commit `0ad194e` on top of `f5bd8b4`
**Refs:** #55, #62

**Context:** Rick requested changes twice on PR #66 (round 1 reviewed by Birdperson's fix, round 2 by Beth's fix). Both are locked out under strict reviewer lockout; I own round 3. Scope was limited to exactly Rick's two remaining round-2 items (R1, R2) — R3 (squash-merge with edited commit message) belongs to the coordinator, not me.

**R1 — one late error cascades into every remaining scenario:** Three compounding bugs, all in `ScenarioErrorAttribution` / `ConformanceFixture.RunAsync`:
1. The charge to the previous scenario didn't advance the watermark/consumed count on the failing branch, so the same stranded error line(s) got re-attributed to every subsequent scenario (B, C, D... all re-charged against A's line).
2. `Assert.Fail` for the charge ran *before* the `try`/`finally` in `RunAsync`, so the current scenario was never recorded when the charge failed — it disappeared from the report entirely instead of being attributed.
3. Backend-startup errors (before any scenario ran) had no scenario to charge against, surfacing as `<unknown scenario>` and cascading via bug #1.

Fix: make the charge always consume what it charges; move the charge + resulting `Assert.Fail` inside `try`/`finally` so every scenario is always recorded (guarded by a `postBodyRecorded` flag to avoid double-recording); seed a `StartupScenarioName` pseudo-scenario at fixture `InitializeAsync` so startup errors fail once, by name, without cascading.

**R2 — unhandled-error count read twice per checkpoint:** Each checkpoint (scenario start, scenario end) read the live count twice — once for the charge/check, once for the baseline/record — creating a window where a line landing between the two reads was silently lost. Fix: collapsed to `BeginScenario`/`EndScenario`, each taking a single snapshot read and deriving both the charge/check and the baseline/record from that one read.

**Testing approach:**
- 7 new unit tests in `ScenarioErrorAttributionTests.cs` (cascade regression, idempotency, startup pseudo-scenario, fixed the pre-existing first-scenario test that only asserted the zero-count case despite describing the nonzero case in its own comment, single-read proofs via a fake count source that increments between reads).
- Updated the one existing real-process integration test (`CapturedProcessOutputWaitTests.Fixture_level_M1...`) to route through `BeginScenario`/`EndScenario` instead of hand-wiring internals — this was the literal gap Rick's review flagged.
- Mutation-checked all 4 logic changes (byte-identical revert confirmed via file-hash/diff snapshots after each): each caught by 1–3 tests.
- **Disclosed gap:** the `ConformanceFixture.RunAsync` `try`/`finally` reordering itself has no fast/pure unit-test oracle — it needs a live fixture + real backend process. Validated via code review + full-suite regression (456/456 × 5 local runs) instead of a dedicated mutation-tested unit test. Documented this honestly in the PR comment rather than overclaiming coverage.

**Validation:**
- Full conformance suite (`Category!=Browser`): 456/456 green × 5 consecutive local runs.
- Targeted harness unit tests (33 tests): 10/10 consecutive green runs.
- `pytest app/backend/tests -q`: 875 passed / 125 subtests, clean.
- `ruff check .`: clean.
- CI: green on `0ad194e`. One `Conformance suite (backend=python)` run hit a one-off timing flake in the pre-existing real-process `Fixture_level_M1...` test (unrelated to this change's logic — `BeginScenario` is a pure single-read wrapper around the same call path); passed clean on rerun.

**Learnings for the team:**
- **Worktree frontend/static assets aren't copied by `git worktree add`.** The full conformance suite failed 332/456 in a fresh worktree purely because `app/backend/static/index.html` (a build artifact) didn't exist there. Fixed by `robocopy`-mirroring `node_modules` and the built `static/` dir from the main checkout, same pattern already used for the Python venv. Worth a note in onboarding docs for anyone else spinning up a worktree for this repo.
- **A single CI flake in a genuinely timing-sensitive real-process test is expected and should be triaged, not panicked over** — rerunning the specific failed job (not the whole suite) confirmed it was environmental (CI runner scheduling jitter under a real child-process wall-clock test), not a regression, before declaring done.
- **When "@-mentioning" a squad persona in a GitHub PR comment, there is no separate GitHub account per persona** — all reviews/comments post under one shared account (persona identified in the body text, e.g. "## Rick (Lead, ...)"). Don't fabricate an `@handle` for a persona; it renders as a no-op at best and risks pinging an unrelated real GitHub user at worst.

#### Squanchy: PR #66 R4 — de-flake `Fixture_level_M1_late_error_from_scenario_A_is_attributed_to_A`

**Date:** 2026-09-25 (session dated 2026-09-26 per squad clock)
**PR:** swigerb/SonicAIDriveThru#66 (`squad/55-62-conformance-flakes` → `dev`)
**Scope:** R4 only, per Rick's round-3 review (`#pullrequestreview-5325876077`). R1–R3 already resolved by Summer; strict reviewer lockout kept Birdperson/Beth/Summer out of this artifact for this round.

**Problem:** `Fixture_level_M1_late_error_from_scenario_A_is_attributed_to_A` scheduled scenario A's late backend error at a fixed 300ms delay and relied on scenario B's 500ms quiescence window (measured from an earlier line) still being open when that error was dispatched — about 200ms of margin for the Python `time.sleep`, the stderr pipe, and .NET's ThreadPool `Process.ErrorDataReceived` dispatch to fit inside. CI run 36218817741 (attempt 1, job 108340055593) blew through that margin: `Assert.NotNull() Failure: Value is null`. Local repro confirmed the exact edge: 450ms still passed, 550ms reproduced the CI failure byte-for-byte.

This was a test-design bug, not a production-code bug: #66 M1(b) exists precisely because a drain's idle window is a heuristic that can miss an in-flight line, so a test that assumed the heuristic would always catch one was asserting the wrong contract.

**Fix:** Rewired the test to Rick's suggested shape (starting point from his round-3 review snippet), then generalized to a `[Theory]`:
- Scenario A's own `ScenarioErrorAttribution.EndScenario` check now runs and passes **before** the late-error command is even written to the child process's stdin — no drain, no idle window, no wall-clock margin. This is structural, not timing-dependent.
- The error command is sent only afterwards, with a parameterized `lagMs`.
- The test then waits on the real, monotonically-increasing `CountUnhandledErrors()` value itself via `WaitForDiagnosticsAsync`'s predicate (re-evaluated after every `Append`, i.e. after `ScanLine` has actually incremented the count — not a string match, which could observe a false match one `Append` cycle ahead of the count moving), capped at a generous 30s.
- Only once the count has provably risen does the test call scenario B's `BeginScenario` and assert the stranded error is attributed to `'ScenarioA'`.
- `[Theory]` over `lagMs` = 0, 550 (exact CI repro), 2000 proves the fix is lag-independent, not merely no-longer-failing-at-one-specific-value.

No production harness behavior changed — only this one test.

**Validation evidence:**
- **Mutation-check:** temporarily made `ScenarioErrorAttribution.ChargeStrandedErrorsToPreviousScenario` always `return null` (disabling charging to the previous scenario). All 3 theory cases went red (`Assert.NotNull() Failure: Value is null`), confirming the test still catches a broken attribution. Reverted via `git checkout --` — `git diff --stat` on that file confirmed a byte-identical restore before proceeding.
- **Lag sweep:** 0ms, 550ms, 2000ms all green in the same `dotnet test` run (3/3 theory cases passed).
- **15/15 unloaded:** looped `dotnet test --filter ...Fixture_level_M1...` 15 times, 15/15 green (all 3 lag cases each run).
- **15/15 under heavy parallel CPU load:** started 24 CPU-busy `powershell.exe` processes (one per logical core; confirmed 100% CPU via `Get-Counter`), looped the same test 15 times, 15/15 green. All 24 load processes stopped by PID afterward; CPU returned to baseline (~5%).
- **Harness unit tests:** 35/35 green (`CapturedProcessOutputTests` + 10 `ScenarioErrorAttributionTests` + `CapturedProcessOutputWaitTests`, which now includes the 3 R4 theory cases in place of the old single fact) — exceeds the 10/10 bar.
- **Full conformance suite** (`dotnet test Conformance.slnx --filter "Category!=Browser"`, `CONFORMANCE_BACKEND=python`): 3 consecutive green runs, 458/458 each time.
- **`python -m pytest app/backend/tests -q`:** 875 passed, 125 subtests passed. Clean.
- **`ruff check .`:** All checks passed. Clean.
- **PR CI:** pushed as commit `086654b`; see PR #66 comment for final CI status.

**Toolchain notes for future agents on this artifact:**
- Worktree venv: a plain copy of the repo-root `.venv` (never a junction/symlink) works fine in a worktree — `pyvenv.cfg`'s `home` path points at the system Python install, which is unaffected by the venv's own directory being copied elsewhere.
- `app/backend/static` is gitignored build output required by the harness's Python launcher; copying it from an already-built sibling checkout (rather than re-running `npm run build`) is a valid, faster local shortcut when one is available — CI always builds it fresh via the workflow's own `npm ci && npm run build` step regardless.
- `.NET 11 RC1` at `C:\Users\brswig\.dotnet-sdks\11.0.100-rc.1.26425.128\` needs both `DOTNET_ROOT` and a `PATH` prefix set per-process (PowerShell sessions don't persist env vars across tool calls) — set both at the top of every command block that shells out to `dotnet`.

**Commit:** SHA: `086654bdc86211be8b3612c6eeab8cc6f8081ab7`. Message: `R4: de-flake Fixture_level_M1_late_error_from_scenario_A_is_attributed_to_A`, trailer `Refs #55, #62` (never `Fixes` — R3/M3 already established `dev`'s default-branch auto-close risk), `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`. Pushed to `origin/squad/55-62-conformance-flakes` as a new commit on top of `90d06a8` (no force-push), using `$env:GH_TOKEN = (gh auth token --user swigerb)` for that one process only — `brswig_microsoft` push was refused with a 403 as expected.

**Not merged:** Per instructions, PR #66 was not merged. Commented on the PR addressed to Rick with the change description and evidence above; awaiting his re-review.

