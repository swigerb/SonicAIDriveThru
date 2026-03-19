# Project Context

- **Owner:** Brian Swiger
- **Project:** Dunkin Voice Chat Assistant — AI-powered voice ordering experience using Azure OpenAI GPT-4o Realtime, Azure AI Search, and Azure Container Apps
- **Stack:** Python backend (aiohttp, WebSockets, Azure OpenAI Realtime, Azure AI Search, Azure Speech SDK), React/TypeScript frontend (Vite, Tailwind CSS, shadcn/ui), Bicep IaC, Docker, azd CLI
- **Created:** 2026-03-19

## Learnings

- **Sonic Rebrand Scope (2026-03-20)**: Identified ~100+ Dunkin-specific references across frontend UI, backend prompts, menu data, docs, and team context. Critical changes needed in system prompts (`app.py`/`rtmt.py`), frontend components (`App.tsx`/`order-summary.tsx`), menu data files, and logo asset. No changes required in infrastructure, tests (logic remains), or upstream attribution. Scope documented in `.squad/decisions/inbox/rick-sonic-rebrand-scope.md`. Recommended 2–4 dev-days with team parallelization.
