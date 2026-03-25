# Project Context

- **Owner:** Brian Swiger
- **Project:** Sonic Drive-In Voice Ordering Assistant — AI-powered voice ordering experience using Azure OpenAI GPT-4o Realtime, Azure AI Search, and Azure Container Apps
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


