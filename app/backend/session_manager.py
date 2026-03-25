"""Session lifecycle management for Sonic AI Drive-Thru realtime middleware.

Handles per-connection session creation, tracking, cleanup, greeting state,
session identifiers, and context window monitoring.
"""

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

# ── Context Window Monitoring ──
_CTX_MAX_TOKENS = _context_cfg.get("max_tokens", 128000)
_CTX_WARNING_PCT = _context_cfg.get("warning_threshold_pct", 80)
_CTX_CRITICAL_PCT = _context_cfg.get("critical_threshold_pct", 95)

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
    """Manages WebSocket session lifecycle: creation, tracking, cleanup, greeting state."""

    def __init__(self, prompt_loader=None):
        self._session_map: dict[web.WebSocketResponse, str] = {}
        self._sent_greeting: set[str] = set()
        self._context_monitors: dict[str, ContextMonitor] = {}
        if prompt_loader is not None:
            self._greeting_msg = prompt_loader.get_greeting_json_str()
        else:
            self._greeting_msg = _DEFAULT_GREETING_MSG

    @property
    def greeting_msg(self) -> str:
        return self._greeting_msg

    def create_session(self, ws: web.WebSocketResponse) -> str:
        """Create a new order session and map it to the WebSocket connection."""
        session_id = order_state_singleton.create_session()
        self._session_map[ws] = session_id
        self._context_monitors[session_id] = ContextMonitor(session_id)
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
        self._session_map.pop(ws, None)

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
