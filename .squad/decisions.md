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
  string, and `MENU_CATEGORY_MAP` was still keyed by bare `name.lower()` — so a *lookup* of a
  `®`-bearing menu item name (not just a customised one) missed the map's own entry for itself and
  fell through to keyword guessing. This turned out to affect **12 of the 60** menu items (every
  `®`-bearing name — `SuperSONIC® Bacon Double Cheeseburger`, `SONIC® Cheeseburger`, `Ocean Water®`,
  both FRITOS® wraps, etc.), not just the single NBSP-affected OREO Blast Rick's report named —
  proven by a mutation reverting the map-key line back to `name.lower()`, which failed a new
  direct-resolution test for all 12 in one shot.
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



- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
