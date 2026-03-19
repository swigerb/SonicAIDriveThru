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
- **Performance Pass (2026-03-19)**: Major frontend perf overhaul. Key findings and fixes:
  - AudioContext is expensive to create (~50-100ms). Recorder and Player now reuse contexts across sessions instead of recreating per start/reset.
  - Audio recorder had O(n²) buffer copying — new Uint8Array created on every append. Replaced with pre-allocated ring buffer using copyWithin() for zero-alloc shifting.
  - Audio player base64 decode used `Uint8Array.from(binary, c => c.charCodeAt(0))` which is slow due to per-char callback. Replaced with direct charCodeAt loop.
  - TranscriptPanel had a `setInterval` running every 1s to update `currentTime`, causing full component re-renders constantly. Removed — timestamp comparison now uses adjacent transcript timestamps only.
  - Key components memoized with React.memo: OrderSummary, OrderItemRow, TranscriptPanel, TranscriptItem, MenuPanel, StatusMessage, BrandHero, SessionTokenBanner.
  - Settings component lazy-loaded (7.4 kB saved from critical path).
  - Vite config: replaced per-package manualChunks (created hundreds of tiny files) with strategic vendor groups (react-vendor, ui-vendor, i18n, motion). Disabled sourcemaps in prod. Added cache-busting hash filenames.
  - WebSocket reconnection now uses exponential backoff with jitter (1s→30s cap) instead of instant retry.
  - getUserMedia now requests specific audio constraints (sampleRate: 24000, mono, echoCancellation, noiseSuppression, autoGainControl) for lower latency capture.
  - Tailwind CSS purge was already correctly configured via `content` array in tailwind.config.js. PostCSS handles minification via autoprefixer.
- **Performance Audit Orchestration (2026-03-19T13-21)**: Team completed full-stack performance sprint with 5 agents. Rick lead: 8 fixes across JSON parsing, token cap, search params, system prompt, JSON caching, VAD timing, and response filtering. Summer: 10 fixes for race conditions, hot-path fast-returns, search caching, compression, gzip, logging, memory. Morty: 9 fixes for AudioContext reuse, zero-alloc buffers, memoization, lazy loading, vendor chunking. Squanchy: 6 infrastructure fixes for Gunicorn async, health probes, auto-scaling, Docker caching. Birdperson: 28 performance tests validating latency, memory, thread safety, production readiness. All decisions documented in decisions.md. Orchestration logs written per-agent.
- **Menu Size Sync (2026-03-19)**: Updated `menuItems.json` to include all size variants from production data (`sonic-menu-items.json`). Drinks (Cherry Limeade, Blue Raspberry Slush, Ocean Water) now have 5 sizes: mini, small, medium, large, rt 44. Shakes get mini added (4 sizes). SONIC Blast corrected to mini/small/medium (production only has 3). Prices updated to match production data. Script at `scripts/update_menu_sizes.py` can be re-run if production data changes. Production data uses size prefixes like "Mini ", "Sm ", "Med ", "Lg ", "RT 44® " before product names. Shakes and Blasts don't have RT 44 sizes in production data — only drinks do.
