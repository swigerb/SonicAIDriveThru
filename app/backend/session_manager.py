"""Session lifecycle management for Sonic AI Drive-Thru realtime middleware.

Handles per-connection session creation, tracking, cleanup, greeting state,
session identifiers, context window monitoring, concurrency limits, and idle
timeout enforcement.
"""

import asyncio
import json
import logging
import time
from typing import Any

from aiohttp import web

from config_loader import get_config
from order_state import SessionIdentifiers, order_state_singleton

logger = logging.getLogger("sonic-drive-in")

_config = get_config()
_context_cfg = _config.get("context", {})
_security_cfg = _config.get("security", {})

# ── Context Window Monitoring ──
_CTX_MAX_TOKENS = _context_cfg.get("max_tokens", 128000)
_CTX_WARNING_PCT = _context_cfg.get("warning_threshold_pct", 80)
_CTX_CRITICAL_PCT = _context_cfg.get("critical_threshold_pct", 95)

# ── Session Limits ──
_MAX_CONCURRENT_SESSIONS = _security_cfg.get("max_concurrent_sessions", 10)
_IDLE_TIMEOUT_SECONDS = _security_cfg.get("idle_timeout_seconds", 300)

# Rough token estimation: ~4 characters per token for English text.
# This is intentionally conservative (over-estimates) for safety monitoring.
_CHARS_PER_TOKEN = 4

# Default greeting — overridden by PromptLoader at runtime.
_DEFAULT_GREETING_MSG = json.dumps({
    "type": "conversation.item.create",
    "item": {
        "type": "message",
        "role": "user",
        "content": [
            {"type": "input_text", "text": "Say EXACTLY this greeting and NOTHING else: Welcome to Sonic Drive-In! What can I get started for you today?"}
        ]
    }
})


class ContextMonitor:
    """Estimates token usage in the conversation context window and logs warnings.

    Uses a simple character-based heuristic (~4 chars/token). Not exact, but
    sufficient for warning when we're approaching the context limit.
    """
    __slots__ = ("session_id", "_char_count", "_warned_warning", "_warned_critical")

    def __init__(self, session_id: str):
        self.session_id = session_id
        self._char_count = 0
        self._warned_warning = False
        self._warned_critical = False

    def add_content(self, text: str) -> None:
        """Track content that contributes to the context window."""
        if not text:
            return
        self._char_count += len(text)
        self._check_thresholds()

    @property
    def estimated_tokens(self) -> int:
        return self._char_count // _CHARS_PER_TOKEN

    @property
    def usage_pct(self) -> float:
        if _CTX_MAX_TOKENS <= 0:
            return 0.0
        return (self.estimated_tokens / _CTX_MAX_TOKENS) * 100

    def _check_thresholds(self) -> None:
        pct = self.usage_pct
        tokens = self.estimated_tokens

        if not self._warned_critical and pct >= _CTX_CRITICAL_PCT:
            logger.warning(
                "CRITICAL: Context window at %d%% (%s/%s tokens) for session %s",
                int(pct), f"{tokens:,}", f"{_CTX_MAX_TOKENS:,}", self.session_id,
            )
            self._warned_critical = True
            self._warned_warning = True
        elif not self._warned_warning and pct >= _CTX_WARNING_PCT:
            logger.warning(
                "WARNING: Context window at %d%% (%s/%s tokens) for session %s",
                int(pct), f"{tokens:,}", f"{_CTX_MAX_TOKENS:,}", self.session_id,
            )
            self._warned_warning = True


class SessionManager:
    """Manages WebSocket session lifecycle: creation, tracking, cleanup, greeting state,
    concurrency limits, and idle timeout."""

    def __init__(self, prompt_loader=None):
        self._session_map: dict[web.WebSocketResponse, str] = {}
        self._sent_greeting: set[str] = set()
        self._context_monitors: dict[str, ContextMonitor] = {}
        self._last_activity: dict[str, float] = {}
        self._idle_check_task: asyncio.Task | None = None
        if prompt_loader is not None:
            self._greeting_msg = prompt_loader.get_greeting_json_str()
        else:
            self._greeting_msg = _DEFAULT_GREETING_MSG

    @property
    def greeting_msg(self) -> str:
        return self._greeting_msg

    @property
    def active_session_count(self) -> int:
        return len(self._session_map)

    def can_accept_session(self) -> bool:
        """Return True if we haven't hit the concurrent session limit."""
        return self.active_session_count < _MAX_CONCURRENT_SESSIONS

    def touch_activity(self, session_id: str) -> None:
        """Update the last-activity timestamp for idle detection."""
        self._last_activity[session_id] = time.monotonic()

    def create_session(self, ws: web.WebSocketResponse) -> str:
        """Create a new order session and map it to the WebSocket connection."""
        session_id = order_state_singleton.create_session()
        self._session_map[ws] = session_id
        self._context_monitors[session_id] = ContextMonitor(session_id)
        self._last_activity[session_id] = time.monotonic()
        return session_id

    def get_session_id(self, ws: web.WebSocketResponse) -> str | None:
        return self._session_map.get(ws)

    def has_sent_greeting(self, session_id: str) -> bool:
        return session_id in self._sent_greeting

    def mark_greeting_sent(self, session_id: str) -> None:
        self._sent_greeting.add(session_id)

    def get_context_monitor(self, session_id: str | None) -> ContextMonitor | None:
        if session_id is None:
            return None
        return self._context_monitors.get(session_id)

    def cleanup_session(self, ws: web.WebSocketResponse, session_id: str | None) -> None:
        """Remove all state associated with a WebSocket connection."""
        if session_id is not None:
            order_state_singleton.delete_session(session_id)
            self._sent_greeting.discard(session_id)
            self._context_monitors.pop(session_id, None)
            self._last_activity.pop(session_id, None)
        self._session_map.pop(ws, None)

    async def close_idle_sessions(self) -> None:
        """Close WebSocket connections that have been idle beyond the timeout."""
        now = time.monotonic()
        idle_pairs: list[tuple[web.WebSocketResponse, str]] = []
        for ws, sid in list(self._session_map.items()):
            last = self._last_activity.get(sid, now)
            if (now - last) > _IDLE_TIMEOUT_SECONDS:
                idle_pairs.append((ws, sid))
        for ws, sid in idle_pairs:
            logger.warning("Closing idle session %s (idle > %ds)", sid, _IDLE_TIMEOUT_SECONDS)
            try:
                await ws.close(code=4000, message=b"Session timed out due to inactivity")
            except Exception:
                pass
            self.cleanup_session(ws, sid)

    async def _idle_check_loop(self) -> None:
        """Background task: scan for idle sessions every 60 seconds."""
        while True:
            try:
                await self.close_idle_sessions()
            except Exception as e:
                logger.warning("Idle check error: %s", e)
            await asyncio.sleep(60)

    def start_idle_checker(self) -> None:
        """Start the background idle-session checker. Safe to call multiple times."""
        if self._idle_check_task is None or self._idle_check_task.done():
            self._idle_check_task = asyncio.ensure_future(self._idle_check_loop())

    def stop_idle_checker(self) -> None:
        """Cancel the idle checker background task."""
        if self._idle_check_task and not self._idle_check_task.done():
            self._idle_check_task.cancel()

    async def emit_session_identifiers(
        self,
        client_ws: web.WebSocketResponse,
        event_type: str,
        identifiers: SessionIdentifiers | None,
    ) -> None:
        if identifiers is None:
            return
        await client_ws.send_json(
            {
                "type": event_type,
                "sessionToken": identifiers.session_token,
                "roundTripIndex": identifiers.round_trip_index,
                "roundTripToken": identifiers.round_trip_token,
            }
        )
