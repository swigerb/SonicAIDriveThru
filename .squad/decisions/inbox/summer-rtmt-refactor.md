# Decision: rtmt.py Code Organization (Phase 3)

**Author:** Summer (Backend Dev)  
**Date:** 2026-03-25  
**Status:** Implemented

## What Changed

Broke `app/backend/rtmt.py` (766 lines) into 3 focused modules:

1. **`session_manager.py`** (154 lines) — Session lifecycle, greeting state, context window monitoring
2. **`audio_pipeline.py`** (199 lines) — Echo suppression state machine, verbose logging infrastructure, audio constants
3. **`rtmt.py`** (586 lines) — Thin orchestrator: RTMiddleTier class, WebSocket routing, message processing

## Key Design Decisions

- **EchoSuppressor class**: Encapsulates `ai_speaking`, `cooldown_end`, `greeting_in_progress` into clean methods (`should_suppress_audio`, `on_audio_delta`, `on_audio_done`, `on_speech_started`, `on_barge_in`). Eliminates `nonlocal` variable sharing between nested closures for echo state.
- **SessionManager class**: Owns `_session_map`, `_sent_greeting`, and `_context_monitors`. Single cleanup method (`cleanup_session`) prevents leaked state.
- **Public API preserved**: `from rtmt import RTMiddleTier, ToolResult, ToolResultDirection, Tool, RTToolCall` unchanged. No downstream import changes needed.
- **No circular imports**: `session_manager.py` → `order_state`, `config_loader`; `audio_pipeline.py` → `config_loader`; `rtmt.py` → both new modules.

## Context Window Monitoring (New Feature)

- `ContextMonitor` class tracks estimated token usage per session (~4 chars/token heuristic)
- Logs WARNING at 80% and CRITICAL at 95% of max tokens (128K default)
- Config in `config.yaml` under `context:` key
- Monitors: system message, tool schemas, tool args/results, AI responses, user transcriptions
- No truncation implemented — monitoring only for now

## Impact

- All 128 tests pass
- No behavioral changes — purely structural refactoring + monitoring addition
- Easier to navigate, test, and modify individual concerns independently
