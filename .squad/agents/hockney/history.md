# Hockney — History

## Project Context
- **Project:** Sonic AI Drive-Thru Voice Assistant — testing for Python backend + React frontend
- **Stack:** Python unittest (backend), Vitest + React Testing Library (frontend)
- **User:** Brian Swiger
- **Existing tests:** app/backend/tests/test_order_state.py, app/backend/tests/test_extras_rules.py
- **Frontend coverage scoped to:** order-summary.tsx, status-message.tsx (in vite.config.ts)

## Learnings
- Backend tests require `pip install -r requirements.txt` for azure SDK deps (azure.identity, azure.search.documents)
- `_get_bool_env` in app.py is importable independently but triggers full module import chain; keep azure deps installed
- `_is_extra_item` and `_infer_category` in tools.py are pure functions, easy to unit-test without mocks
- The `search()` tool can be tested with AsyncMock clients; `HttpResponseError` fallback path needs two-call mock
- OrderState singleton must have `.sessions = {}` in setUp to isolate tests
- Frontend vitest coverage is scoped to order-summary.tsx and status-message.tsx in vite.config.ts
- `vi` is globally available in vitest (globals: true); no explicit import needed in test files
- `userEvent` from @testing-library/user-event works for click simulation on button components

## Test Files Created/Expanded (Session 2025-07)
### Backend (app/backend/tests/)
- **test_models.py** — 12 tests: OrderItem + OrderSummary Pydantic model creation, serialization, JSON round-trips, edge cases (zero qty/price, large qty, float precision)
- **test_app.py** — 8 tests: `_get_bool_env` truthy/falsy values, whitespace stripping, default parameter behavior; `create_app` default voice is "coral", system prompt contains "Please pull around to the next window", system prompt contains `get_order` tool instruction
- **test_tools_search.py** — 8 tests: `_is_extra_item`, `_infer_category` category inference, search result formatting, empty results fallback, HTTP error handling, field-mismatch retry
- **test_order_state.py** (expanded) — 11 new tests: delete session, delete nonexistent session, concurrent sessions independence, round-trip token format, multiple advances, remove/decrement/noop, duplicate add, display formatting for all size variants
- **test_extras_rules.py** (expanded) — 7 new tests: cold brew extras, mixed donut+latte order, multiple donuts blocked, multiple beverages allowed, blocked base message content, non-extra always allowed, remove bypasses check

### Frontend (app/frontend/src/components/ui/__tests__/)
- **calculate-order-summary.test.tsx** — 4 tests: empty list, single item, multi-item with quantities, zero-price
- **loading-spinner.test.tsx** — 3 tests: default message, custom message, spinner element present
- **grounding-file.test.tsx** — 2 tests: renders file name, onClick fires

### Totals
- Backend: 59 tests (was 9) — all passing
- Frontend: 13 tests (was 4) — all passing

## Team Feedback (2026-02-25 Cleanup Sprint)
- **Keaton (Lead):** Comprehensive audit identified testing gaps (WebSocket, hooks). Hockney's expansion addresses core gaps well.
- **Fenster (Backend):** All 56 tests pass after Python modernization. Clean, maintainable baseline.
- **McManus (Frontend):** All 13 tests pass after code quality cleanup. Ready for integration with new test files.

## Voice & Prompt Update (Brian's request)
- Added 3 tests to test_app.py covering: default voice "coral", closing phrase "Please pull around to the next window", and get_order tool instruction in system prompt
- `create_app` can be tested by mocking `RTMiddleTier` class and `attach_tools_rtmt`, with `RUNNING_IN_PRODUCTION=1` to skip .env loading
- All 59 backend tests pass
