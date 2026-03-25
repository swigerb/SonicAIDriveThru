"""Audio processing and echo suppression for Sonic AI Drive-Thru realtime middleware.

Handles echo suppression state machine, audio passthrough optimization,
and verbose audio logging.
"""

import asyncio
import json
import logging
import os
import pathlib
import re
from datetime import datetime, timezone
from typing import Any

from config_loader import get_config

logger = logging.getLogger("sonic-drive-in")

_config = get_config()
_audio_cfg = _config.get("audio", {})

# ── Verbose diagnostic logger ──
vlogger = logging.getLogger("sonic-verbose")
_VERBOSE_GLOBAL = os.environ.get("VERBOSE_LOGGING", "").lower() in ("true", "1", "yes")
if _VERBOSE_GLOBAL:
    vlogger.setLevel(logging.DEBUG)
    if not vlogger.handlers:
        _handler = logging.StreamHandler()
        _handler.setFormatter(logging.Formatter("%(message)s"))
        vlogger.addHandler(_handler)
else:
    vlogger.setLevel(logging.WARNING)

# ── Verbose file logging ──
_VERBOSE_LOG_FILE_GLOBAL = os.environ.get("VERBOSE_LOG_FILE", "").lower() in ("true", "1", "yes")
_LOGS_DIR = pathlib.Path(__file__).parent / "logs"


def create_verbose_file_handler() -> logging.FileHandler:
    """Create a FileHandler that writes verbose logs to a timestamped file in app/backend/logs/."""
    _LOGS_DIR.mkdir(parents=True, exist_ok=True)
    ts = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H-%M")
    log_path = _LOGS_DIR / f"verbose-{ts}.log"
    fh = logging.FileHandler(log_path, encoding="utf-8")
    fh.setLevel(logging.DEBUG)
    fh.setFormatter(logging.Formatter("%(message)s"))
    fh.stream.reconfigure(line_buffering=True)  # type: ignore[union-attr]
    header = f"═══ Verbose Log Started: {datetime.now(timezone.utc).isoformat()} ═══"
    fh.stream.write(header + "\n")
    fh.stream.flush()
    logger.info("Verbose log file opened: %s", log_path)
    return fh


def remove_verbose_file_handler(fh: logging.FileHandler) -> None:
    """Remove a file handler from the verbose logger and close it cleanly."""
    vlogger.removeHandler(fh)
    try:
        fh.close()
    except Exception:
        pass


# If VERBOSE_LOG_FILE env var is set, attach a global file handler at module load.
_global_file_handler: logging.FileHandler | None = None
if _VERBOSE_LOG_FILE_GLOBAL:
    vlogger.setLevel(logging.DEBUG)
    if not any(isinstance(h, logging.StreamHandler) and not isinstance(h, logging.FileHandler) for h in vlogger.handlers):
        _sh = logging.StreamHandler()
        _sh.setFormatter(logging.Formatter("%(message)s"))
        vlogger.addHandler(_sh)
    _global_file_handler = create_verbose_file_handler()
    vlogger.addHandler(_global_file_handler)

# Max characters of tool result text to log
_VERBOSE_RESULT_TRUNCATE = _config.get("logging", {}).get("verbose_result_truncate", 500)

# High-frequency server message types that never need middleware modification.
_PASSTHROUGH_SERVER_TYPES = frozenset({
    "response.audio.delta",
    "response.audio.done",
    "response.audio_transcript.delta",
    "response.audio_transcript.done",
    "response.text.delta",
    "response.text.done",
    "response.content_part.added",
    "response.content_part.done",
    "input_audio_buffer.speech_started",
    "input_audio_buffer.speech_stopped",
    "input_audio_buffer.committed",
    "rate_limits.updated",
})

# Client messages that never need middleware modification.
_PASSTHROUGH_CLIENT_TYPES = frozenset({
    "input_audio_buffer.append",
    "input_audio_buffer.clear",
    "input_audio_buffer.commit",
})

# Regex to extract "type":"..." from raw JSON without full parse.
TYPE_RE = re.compile(r'"type"\s*:\s*"([^"]+)"')

# Pre-serialized static payloads
RESPONSE_CREATE_MSG = json.dumps({"type": "response.create"})
INPUT_AUDIO_CLEAR_MSG = json.dumps({"type": "input_audio_buffer.clear"})

# Cooldown period (seconds) after AI audio ends before accepting user audio.
ECHO_COOLDOWN_SEC = _audio_cfg.get("echo_cooldown_seconds", 1.5)

# Fast substring markers for echo suppression
MARKER_AUDIO_APPEND = '"input_audio_buffer.append"'
MARKER_AUDIO_DELTA = '"response.audio.delta"'
MARKER_AUDIO_DONE = '"response.audio.done"'
MARKER_SPEECH_STARTED = '"input_audio_buffer.speech_started"'
MARKER_SESSION_UPDATE = '"session.update"'
MARKER_SESSION_UPDATED = '"session.updated"'
MARKER_RESPONSE_CANCEL = '"response.cancel"'
MARKER_VERBOSE_LOGGING = '"extension.set_verbose_logging"'
MARKER_LOG_TO_FILE = '"extension.set_log_to_file"'


def vlog(verbose: bool, msg: str, *args: Any) -> None:
    """Log to sonic-verbose at DEBUG level if this session has verbose enabled."""
    if verbose or _VERBOSE_GLOBAL:
        vlogger.debug(msg, *args)


class EchoSuppressor:
    """Per-connection echo suppression state machine.

    Tracks whether the AI is currently speaking, manages cooldown periods,
    and handles greeting-specific echo blocking. Safe without locks in
    single-threaded asyncio.
    """
    __slots__ = ("ai_speaking", "cooldown_end", "greeting_in_progress")

    def __init__(self):
        self.ai_speaking = False
        self.cooldown_end = 0.0
        self.greeting_in_progress = False

    def should_suppress_audio(self, loop_time: float) -> bool:
        """Return True if user audio should be dropped (AI speaking or cooldown active)."""
        return self.ai_speaking or loop_time < self.cooldown_end

    def on_audio_delta(self, verbose: bool = False) -> None:
        """AI started sending audio — begin suppression."""
        if not self.ai_speaking:
            logger.debug("Echo suppression: AI speaking — suppressing user audio")
            vlog(verbose, "─── [Echo] ai_speaking=True — suppressing user audio ───")
        self.ai_speaking = True

    def on_audio_done(self, loop: asyncio.AbstractEventLoop, target_ws: Any, verbose: bool = False) -> None:
        """AI finished sending audio — start cooldown and flush echo."""
        self.ai_speaking = False
        if self.greeting_in_progress:
            actual_cooldown = ECHO_COOLDOWN_SEC * 2
            self.greeting_in_progress = False
            logger.debug("Echo suppression: greeting audio done — extended cooldown %.1fs", actual_cooldown)
        else:
            actual_cooldown = ECHO_COOLDOWN_SEC
        self.cooldown_end = loop.time() + actual_cooldown
        logger.debug("Echo suppression: AI audio done — cooldown %.1fs", actual_cooldown)
        vlog(verbose, "─── [Echo] ai_speaking=False — cooldown %.1fs ───", actual_cooldown)
        # Flush any echoed audio that leaked into OpenAI's buffer
        asyncio.ensure_future(target_ws.send_str(INPUT_AUDIO_CLEAR_MSG))
        # Schedule a second flush after cooldown expires
        def _make_delayed_flush(tws=target_ws):
            if not tws.closed:
                asyncio.ensure_future(tws.send_str(INPUT_AUDIO_CLEAR_MSG))
        loop.call_later(actual_cooldown, _make_delayed_flush)

    def on_speech_started(self, verbose: bool = False) -> bool:
        """Server VAD detected speech. Returns True if it should be ignored (greeting echo)."""
        if self.greeting_in_progress:
            logger.debug("Echo suppression: ignoring speech_started during greeting")
            vlog(verbose, "─── [Echo] speech_started IGNORED (greeting in progress) ───")
            return True
        if self.ai_speaking:
            logger.debug("Echo suppression: barge-in detected — resuming user audio")
            vlog(verbose, "─── [Echo] Barge-in — ai_speaking=False, cooldown reset ───")
        self.ai_speaking = False
        self.cooldown_end = 0.0
        return False

    def on_barge_in(self, verbose: bool = False) -> None:
        """Client sent response.cancel — user wants to speak."""
        logger.info("Client sent response.cancel — disabling echo suppression for barge-in")
        vlog(verbose, "─── [Client] response.cancel — barge-in, echo suppression OFF ───")
        self.ai_speaking = False
        self.cooldown_end = 0.0

    def start_greeting_suppression(self, verbose: bool = False) -> None:
        """Pre-set suppression before greeting fires."""
        self.ai_speaking = True
        self.greeting_in_progress = True
        vlog(verbose, "  Echo suppression: ai_speaking=True (pre-set for greeting)")
