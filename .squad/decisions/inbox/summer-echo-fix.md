# Decision: Multi-Layer Echo Self-Talk Fix

**Author:** Summer (Backend Dev)
**Date:** 2026-03-22
**Status:** Implemented

## Problem

AI generates 4+ unsolicited patience responses after speaking ("No problem — take your time...", "Take all the time you need...") because:
1. Echo cooldown (0.5s) was too short — speakers still resonating when mic reopens
2. No buffer flush after cooldown window — echo audio accumulated during cooldown triggered VAD
3. System prompt "give them space — NEVER rush" instruction caused model to actively fill silence

## Changes

### rtmt.py
1. **`_ECHO_COOLDOWN_SEC`: 0.5 → 1.5** — gives adequate time for speaker echo to decay (3.0s for greeting via 2x multiplier)
2. **Delayed second buffer flush** — `loop.call_later(actual_cooldown, _make_delayed_flush)` fires `input_audio_buffer.clear` after cooldown expires, catching any echo audio that accumulated during the window

### app.py
3. **Removed patience instruction** — replaced "If the guest pauses or says 'uh' / 'let me see,' give them space — NEVER rush" with "NEVER speak unless the guest has spoken first — if there is silence, WAIT silently. Do NOT fill silence with 'No rush', 'Take your time', or any unprompted chatter"
4. **max_tokens stays at 4096** — needed for tool call budget; prompt controls verbosity

## Trade-offs
- 1.5s cooldown adds slight delay before user can speak after AI finishes (acceptable for drive-thru)
- Removing patience instruction means AI won't proactively comfort hesitant users (correct for demo — silence is better than self-talk)

## Test Results
118 passed, 1 pre-existing async framework failure (unrelated)
