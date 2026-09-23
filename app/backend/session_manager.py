"""Session lifecycle management for Sonic AI Drive-Thru realtime middleware.

Handles per-connection session creation, tracking, cleanup, greeting state,
session identifiers, context window monitoring, concurrency limits, idle
timeout enforcement, and the detached "grace hold" that lets a guest resume
their order after a transport drop.

Lifecycle::

    create_session ──► ATTACHED ──(transport close)──► DETACHED ──(grace or idle expiry)──► ENDED
                          │  ▲                            │
                          │  └──────(extension.resume)────┘
                          └──(idle close 4000 / end_session)──────────────────────────────► ENDED

The idle clock runs from the guest's last activity and keeps running while the
session is detached, so a detached session expires at
``min(detached_at + grace_seconds, last_activity + idle_timeout_seconds)``.
"""

import asyncio
import hashlib
import hmac
import json
import logging
import secrets
import time
from collections import OrderedDict, deque
from collections.abc import Callable
from dataclasses import dataclass

from aiohttp import web

import conformance_hooks
from config_loader import get_config
from order_state import SessionIdentifiers, order_state_singleton

logger = logging.getLogger("sonic-drive-in")

_config = get_config()
_context_cfg = _config.get("context", {})
_security_cfg = _config.get("security", {})
_resume_cfg = _config.get("resume", {}) or {}

# ── Context Window Monitoring ──
_CTX_MAX_TOKENS = _context_cfg.get("max_tokens", 128000)
_CTX_WARNING_PCT = _context_cfg.get("warning_threshold_pct", 80)
_CTX_CRITICAL_PCT = _context_cfg.get("critical_threshold_pct", 95)

# ── Session Limits ──
_MAX_CONCURRENT_SESSIONS = _security_cfg.get("max_concurrent_sessions", 10)
_IDLE_TIMEOUT_SECONDS = conformance_hooks.seconds(
    "CONFORMANCE_IDLE_TIMEOUT_SECONDS", _security_cfg.get("idle_timeout_seconds", 300)
)

# ── Resume (grace hold after a transport drop) ──
_RESUME_ENABLED = bool(_resume_cfg.get("enabled", True))
_RESUME_GRACE_SECONDS = conformance_hooks.seconds(
    "CONFORMANCE_GRACE_SECONDS", float(_resume_cfg.get("grace_seconds", 120))
)
_RESUME_MAX_DETACHED = int(_resume_cfg.get("max_detached", 20))
_RESUME_HISTORY_TURNS = int(_resume_cfg.get("history_turns", 6))
_RESUME_HISTORY_CHARS = int(_resume_cfg.get("history_chars", 2000))
_RESUME_NUDGE_AFTER_SECONDS = conformance_hooks.seconds(
    "CONFORMANCE_NUDGE_AFTER_SECONDS", float(_resume_cfg.get("nudge_after_seconds", 30))
)
_RESUME_FIRST_FRAME_TIMEOUT_SECONDS = conformance_hooks.seconds(
    "CONFORMANCE_FIRST_FRAME_TIMEOUT_SECONDS", float(_resume_cfg.get("first_frame_timeout_seconds", 2.0))
)
_RESUME_SWEEP_INTERVAL_SECONDS = conformance_hooks.seconds(
    "CONFORMANCE_SWEEP_INTERVAL_SECONDS", float(_resume_cfg.get("sweep_interval_seconds", 15))
)

# Close code for an intentional idle close. Application-range (4000-4999) so the
# browser can tell it apart from transport errors (1002/1006/1011) and must not
# auto-reconnect into a live-mic session. Mirrors WS_CLOSE_IDLE_TIMEOUT in
# app/frontend/src/hooks/useRealtime.tsx. An idle close ends the session: the
# order is deleted and it can never be resumed.
IDLE_CLOSE_CODE = 4000
IDLE_CLOSE_REASON = "idle_timeout"

# Close code for a socket whose session was resumed on a newer socket (e.g. a
# half-open socket after a network switch). The browser must not reconnect.
SUPERSEDED_CLOSE_CODE = 4002
SUPERSEDED_CLOSE_REASON = "superseded"

# The guest explicitly ended the session (extension.end_session).
SESSION_ENDED_CLOSE_CODE = 1000
SESSION_ENDED_CLOSE_REASON = "session_ended"

# Resume ids are secrets.token_urlsafe(32): 43 chars. Anything far outside that is malformed.
_RESUME_ID_MIN_LEN = 32
_RESUME_ID_MAX_LEN = 128


def _resume_digest(resume_id: str) -> str:
    return hashlib.sha256(resume_id.encode("utf-8", "replace")).hexdigest()


def resume_id_fingerprint(resume_id: object) -> str:
    """The only form of a resume id that may be logged: sha256(id)[:8]."""
    if not isinstance(resume_id, str) or not resume_id:
        return "none"
    return _resume_digest(resume_id)[:8]


# Rehydration for a resumed session's new upstream: one system item, sent after
# the bootstrap session.update and before any response.create.
_REHYDRATION_PREAMBLE = (
    "[Connection restored] The guest's connection dropped for a moment and is back. This is the SAME "
    "guest continuing the SAME order. Do NOT greet them again, do not welcome them back, do not "
    "mention the connection, and do not speak until the guest speaks. Then carry on exactly where "
    "the conversation left off. The current order below is authoritative; use get_order if you need "
    "it again and update_order for any change."
)

# Sent once, if the guest stays silent for resume.nudge_after_seconds after a resume.
_NUDGE_TEXT = (
    "The guest has been quiet since their connection came back. In one short, friendly sentence, "
    "in your carhop persona, ask whether they need anything else with their order. Do not greet "
    "them again, do not mention the connection, and do not read the order back."
)


@dataclass
class ResumeOutcome:
    accepted: bool
    session_id: str | None = None
    reason: str | None = None
    # The rotated resume id for the browser to store (accepted only).
    resume_id: str | None = None
    # A socket still attached to the resumed session, to be closed with 4002.
    stale_ws: web.WebSocketResponse | None = None
    # True if the guest had already been greeted (conversation in progress).
    conversation_started: bool = False

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
    """Manages WebSocket session lifecycle: creation, tracking, detach/resume,
    cleanup, greeting state, concurrency limits, and idle timeout."""

    def __init__(self, prompt_loader=None, clock: Callable[[], float] | None = None):
        # ws -> session_id for ATTACHED sessions only (the concurrency cap counts these).
        self._session_map: dict[web.WebSocketResponse, str] = {}
        # session_id -> the ws it is attached to (reverse of _session_map).
        self._attached: dict[str, web.WebSocketResponse] = {}
        # session_id -> clock() when it detached; oldest first for LRU eviction.
        self._detached: OrderedDict[str, float] = OrderedDict()
        self._sent_greeting: set[str] = set()
        self._context_monitors: dict[str, ContextMonitor] = {}
        self._last_activity: dict[str, float] = {}
        # Resume credentials are stored only as sha256 digests.
        self._resume_digests: dict[str, str] = {}   # session_id -> digest
        self._resume_index: dict[str, str] = {}     # digest -> session_id
        # Last few guest/carhop turns per session, replayed into a resumed upstream.
        self._transcripts: dict[str, deque[tuple[str, str]]] = {}
        self._idle_check_task: asyncio.Task | None = None
        self._clock: Callable[[], float] = clock or time.monotonic

        self.resume_enabled = _RESUME_ENABLED
        self.grace_seconds = _RESUME_GRACE_SECONDS
        self.max_detached = _RESUME_MAX_DETACHED
        self.history_turns = _RESUME_HISTORY_TURNS
        self.history_chars = _RESUME_HISTORY_CHARS
        self.nudge_after_seconds = _RESUME_NUDGE_AFTER_SECONDS
        self.first_frame_timeout_seconds = _RESUME_FIRST_FRAME_TIMEOUT_SECONDS
        self.sweep_interval_seconds = _RESUME_SWEEP_INTERVAL_SECONDS

        if prompt_loader is not None:
            self._greeting_msg = prompt_loader.get_greeting_json_str()
        else:
            self._greeting_msg = _DEFAULT_GREETING_MSG

    @property
    def greeting_msg(self) -> str:
        return self._greeting_msg

    @property
    def idle_timeout_seconds(self) -> float:
        # Read at call time so tests can patch the module global.
        return _IDLE_TIMEOUT_SECONDS

    @property
    def active_session_count(self) -> int:
        """Attached sessions only. Detached (grace-held) sessions have no socket
        and no upstream, so they don't count against the concurrency cap."""
        return len(self._session_map)

    @property
    def detached_session_count(self) -> int:
        return len(self._detached)

    def can_accept_session(self) -> bool:
        """Return True if we haven't hit the concurrent session limit."""
        return self.active_session_count < _MAX_CONCURRENT_SESSIONS

    def now(self) -> float:
        return self._clock()

    def touch_activity(self, session_id: str) -> None:
        """Record guest activity. Drives the idle clock (which also bounds the grace hold)."""
        self._last_activity[session_id] = self._clock()

    def create_session(self, ws: web.WebSocketResponse) -> str:
        """Create a new order session and map it to the WebSocket connection."""
        session_id = order_state_singleton.create_session()
        self._session_map[ws] = session_id
        self._attached[session_id] = ws
        self._context_monitors[session_id] = ContextMonitor(session_id)
        self._last_activity[session_id] = self._clock()
        return session_id

    def get_session_id(self, ws: web.WebSocketResponse) -> str | None:
        return self._session_map.get(ws)

    def is_detached(self, session_id: str) -> bool:
        return session_id in self._detached

    def has_sent_greeting(self, session_id: str) -> bool:
        return session_id in self._sent_greeting

    def mark_greeting_sent(self, session_id: str) -> None:
        self._sent_greeting.add(session_id)

    def get_context_monitor(self, session_id: str | None) -> ContextMonitor | None:
        if session_id is None:
            return None
        return self._context_monitors.get(session_id)

    # ── End / detach ──

    def end_session(self, session_id: str | None, reason: str = "ended") -> None:
        """Permanently end a session: delete the order and every piece of resume state."""
        if session_id is None:
            return
        ws = self._attached.pop(session_id, None)
        if ws is not None and self._session_map.get(ws) == session_id:
            del self._session_map[ws]
        self._detached.pop(session_id, None)
        digest = self._resume_digests.pop(session_id, None)
        if digest is not None:
            self._resume_index.pop(digest, None)
        order_state_singleton.delete_session(session_id)
        self._sent_greeting.discard(session_id)
        self._context_monitors.pop(session_id, None)
        self._last_activity.pop(session_id, None)
        self._transcripts.pop(session_id, None)
        logger.info("Session %s ended (%s)", session_id, reason)

    def cleanup_session(self, ws: web.WebSocketResponse, session_id: str | None) -> None:
        """Remove all state associated with a WebSocket connection (ends the session)."""
        self._session_map.pop(ws, None)
        self.end_session(session_id, "cleanup")

    def detached_expires_at(self, session_id: str) -> float | None:
        """When a detached session expires: the grace hold, capped by the idle budget."""
        detached_at = self._detached.get(session_id)
        if detached_at is None:
            return None
        last = self._last_activity.get(session_id, detached_at)
        return min(detached_at + self.grace_seconds, last + self.idle_timeout_seconds)

    def detach_session(self, ws: web.WebSocketResponse, session_id: str | None, reason: str = "transport_close") -> None:
        """Called when a client socket closes. Holds the order for the grace period
        if the socket was still the attached one; a no-op if the session was already
        ended (idle close) or taken over by a resume (4002 superseded)."""
        mapped = self._session_map.pop(ws, None)
        if mapped is None or session_id is None or mapped != session_id:
            return
        if self._attached.get(session_id) is not ws:
            return
        del self._attached[session_id]

        if not self.resume_enabled or self.grace_seconds <= 0:
            self.end_session(session_id, f"{reason}; resume disabled")
            return

        now = self._clock()
        self._detached[session_id] = now
        self._detached.move_to_end(session_id)
        expires = self.detached_expires_at(session_id)
        if expires is None or expires <= now:
            self.end_session(session_id, f"{reason}; idle budget exhausted")
            return
        logger.info(
            "Session %s detached (%s); holding order for %.0fs", session_id, reason, expires - now,
        )
        while len(self._detached) > max(self.max_detached, 0):
            oldest, _ = next(iter(self._detached.items()))
            self.end_session(oldest, "evicted: max_detached reached")

    # ── Transcript ring buffer and rehydration ──

    def record_turn(self, session_id: str | None, role: str, text: str | None) -> None:
        """Keep the last resume.history_turns turns ("guest" / "carhop") for rehydration."""
        if session_id is None or self.history_turns <= 0 or session_id not in order_state_singleton.sessions:
            return
        text = (text or "").strip()
        if not text:
            return
        turns = self._transcripts.get(session_id)
        if turns is None or turns.maxlen != self.history_turns:
            turns = deque(turns or (), maxlen=self.history_turns)
            self._transcripts[session_id] = turns
        turns.append((role, text[: max(self.history_chars, 0)]))

    def recent_turns(self, session_id: str) -> list[tuple[str, str]]:
        """The buffered turns, oldest first, capped at resume.history_chars in total (newest kept)."""
        budget = max(self.history_chars, 0)
        kept: list[tuple[str, str]] = []
        for role, text in reversed(self._transcripts.get(session_id, ())):
            if budget <= 0:
                break
            if len(text) > budget:
                text = "…" + text[len(text) - budget + 1:]
            kept.append((role, text))
            budget -= len(text)
        kept.reverse()
        return kept

    def build_rehydration_item(self, session_id: str) -> str:
        """One system conversation.item.create carrying the order and the recent turns."""
        order_json = order_state_singleton.get_order_summary_json(session_id)
        turns = self.recent_turns(session_id)
        history = "\n".join(f"{'Guest' if role == 'guest' else 'Carhop'}: {text}" for role, text in turns)
        text = (f"{_REHYDRATION_PREAMBLE}\n\nCurrent order (JSON): {order_json}\n\n"
                f"Recent conversation (oldest first):\n{history or '(none recorded)'}")
        return json.dumps({
            "type": "conversation.item.create",
            "item": {"type": "message", "role": "system", "content": [{"type": "input_text", "text": text}]},
        })

    @staticmethod
    def build_nudge_item() -> str:
        return json.dumps({
            "type": "conversation.item.create",
            "item": {"type": "message", "role": "system", "content": [{"type": "input_text", "text": _NUDGE_TEXT}]},
        })

    # ── Resume credential ──

    def issue_resume_id(self, session_id: str | None) -> str | None:
        """Mint a fresh 256-bit resume id for the session, invalidating any previous one.
        Only its digest is kept; the raw value goes to the browser over the socket and
        nowhere else (never in URLs or logs)."""
        if not self.resume_enabled or session_id is None or session_id not in order_state_singleton.sessions:
            return None
        old = self._resume_digests.pop(session_id, None)
        if old is not None:
            self._resume_index.pop(old, None)
        resume_id = secrets.token_urlsafe(32)
        digest = _resume_digest(resume_id)
        self._resume_digests[session_id] = digest
        self._resume_index[digest] = session_id
        return resume_id

    def resume(self, ws: web.WebSocketResponse, resume_id: object) -> ResumeOutcome:
        """Re-attach the session identified by ``resume_id`` to ``ws``.

        Single use: the presented id is consumed and a rotated one is returned.
        The provisional session created for ``ws`` on connect is ended. If the
        resumed session is still attached to another socket (half-open), that
        socket is handed back as ``stale_ws`` to be closed with 4002.
        """
        if not self.resume_enabled:
            return ResumeOutcome(False, reason="disabled")
        if not isinstance(resume_id, str) or not (_RESUME_ID_MIN_LEN <= len(resume_id) <= _RESUME_ID_MAX_LEN):
            return ResumeOutcome(False, reason="malformed")
        digest = _resume_digest(resume_id)
        session_id = self._resume_index.get(digest)
        stored = self._resume_digests.get(session_id) if session_id is not None else None
        if (session_id is None or stored is None or not hmac.compare_digest(stored, digest)
                or session_id not in order_state_singleton.sessions):
            return ResumeOutcome(False, reason="unknown")
        current = self._session_map.get(ws)
        if current == session_id:
            return ResumeOutcome(False, reason="unknown")

        now = self._clock()
        last = self._last_activity.get(session_id, now)
        expires = self.detached_expires_at(session_id)
        if now - last > self.idle_timeout_seconds or (expires is not None and now >= expires):
            self.end_session(session_id, "resume attempted after expiry")
            return ResumeOutcome(False, reason="expired")

        # Consume the presented id before anything else can use it.
        self._resume_index.pop(digest, None)
        self._resume_digests.pop(session_id, None)

        stale_ws = self._attached.get(session_id)
        if stale_ws is ws:
            stale_ws = None
        if stale_ws is not None:
            self._session_map.pop(stale_ws, None)
            del self._attached[session_id]
        if current is not None:
            self.end_session(current, "replaced by resume")
        self._detached.pop(session_id, None)
        self._session_map[ws] = session_id
        self._attached[session_id] = ws
        new_id = self.issue_resume_id(session_id)
        logger.info("Session %s resumed (resume id %s -> %s)%s", session_id,
                    resume_id_fingerprint(resume_id), resume_id_fingerprint(new_id),
                    "; superseding a still-attached socket" if stale_ws is not None else "")
        return ResumeOutcome(True, session_id=session_id, resume_id=new_id, stale_ws=stale_ws,
                             conversation_started=session_id in self._sent_greeting)

    def sweep_detached(self) -> int:
        """End detached sessions whose grace hold or idle budget has run out."""
        now = self._clock()
        expired = [
            sid for sid in list(self._detached)
            if (exp := self.detached_expires_at(sid)) is not None and now >= exp
        ]
        for sid in expired:
            self.end_session(sid, "grace hold expired")
        return len(expired)

    async def close_idle_sessions(self) -> None:
        """End attached sessions idle beyond the timeout, then close their sockets
        with 4000. The session is ended *before* the close so the socket's own
        close handling can't detach it: an idle close is never resumable."""
        now = self._clock()
        idle_timeout = self.idle_timeout_seconds
        idle_pairs: list[tuple[web.WebSocketResponse, str]] = []
        for ws, sid in list(self._session_map.items()):
            last = self._last_activity.get(sid, now)
            if (now - last) > idle_timeout:
                idle_pairs.append((ws, sid))
        for ws, sid in idle_pairs:
            logger.warning("Closing idle session %s (idle > %ds)", sid, idle_timeout)
            self._session_map.pop(ws, None)
            self.end_session(sid, IDLE_CLOSE_REASON)
            try:
                await ws.close(code=IDLE_CLOSE_CODE, message=IDLE_CLOSE_REASON.encode())
            except Exception:
                pass
        self.sweep_detached()

    async def _idle_check_loop(self) -> None:
        """Background task: scan for idle and expired detached sessions."""
        while True:
            try:
                await self.close_idle_sessions()
            except Exception as e:
                logger.warning("Idle check error: %s", e)
            await asyncio.sleep(self.sweep_interval_seconds)

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
        extra: dict | None = None,
    ) -> None:
        if identifiers is None:
            return
        await client_ws.send_json(
            {
                "type": event_type,
                "sessionToken": identifiers.session_token,
                "roundTripIndex": identifiers.round_trip_index,
                "roundTripToken": identifiers.round_trip_token,
                **(extra or {}),
            }
        )
