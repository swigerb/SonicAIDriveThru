# Keaton — History

## Project Context
- **Project:** Sonic AI Drive-Thru Voice Assistant — voice-driven ordering using Azure OpenAI GPT-4o Realtime + Azure AI Search RAG
- **Stack:** Python 3.11+ (aiohttp) backend, React 18 + TypeScript + Vite frontend
- **User:** Brian Swiger
- **Key files:** app/backend/app.py, app/backend/rtmt.py, app/backend/tools.py, app/frontend/src/App.tsx

## Learnings
- **Architecture:** `rtmt.py` is the core WebSocket proxy (middle-tier pattern) — intercepts tool calls server-side, forwards audio to/from Azure OpenAI Realtime. Do not restructure.
- **Dead code:** `azurespeech.py` and `azure_speech_gpt4o_mini.py` are unused prototypes; `grounding-file*.tsx`, `history-panel.tsx`, `ImageDialog.tsx` have no active imports.
- **Frontend structure:** `App.tsx` is oversized (~585 lines) with inline sub-components (`BrandHero`, SVG art, `SessionTokenBanner`). Needs decomposition.
- **Bug risk:** `useAudioRecorder.tsx` line 13 uses a module-scoped `let buffer` instead of `useRef` — won't reset on remount.
- **Testing:** Only 4 test files total (2 backend: order_state, extras_rules; 2 frontend: order-summary, status-message). No WebSocket or hook tests.
- **Dependency hygiene:** `react-draggable` is a dead dep. `requirements.txt` has BOM encoding issues and includes deps only needed by setup scripts.
- **API contract:** `finalTotal` camelCase in `models.py` is shared with frontend `types.ts` — must change in both if renamed.
- **User preference (Brian):** Wants modern, clean codebase; safety-first approach — "do not break anything."
- **Build commands:** Backend: `ruff check app/backend`, `python -m unittest discover -s tests`. Frontend: `npm run test`, `npm run build`.
- **Key config files:** `pyproject.toml` (ruff only), `app/Dockerfile` (multi-stage Node→Python), `vite.config.ts` (proxy, manual chunks).

## Team Feedback (2026-02-25 Cleanup Sprint)
- **Fenster (Backend):** Successfully modernized all backend files to Python 3.11+. Ruff errors reduced 29→0. All 56 tests pass.
- **McManus (Frontend):** Completed code quality pass on 8 files. Eliminated all `any` types. Fixed critical ref bug in grounding-files.tsx. All 13 tests pass.
- **Hockney (Tester):** Expanded test coverage dramatically: backend 9→56 tests, frontend 4→13 tests. 6 new test files created. All passing.
