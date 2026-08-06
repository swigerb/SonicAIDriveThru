# Fenster — History

## Project Context
- **Project:** Sonic AI Drive-Thru Voice Assistant — Python aiohttp backend with Azure OpenAI Realtime + Azure AI Search
- **Stack:** aiohttp, Pydantic v2, Azure SDKs, python-dotenv, gunicorn
- **User:** Brian Swiger
- **Key files:** app/backend/app.py, app/backend/rtmt.py, app/backend/tools.py, app/backend/order_state.py, app/backend/models.py
- **Singleton pattern:** order_state_singleton in order_state.py — always import, never instantiate new

## Learnings
- **Ruff config:** pyproject.toml targets py311 with rules E, F, I, UP; ignores E501 and E701
- **Import sorting:** ruff I001 is sensitive to local vs third-party grouping; `ruff check --fix` is reliable for sorting
- **Modern annotations:** Replaced `Optional[X]` → `X | None`, `List[X]` → `list[X]`, `Callable` → `collections.abc.Callable`, `super(Cls, self)` → `super()`
- **azurespeech.py had duplicate imports:** `SpeechConfig` and `AudioConfig` were imported twice from different submodules
- **print() in rtmt.py:** Replaced bare `print()` calls with `logger` for consistency
- **get_order tool:** Normalized signature to `(args, session_id)` to match the calling convention in rtmt.py; previous lambda discarded args
- **Redundant block comments:** tools.py had large docblock-style comments above each tool schema that duplicated the schema's own description field — removed
- **__all__ exports:** Added to models.py, order_state.py, rtmt.py, tools.py for cleaner public API
- **Test files had pre-existing issues:** unused imports in test_tools_search.py, unused variable in test_order_state.py — cleaned up
- **Files modified:** models.py, order_state.py, rtmt.py, tools.py, app.py, azurespeech.py, azure_speech_gpt4o_mini.py, tests/test_order_state.py, tests/test_tools_search.py
- **Result:** Ruff errors reduced from 29 → 0; all 56 tests pass

## Voice & Prompt Update (2025-07-24)
- **Default voice:** Changed from "alloy" to "coral" in app.py line 58. Env var override preserved.
- **System prompt:** Updated closing instruction to explicitly require get_order tool for full order recap (items, sizes, quantities, subtotal, tax, total) and new closing phrase: "Thank you! Please pull around to the next window."
- **Validation:** Ruff clean, all 56 tests pass.

## Team Feedback (2026-02-25 Cleanup Sprint)
- **Keaton (Lead):** Completed full codebase audit with 7 P0, 7 P1, 7 P2 findings. Provided cleanup framework.
- **McManus (Frontend):** Eliminated all `any` types in useAzureSpeech.tsx. Fixed critical ref bug in grounding-files.tsx.
- **Hockney (Tester):** Expanded backend test suite 9→56 tests. All passing. Comprehensive model and utility coverage.
