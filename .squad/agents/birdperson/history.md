# Project Context

- **Owner:** Brian Swiger
- **Project:** Dunkin Voice Chat Assistant — AI-powered voice ordering experience using Azure OpenAI GPT-4o Realtime, Azure AI Search, and Azure Container Apps
- **Stack:** Python backend (aiohttp, WebSockets, Azure OpenAI Realtime, Azure AI Search, Azure Speech SDK), React/TypeScript frontend (Vite, Tailwind CSS, shadcn/ui), Bicep IaC, Docker, azd CLI
- **Created:** 2026-03-19

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->
- **Rebrand verification tests added** (`test_rebrand_verification.py`): 12 tests scan every source file for forbidden terms ("dunkin", "crew member", "coffee-chat"). Excludes `.squad/`, `.git/`, `node_modules/`, `__pycache__/`, `voice_rag_README.md` (attribution), and itself. Targeted checks verify README title, index.html `<title>`, and backend system prompt. Pre-rebrand run: 5 pass, 7 fail — exactly right. Tests report file + line number for every violation.
- Existing test files (`test_app.py`, `test_models.py`, `test_order_state.py`, `test_extras_rules.py`, `test_tools_search.py`) contain zero Dunkin/crew-member/coffee-chat references — no updates needed there.
- The backend system prompt in `app.py` was already rebranded to Sonic before these tests ran, so those 3 targeted prompt tests pass immediately.
