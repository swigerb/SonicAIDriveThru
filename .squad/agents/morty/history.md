# Project Context

- **Owner:** Brian Swiger
- **Project:** Sonic AI Drive-Thru Voice Assistant — AI-powered voice ordering experience using Azure OpenAI GPT-4o Realtime, Azure AI Search, and Azure Container Apps
- **Stack:** React/TypeScript frontend (Vite, Tailwind CSS, shadcn/ui), Python backend (aiohttp, WebSockets), Bicep IaC, Docker, azd CLI
- **Created:** 2026-03-19

## Learnings

### 2026-03-19: Frontend Rebrand & Performance Hardening (Consolidated)

**Sonic Rebrand:**
- CSS custom properties (HSL values) in index.css for theming — Tailwind consumes via `hsl(var(...))` pattern. Update CSS variables to rebrand.
- Brand colors: Cherry Red #E40046, Dark Blue #285780, Yellow #FEDD00, Light Blue #74D2E7, Green #328500.
- Font: Fredoka → Nunito Sans for cleaner, energetic Sonic alignment.
- Brand voice: "crew member" → "carhop". Menu: coffee/donuts → slushes/burgers/shakes/tots.
- Test data (`dummyOrder.json`, `dummyTranscripts.json`) and test assertions must sync with branding.

**Frontend Performance Overhaul (2026-03-19T13-21):**
- AudioContext reuse: Recorder & Player now reuse contexts instead of recreating per start/reset (~50-100ms saved per session).
- Audio recorder: O(n²) buffer copying replaced with pre-allocated ring buffer using copyWithin() (zero-alloc shifting).
- Audio player: charCodeAt loop replaced slow `Uint8Array.from(binary, c => c.charCodeAt(0))` callback.
- TranscriptPanel: Removed `setInterval` constantly re-rendering — timestamp now uses adjacent transcript timestamps only.
- React.memo applied to: OrderSummary, OrderItemRow, TranscriptPanel, TranscriptItem, MenuPanel, StatusMessage, BrandHero, SessionTokenBanner.
- Settings component lazy-loaded (7.4 kB saved from critical path).
- Vite chunking: Replaced per-package manualChunks with strategic vendor groups (react-vendor, ui-vendor, i18n, motion). Disabled sourcemaps in prod. Added cache-busting.
- WebSocket reconnection: Exponential backoff with jitter (1s→30s cap) instead of instant retry.
- getUserMedia: Specific audio constraints (sampleRate: 24000, mono, echoCancellation, noiseSuppression, autoGainControl) for lower latency.

### 2026-03-19 through 2026-03-22: Audio Feedback Loop & Echo Suppression (Consolidated)

**Initial Feedback Loop (2026-03-19):**
- VAD threshold: 0.6 → 0.8 (aggressive, to reject echoed speech)
- Silence duration: 400ms → 500ms for better turn detection
- Disabled autoGainControl (was amplifying echoed speaker output)
- Removed unnecessary worklet routing to speakers
- Added mic muting during AI playback via gain node (set to 0/1)

**Early Mute Timing Fix (2026-03-19):**
- Moved mic muting from `response.audio.delta` to `response.created` (earliest hook)
- Frontend sends `input_audio_buffer.clear` on `response.created` to flush pre-buffered echo
- Used `sendJsonMessageRef` pattern to break circular dependency
- Barge-in now unmutes mic and resets state for user interrupts

**Coordinated Server-Side Echo Suppression (2026-03-21):**
- Summer implemented server-side audio gating in `rtmt.py` (drops `input_audio_buffer.append` during `ai_speaking`, 300ms cooldown, buffer flush)
- Combined with frontend early muting = zero phantom transcriptions
- Barge-in ~300ms latency acceptable for drive-thru UX
- Result: all 100 backend + 13 frontend tests pass

**Demo Readiness Tuning (2026-03-21):**
- VAD threshold: 0.8 → 0.7 (echo suppression now robust, threshold can be more forgiving for natural speech)
- Prefix padding: 200ms → 300ms (avoids plosive clipping like "burger")
- Rationale: With multi-layered echo suppression working, VAD can focus on natural speech detection rather than echo rejection

### 2026-03-22: UI Enhancements for Demo

**Verbose Logging & Logging Toggle:**
- Added "Verbose Logging" toggle to Settings panel (localStorage-persisted, default OFF)
- Sends `{"type": "extension.set_verbose_logging", "enabled": true/false}` via WebSocket
- Added "Log to File" sub-toggle under verbose logging (only visible when verbose is ON)
- Sends `{"type": "extension.set_log_to_file", "enabled": true/false}` via WebSocket
- State survives page refresh via localStorage

**Menu Categories Collapse/Expand:**
- Made category headers clickable buttons with `aria-expanded` for accessibility
- ChevronDown icon rotates 180° when open (framer-motion)
- Category items animate in/out with AnimatePresence (height auto, opacity, 0.25s easeInOut)
- First category expanded by default, rest collapsed
- Spacing tightened from `space-y-8` to `space-y-4`

**Session Token Panel Collapsible:**
- Replaced flat `SessionTokenBanner` with collapsible panel (defaults collapsed)
- Single-line header: chevron + session token (full, no truncation) + round number
- Expand reveals scrollable history list (max-height 10rem) with all snapshots newest-first
- Latest entry highlighted with subtle red tint
- Settings "Show Session Tokens" toggle still controls overall visibility
- Chevron uses rotation animation matching menu panel style
- Supports multi-line token wrapping with `break-all`

### 2026-03-19: Menu Size Production Data Sync

Created `scripts/update_menu_sizes.py` to sync `menuItems.json` with production `sonic-menu-items.json`. Drinks (Cherry Limeade, Blue Raspberry, Ocean Water) now have 5 sizes (mini, small, medium, large, rt 44). Shakes get mini added (4 sizes). SONIC Blast corrected to 3 sizes. Prices sourced from production data. Production data uses prefixes ("Mini ", "Sm ", "Lg ", "RT 44®") — script strips and normalizes.

### 2026-03-19: Azure Speech Hook Tool Response Fix

Fixed `useAzureSpeech.tsx`: (1) `onReceivedToolResponse` parameter was declared but never destructured — order updates silently dropped. (2) Added `tool_results` processing from `/azurespeech/speech-to-text` response, constructing `ExtensionMiddleTierToolResponse` objects matching WebSocket pattern. (3) Added `session_id` flow using `useRef<string>(crypto.randomUUID())` — regenerated on `startSession()`, sent in every request for backend order state tracking. Backward compatible: missing `tool_results` handled gracefully.

### 2026-08-06: Frontend Dependency Updates

**Major upgrades applied (all verified green — build + 13/13 tests pass):**
- Vite 5 → 6 (6.4.3), @vitejs/plugin-react 4 → 5 (5.2.0)
- Vitest 1 → 2 (2.1.9), @vitest/coverage-v8 1 → 2 (2.1.9)
- lucide-react 0.445 → 1.28 — `Github` icon removed upstream (trademark); replaced with `FaGithub` from `react-icons/fa` in `App.tsx`
- i18next 23 → 26 (26.3.6), react-i18next 15 → 17 (17.0.11), i18next-http-backend 2 → 4 (4.0.1)
- tailwind-merge 2 → 3 (3.6.0)
- @testing-library/jest-dom 6 → 7 (7.0.0)
- @types/node 22 → 26 (26.1.2)
- jsdom 24 → 29 (29.1.1)
- prettier-plugin-tailwindcss 0.6 → 0.8 (0.8.1)

**In-range semver updates (minor/patch):**
- All @radix-ui/* packages, axios, class-variance-authority, darkreader, framer-motion (11.18.2), i18next-browser-languagedetector, react-draggable, react-icons, react-use-websocket, autoprefixer, postcss, prettier, typescript (5.9.3), @types/react, @types/react-dom, @testing-library/user-event, tailwindcss (3.4.19)

**Source changes:** `App.tsx` — replaced `Github` import from `lucide-react` with `FaGithub` from `react-icons/fa` (lucide-react 1.x removed branded icons).

**Deferred upgrades with rationale:**
- **React 18 → 19**: `react-draggable` uses `findDOMNode` (removed in React 19). `@testing-library/react` 14 doesn't support React 19 (needs v16). Would require auditing all Radix UI, framer-motion, and react-use-websocket peer compatibility. High risk to demo stability. **Migration steps:** (1) Upgrade react-draggable or replace with a ref-based alternative, (2) upgrade @testing-library/react to 16, (3) update @types/react + @types/react-dom to 19, (4) audit all peer deps, (5) test all UI interactions.
- **Tailwind CSS 3 → 4**: CSS-first config model, requires rewriting tailwind.config.js, postcss.config.js, and CSS entrypoint. `tailwindcss-animate` and `prettier-plugin-tailwindcss` compatibility uncertain. High visual regression risk for demo app. **Migration steps:** (1) Replace `tailwind.config.js` with `@theme` directives in CSS, (2) replace `postcss-config.js` plugin with `@tailwindcss/postcss`, (3) audit all `hsl(var(...))` color patterns, (4) verify tailwindcss-animate compatibility, (5) full visual regression test.
- **TypeScript 5 → 7**: Major version, potential breaking type-checking changes. Current 5.9.3 is latest 5.x. **Migration steps:** review TS 6/7 release notes for breaking changes, run `tsc --strict` and fix any new errors.
- **Vite 6 → 8**: Would require @vitejs/plugin-react 6 (which requires Vite 8). Multiple major jumps. Current Vite 6 is stable. **Migration steps:** upgrade vite to 8 + @vitejs/plugin-react to 6 together, review config for breaking changes.
- **Vitest 2 → 4**: Would require @vitest/coverage-v8 4. Current v2 is stable and working. **Migration steps:** upgrade both together, review config for API changes.
- **framer-motion 11 → 13**: v13 is alpha only. Package being renamed to `motion`. **Migration steps:** wait for stable 12.x/13.x release, rename import from `framer-motion` to `motion`, update vite.config.ts manualChunks.

### 2026-08-06: React 19 + Tailwind CSS 4 Migration

**React 18.3.1 → 19.2.8 (landed):**
- Upgraded `react`, `react-dom` to ^19.2.8
- Upgraded `@types/react` to ^19.2.17, `@types/react-dom` to ^19.2.3
- Upgraded `@testing-library/react` 14 → 16.3.2 (v14 did not support React 19)
- Removed `react-draggable` — was in package.json but **never imported or used** in any source file (stale dependency). No draggable UI element exists; the "drag" content in the codebase is menu items like "dragon fruit".
- Fixed `useRef<Recorder>()` → `useRef<Recorder | null>(null)` in `useAudioRecorder.tsx` — React 19 types require an explicit initial value argument.
- `forwardRef` usage in shadcn/ui components (button, card, dialog, label, sheet, switch) left as-is — still valid in React 19, just no longer required for new components.
- Peer dep verification: framer-motion 11.18.2, all @radix-ui/*, react-i18next 17, react-use-websocket 4.8.1, react-icons 5 — all support React 19.

**Tailwind CSS 3.4 → 4.3.3 (landed):**
- Ran official `npx @tailwindcss/upgrade --force` codemod
- `tailwind.config.js` removed — theme migrated to `@theme` block in `src/index.css`
- `postcss.config.js` updated: `tailwindcss` + `autoprefixer` → `@tailwindcss/postcss`
- `autoprefixer` removed (handled internally by `@tailwindcss/postcss`)
- `tailwindcss-animate` kept at 1.0.7 — works via `@plugin 'tailwindcss-animate'` directive in v4
- CSS entrypoint: `@tailwind base/components/utilities` → `@import 'tailwindcss'`
- Dark mode: `@custom-variant dark (&:is(.dark *))` replaces `darkMode: ["class"]` config
- Utility class renames (codemod-applied): `bg-gradient-to-*` → `bg-linear-to-*`, `shadow-sm` → `shadow-xs`, `shadow` → `shadow-sm`, `backdrop-blur-sm` → `backdrop-blur-xs`, `drop-shadow-sm` → `drop-shadow-xs`, `focus-visible:outline-none` → `focus-visible:outline-hidden`, `flex-grow` → `grow`
- Brand colour variables (--brand-red, --brand-blue, --brand-light, --brand-dark) preserved in `@layer base` with full light/dark variants
- CSS size: 34.58 kB → 50.15 kB (gzip: 7.19 → 9.20 kB) — increase from expanded v4 preflight and compatibility layer; brand colours and animations verified present in output

**Result:** `npm run build` succeeds (tsc + vite), `npm test` = 13/13 passing. No `@ts-ignore`, no `any` casts, no skipped tests.
