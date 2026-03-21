# Decision: Dual-Trigger Greeting for API Resilience

**Author:** Summer (Backend Dev)
**Date:** 2026-03-22
**Status:** Implemented

## Context

Brian started a demo and got complete silence — no greeting, no audio. The session was active (frontend showed "Conversation in progress") but the AI never spoke.

Root cause: A recent change moved the greeting trigger from `from_client_to_server` (fired after forwarding `session.update`) to `from_server_to_client` (fired after receiving `session.updated` from Azure OpenAI). The intent was correct — ensure tools are configured before greeting — but Azure's Realtime API doesn't reliably send `session.updated`, so the greeting never fired.

## Decision

Implement dual-trigger greeting in `rtmt.py`:

1. **Primary (server→client):** Fire greeting when `session.updated` is received — guarantees tools are configured.
2. **Fallback (client→server):** Fire greeting after forwarding a `session.update` message — reliable because it doesn't depend on API response events.

The existing `greeting_sent` flag (checked in `send_greeting_once()`) prevents double-greeting regardless of which trigger fires first.

## Implementation

- Added `_MARKER_SESSION_UPDATE = '"session.update"'` constant alongside existing `_MARKER_SESSION_UPDATED`.
- In `from_client_to_server`, after forwarding the client message, check if it was a `session.update` (but NOT `session.updated`) and fire `send_greeting_once()`.
- Defensive substring check: `_MARKER_SESSION_UPDATE in msg.data and _MARKER_SESSION_UPDATED not in msg.data` — even though `session.updated` can't appear in client→server messages, this is belt-and-suspenders.

## Trade-offs

- The fallback trigger may fire before tools are fully acknowledged by OpenAI. In practice this is fine because the `session.update` has already been forwarded — OpenAI processes messages in order, so tools will be configured by the time it processes the greeting's `response.create`.
- Slight increase in code complexity (two trigger sites instead of one), mitigated by clear comments and the single `send_greeting_once()` function.

## Impact

Eliminates demo-blocking silence on startup. All 118 tests pass. Frontend build succeeds.
