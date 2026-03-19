# Project Context

- **Owner:** Brian Swiger
- **Project:** Sonic Drive-In Voice Ordering Assistant — AI-powered voice ordering experience using Azure OpenAI GPT-4o Realtime, Azure AI Search, and Azure Container Apps
- **Stack:** React/TypeScript frontend (Vite, Tailwind CSS, shadcn/ui), Python backend (aiohttp, WebSockets), Bicep IaC, Docker, azd CLI
- **Created:** 2026-03-19

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->
- The frontend uses CSS custom properties (HSL values) in index.css for theming, consumed via Tailwind's `hsl(var(...))` pattern in tailwind.config.js. To rebrand, update the CSS variables — Tailwind config stays the same.
- Sonic brand colors map: Primary action → Cherry Red #E40046, Header/nav → Dark Blue #285780, Accent → Yellow #FEDD00, Secondary → Light Blue #74D2E7, Success → Green #328500.
- Brand voice changed from "crew member" to "carhop" (Sonic's term). Menu shifted from coffee/donuts to slushes/burgers/shakes/tots.
- The old dunkin-logo.svg is still in src/assets/ (unused, no longer imported). Safe to delete if desired.
- Font changed from Fredoka to Nunito Sans for a cleaner, more energetic Sonic-aligned feel.
- Test data (dummyOrder.json, dummyTranscripts.json) and test assertions must be kept in sync with branding — they reference specific menu items and branding text.
- **Team Orchestration (2026-03-19T04-06)**: Rick provided scope analysis, Morty completed frontend rebrand (13 tests pass), Summer completed backend rebrand (69 tests pass), Birdperson created verification tests (12 tests pass).
