# Squad Team

> Dunkin Voice Chat Assistant — AI-powered drive-thru ordering experience

## Coordinator

| Name | Role | Notes |
|------|------|-------|
| Squad | Coordinator | Routes work, enforces handoffs and reviewer gates. |

## Members

| Name | Role | Charter | Status |
|------|------|---------|--------|
| Rick | Lead | `.squad/agents/rick/charter.md` | 🏗️ Active |
| Morty | Frontend Dev | `.squad/agents/morty/charter.md` | ⚛️ Active |
| Summer | Backend Dev | `.squad/agents/summer/charter.md` | 🔧 Active |
| Birdperson | Tester | `.squad/agents/birdperson/charter.md` | 🧪 Active |
| Squanchy | DevOps | `.squad/agents/squanchy/charter.md` | ⚙️ Active |
| Scribe | Session Logger | `.squad/agents/scribe/charter.md` | 📋 Active |
| Ralph | Work Monitor | — | 🔄 Monitor |

## Project Context

- **Owner:** Brian Swiger
- **Project:** Dunkin Voice Chat Assistant — an Inspire Brands–themed, voice-driven ordering experience showcasing Azure OpenAI GPT-4o Realtime, Azure AI Search, and Azure Container Apps. Emulates a Dunkin crew member who can search the menu, hold multilingual conversations, and keep orders in sync across devices.
- **Repo:** https://github.com/swigerb/SonicAIDriveThru
- **Stack:**
  - **Frontend:** React, TypeScript, Vite, Tailwind CSS, shadcn/ui
  - **Backend:** Python (aiohttp, WebSockets), Azure OpenAI GPT-4o Realtime API, Azure AI Search, Azure Speech SDK
  - **Infrastructure:** Bicep IaC, Azure Container Apps, Docker, azd CLI
  - **Data:** Jupyter notebooks for menu ingestion, JSON/PDF parsing, semantic hybrid search
- **Key Files:**
  - `app/backend/app.py` — Main backend entry point (aiohttp server)
  - `app/backend/rtmt.py` — Real-time middle tier for Azure OpenAI Realtime API
  - `app/backend/tools.py` — Azure AI Search tool calling integration
  - `app/backend/order_state.py` — Order state management
  - `app/frontend/src/` — React frontend with Vite
  - `infra/main.bicep` — Azure infrastructure definitions
  - `azure.yaml` — azd deployment configuration
- **Created:** 2026-03-19
