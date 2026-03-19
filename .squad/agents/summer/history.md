# Project Context

- **Owner:** Brian Swiger
- **Project:** Sonic Voice Chat Assistant — AI-powered voice ordering experience using Azure OpenAI GPT-4o Realtime, Azure AI Search, and Azure Container Apps
- **Stack:** Python backend (aiohttp, WebSockets, Azure OpenAI Realtime, Azure AI Search, Azure Speech SDK), React/TypeScript frontend (Vite, Tailwind CSS, shadcn/ui), Bicep IaC, Docker, azd CLI
- **Created:** 2026-03-19

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->
- **Sonic Rebrand (backend)**: Replaced all Dunkin references across backend Python files, system prompts, tools, test suites, docs, and menu data. Key insight: the `_infer_category()` function in tools.py is tightly coupled to the frontend menuItems.json — the `MENU_CATEGORY_MAP` loads at module init from that file, so ALLOWED_EXTRA_CATEGORIES and BLOCKED_EXTRA_CATEGORIES must include both the frontend JSON category names AND the fallback keyword-inferred names. The structured_menu_items file was malformed JSON with Dunkin data; replaced entirely with Sonic menu items (slushes, shakes, burgers, hot dogs, tots, breakfast, drinks, extras).
- The `test_app.py` CreateAppConfigTests tests error due to a missing `static/` directory (requires frontend build). Pre-existing issue, not rebrand-related.
- Upstream attribution URLs (john-carroll-sw/coffee-chat-voice-assistant) are preserved and excluded from rebrand verification tests.
- **Team Orchestration (2026-03-19T04-06)**: Rick provided scope analysis, Morty completed frontend rebrand, Summer completed backend rebrand (69 tests pass), Birdperson created verification tests (12 tests pass).
