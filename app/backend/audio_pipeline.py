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
from collections.abc import Callable
from datetime import UTC, datetime
from typing import Any

import aiohttp

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
    ts = datetime.now(UTC).strftime("%Y-%m-%dT%H-%M")
    log_path = _LOGS_DIR / f"verbose-{ts}.log"
    fh = logging.FileHandler(log_path, encoding="utf-8")
    fh.setLevel(logging.DEBUG)
    fh.setFormatter(logging.Formatter("%(message)s"))
    fh.stream.reconfigure(line_buffering=True)  # type: ignore[union-attr]
    header = f"═══ Verbose Log Started: {datetime.now(UTC).isoformat()} ═══"
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
# Includes both GA (v1) and legacy (2024-10-01-preview) event names.
_PASSTHROUGH_SERVER_TYPES = frozenset({
    # GA event names (gpt-realtime-2.1 via /openai/v1/realtime)
    "response.output_audio.delta",
    "response.output_audio.done",
    "response.output_audio_transcript.delta",
    "response.output_audio_transcript.done",
    "response.output_text.delta",
    "response.output_text.done",
    # Legacy event names (2024-10-01-preview via /openai/realtime)
    "response.audio.delta",
    "response.audio.done",
    "response.audio_transcript.delta",
    "response.audio_transcript.done",
    "response.text.delta",
    "response.text.done",
    # Unchanged across versions
    "response.content_part.added",
    "response.content_part.done",
    "input_audio_buffer.speech_started",
    "input_audio_buffer.speech_stopped",
    "input_audio_buffer.committed",
    "rate_limits.updated",
})

# GA → legacy event name translation for client compatibility.
# The frontend expects legacy names; the GA /openai/v1 endpoint sends these.
_GA_TO_LEGACY_EVENTS: dict[str, str] = {
    "response.output_audio.delta": "response.audio.delta",
    "response.output_audio.done": "response.audio.done",
    "response.output_audio_transcript.delta": "response.audio_transcript.delta",
    "response.output_audio_transcript.done": "response.audio_transcript.done",
    "response.output_text.delta": "response.text.delta",
    "response.output_text.done": "response.text.done",
}

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

# Fast substring markers for echo suppression (GA event names)
MARKER_AUDIO_APPEND = '"input_audio_buffer.append"'
MARKER_AUDIO_DELTA = '"response.output_audio.delta"'
MARKER_AUDIO_DONE = '"response.output_audio.done"'
MARKER_SPEECH_STARTED = '"input_audio_buffer.speech_started"'
MARKER_SESSION_UPDATE = '"session.update"'
MARKER_SESSION_UPDATED = '"session.updated"'
MARKER_RESPONSE_CANCEL = '"response.cancel"'
MARKER_RESPONSE_DONE = '"response.done"'
MARKER_VERBOSE_LOGGING = '"extension.set_verbose_logging"'
MARKER_LOG_TO_FILE = '"extension.set_log_to_file"'
MARKER_SET_VOICE = '"extension.set_voice"'
MARKER_TRANSCRIPTION_COMPLETED = '"conversation.item.input_audio_transcription.completed"'
MARKER_RESPONSE_CREATE = '"response.create"'
MARKER_RESUME = '"extension.resume"'
MARKER_END_SESSION = '"extension.end_session"'
# Legacy markers for backward compatibility
MARKER_AUDIO_DELTA_LEGACY = '"response.audio.delta"'
MARKER_AUDIO_DONE_LEGACY = '"response.audio.done"'


def vlog(verbose: bool, msg: str, *args: Any) -> None:
    """Log to sonic-verbose at DEBUG level if this session has verbose enabled."""
    if verbose or _VERBOSE_GLOBAL:
        vlogger.debug(msg, *args)


async def _best_effort_send(ws: Any, msg: str) -> None:
    """Send `msg` on `ws`, tolerating a socket that's already closing/closed.

    swigerb/SonicAIDriveThru#59: the echo-flush sends below are advisory --
    nothing downstream depends on them succeeding -- but firing them via a
    bare `asyncio.ensure_future(ws.send_str(...))` let a `ClientConnectionResetError`
    ("Cannot write to closing transport"), raised when the upstream closes right
    after `response.output_audio.done`, surface as an "unretrieved" task
    exception: noisy `ERROR:asyncio:...` logs on every such disconnect, in
    production too (not A2-specific -- reproducible on `dev`).

    The pre-send `ws.closed` check is still a check-then-act race (the socket
    can flip to closing between the check and the write), so the exception
    handling here -- not the check alone -- is what actually closes the gap.
    Any task built from this coroutine can never complete with an unhandled
    exception, so wrapping it (via `_spawn` or plain `ensure_future`) is safe
    without also awaiting or inspecting the resulting task.
    """
    if ws.closed:
        return
    try:
        await ws.send_str(msg)
    except (ConnectionResetError, aiohttp.ClientError) as e:
        logger.debug("Best-effort send skipped on a closing socket: %s", e)


class EchoSuppressor:
    """Per-connection echo suppression state machine.

    Tracks whether the AI is currently speaking, manages cooldown periods,
    and handles greeting-specific echo blocking. Safe without locks in
    single-threaded asyncio.
    """
    __slots__ = (
        "ai_speaking",
        "cooldown_end",
        "greeting_in_progress",
        "_flush_handle",
        "_greeting_awaiting_retry",
        "_greeting_audio_seen",
        "_closed",
    )

    def __init__(self):
        self.ai_speaking = False
        self.cooldown_end = 0.0
        self.greeting_in_progress = False
        # swigerb/SonicAIDriveThru#59: handle for the delayed post-cooldown
        # flush's `loop.call_later`, so `close()` can cancel it on teardown
        # instead of letting it fire (and attempt a send) after the
        # connection has already gone away.
        self._flush_handle: asyncio.TimerHandle | None = None
        # swigerb/SonicAIDriveThru#59 (PR #58 re-review, "F1"): once True,
        # close() is terminal -- on_audio_done() must not re-arm the flush
        # (spawn a send, or schedule a new delayed-flush timer) even if it's
        # called again afterwards (e.g. a leftover response.done racing the
        # connection's own finally block during teardown).
        self._closed = False
        # swigerb/SonicAIDriveThru#48 (PR #58 re-review, "M1"): set by
        # on_response_done() when a greeting's response.done arrives with no
        # audio ever rendered -- the rate-limit ladder (RateLimitRecovery)
        # may still retry that same greeting with a bare response.create. If
        # it does, the retry's own first audio delta must re-enter greeting
        # suppression (see on_audio_delta()) instead of being treated as an
        # ordinary response -- otherwise speech_started during the retry's
        # audio is no longer ignored, and on_audio_done() gives it only the
        # normal cooldown instead of the doubled post-greeting one.
        self._greeting_awaiting_retry = False
        # swigerb/SonicAIDriveThru#48 (PR #58 re-review, "S1"): set by
        # on_audio_delta() the moment a real audio delta arrives while a
        # greeting is in progress. `ai_speaking` alone can't tell "nothing
        # rendered" apart from "at least some audio rendered but no
        # audio.done arrived to complete it" -- start_greeting_suppression()
        # pre-sets ai_speaking=True before any audio exists, and
        # on_audio_delta() also sets it, so both the no-audio and the
        # partial-audio case reach on_response_done() with ai_speaking still
        # True. Only the no-audio case should get the instant, no-cooldown
        # unmute; partial audio already reached the guest, so it carries the
        # same residual echo risk a normal on_audio_done() completion does.
        self._greeting_audio_seen = False

    def should_suppress_audio(self, loop_time: float) -> bool:
        """Return True if user audio should be dropped (AI speaking or cooldown active)."""
        return self.ai_speaking or loop_time < self.cooldown_end

    def on_audio_delta(self, verbose: bool = False) -> None:
        """AI started sending audio — begin suppression."""
        if self._greeting_awaiting_retry:
            # #48 M1: this audio belongs to the rate-limit ladder's retry of a
            # greeting whose first attempt produced no audio at all -- treat
            # it as the greeting's own audio (see on_response_done() and the
            # class docstring above), not an ordinary response.
            self._greeting_awaiting_retry = False
            self.greeting_in_progress = True
        if self.greeting_in_progress:
            # #48 S1: at least one real delta has now rendered for this
            # greeting -- on_response_done(), if it fires before audio.done,
            # must no longer treat this as the "nothing rendered" case.
            self._greeting_audio_seen = True
        if not self.ai_speaking:
            logger.debug("Echo suppression: AI speaking — suppressing user audio")
            vlog(verbose, "─── [Echo] ai_speaking=True — suppressing user audio ───")
        self.ai_speaking = True

    def on_audio_done(
        self,
        loop: asyncio.AbstractEventLoop,
        target_ws: Any,
        verbose: bool = False,
        spawn: Callable[[Any], asyncio.Task] | None = None,
    ) -> None:
        """AI finished sending audio — start cooldown and flush echo.

        `spawn` lets the caller (rtmt.py's `_forward_messages`) route the two
        fire-and-forget flush sends through its own `_spawn`/`_BACKGROUND_TASKS`
        tracking (swigerb/SonicAIDriveThru#59) so they get the same
        held-until-done reference as every other background task on the
        connection, without `audio_pipeline.py` importing from `rtmt.py`
        (which imports from here, so that would be circular). It defaults to
        `asyncio.ensure_future` -- today's behaviour -- when the caller (e.g.
        the existing unit tests) doesn't pass one.
        """
        spawn = spawn or asyncio.ensure_future
        if self._closed:
            # swigerb/SonicAIDriveThru#59 (PR #58 re-review, "F1"): close()
            # is terminal -- once the connection's teardown has run, a
            # later on_audio_done() call (e.g. a leftover response.done
            # racing the connection's own finally block) must not re-arm
            # the flush by spawning a send or scheduling a fresh
            # delayed-flush timer.
            return
        self.ai_speaking = False
        if self.greeting_in_progress:
            actual_cooldown = ECHO_COOLDOWN_SEC * 2
            self.greeting_in_progress = False
            self._greeting_awaiting_retry = False
            self._greeting_audio_seen = False
            logger.debug("Echo suppression: greeting audio done — extended cooldown %.1fs", actual_cooldown)
        else:
            actual_cooldown = ECHO_COOLDOWN_SEC
        self.cooldown_end = loop.time() + actual_cooldown
        logger.debug("Echo suppression: AI audio done — cooldown %.1fs", actual_cooldown)
        vlog(verbose, "─── [Echo] ai_speaking=False — cooldown %.1fs ───", actual_cooldown)
        # Flush any echoed audio that leaked into OpenAI's buffer
        spawn(_best_effort_send(target_ws, INPUT_AUDIO_CLEAR_MSG))
        # Schedule a second flush after cooldown expires. Cancel any flush
        # still pending from a previous on_audio_done() call first -- two
        # audio.done events closer together than a cooldown would otherwise
        # leave an earlier timer alive alongside this new one.
        if self._flush_handle is not None:
            self._flush_handle.cancel()
        def _make_delayed_flush(tws=target_ws):
            self._flush_handle = None
            spawn(_best_effort_send(tws, INPUT_AUDIO_CLEAR_MSG))
        self._flush_handle = loop.call_later(actual_cooldown, _make_delayed_flush)

    def close(self) -> None:
        """Cancel any delayed echo flush still pending, and become terminal.

        swigerb/SonicAIDriveThru#59: called from the connection's teardown
        (rtmt.py `_forward_messages`'s `finally`) so a timer scheduled by
        `on_audio_done()` can't fire -- and attempt a send -- after the
        connection has already gone away. `_best_effort_send()` would still
        no-op/catch cleanly if this were skipped, but cancelling the timer
        closes the race window outright instead of relying on that as the
        only backstop.

        PR #58 re-review "F1": also marks this suppressor closed so any
        LATER call to `on_audio_done()` is a no-op instead of re-arming the
        flush it just cancelled -- `close()` must be a one-way, terminal
        transition, not just a one-time timer cancellation.
        """
        self._closed = True
        if self._flush_handle is not None:
            self._flush_handle.cancel()
            self._flush_handle = None

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
        # #48 M1: genuine guest speech (not greeting echo, since the check above
        # already returned) means the guest, not a rate-limit ladder retry, is
        # what's actually happening next -- a pending retry no longer gets to
        # re-claim greeting suppression for whatever audio follows.
        self._greeting_awaiting_retry = False
        return False

    def on_barge_in(self, verbose: bool = False) -> None:
        """Client sent response.cancel — user wants to speak."""
        logger.info("Client sent response.cancel — disabling echo suppression for barge-in")
        vlog(verbose, "─── [Client] response.cancel — barge-in, echo suppression OFF ───")
        self.ai_speaking = False
        self.cooldown_end = 0.0
        # #48 M1: see on_speech_started() above -- an explicit browser barge-in
        # is just as much "the guest, not a retry" as genuine speech is.
        self._greeting_awaiting_retry = False

    def on_external_response_create(self) -> None:
        """Someone other than the rate-limit ladder itself asked for a fresh response
        (today: rtmt.py calls this alongside `RateLimitRecovery.on_external_response_create()`
        for the browser's own `response.create`).

        PR #58 re-review Nit: a pending `_greeting_awaiting_retry` (#48 M1) is speculating
        that the *ladder's own* retry of the greeting is what produces the next audio delta.
        If a genuinely new, unrelated response gets created first, its audio must not be
        mistaken for the greeting's continuation -- same reasoning as `on_speech_started()`
        and `on_barge_in()` above, which already cancel this speculative re-arm for guest
        speech and an explicit barge-in respectively.
        """
        self._greeting_awaiting_retry = False

    def start_greeting_suppression(self, verbose: bool = False) -> None:
        """Pre-set suppression before greeting fires."""
        self.ai_speaking = True
        self.greeting_in_progress = True
        # #48 S1: fresh greeting, no audio rendered yet.
        self._greeting_audio_seen = False
        vlog(verbose, "  Echo suppression: ai_speaking=True (pre-set for greeting)")

    def on_response_done(self, loop: asyncio.AbstractEventLoop, target_ws: Any, verbose: bool = False) -> None:
        """A response finished (any status) — the safety net for a greeting with no audio.

        `on_audio_done()` (a real audio delta/done pair) and `on_barge_in()` (browser
        response.cancel) are the two normal ways `ai_speaking` gets cleared. A greeting that
        never produces audio at all (text-only fallback, cancelled/failed before any audio,
        a rate-limited retry with no output) triggers neither, so `should_suppress_audio()`
        would drop the guest's mic forever until they physically interrupt (#48).
        `response.done` is the one event GA guarantees for every response regardless of
        status, so treat it as the fallback: end suppression here too, but only for the
        pending greeting, and only if nothing else already has.

        A rate-limited greeting's `response.done` is exactly this "no audio at all" case,
        and the mic is correctly released here -- but `RateLimitRecovery` may then retry
        that same greeting with a bare `response.create` (PR #58 re-review, "M1"). If it
        does, the greeting isn't actually over: this sets `_greeting_awaiting_retry` so the
        retry's own first audio delta (`on_audio_delta()`) re-enters greeting suppression,
        instead of the retry's `speech_started`/`response.done` being treated as an ordinary
        mid-conversation response (which would fail to ignore echo during the retry's audio
        and give it only the normal, not doubled, post-greeting cooldown).

        A greeting whose audio *started* streaming (at least one delta arrived) but never
        got a completing `audio.done` -- cancelled/errored mid-stream -- is a third case
        (PR #58 re-review, "S1"). `ai_speaking` alone can't distinguish it from "nothing
        rendered": `start_greeting_suppression()` pre-sets it before any audio exists, and
        `on_audio_delta()` also sets it, so both reach here with `ai_speaking` still True.
        But partial audio already reached the guest, carrying the same residual echo risk a
        normal `on_audio_done()` completion does -- `_greeting_audio_seen` (set by
        `on_audio_delta()`) distinguishes the two, and this applies the same doubled
        post-greeting cooldown `on_audio_done()` would, instead of the no-audio case's
        instant unmute.
        """
        if not self.greeting_in_progress:
            return  # no greeting pending, or on_audio_done() already ended it normally.
        self.greeting_in_progress = False
        if not self.ai_speaking:
            # Something else (on_barge_in(), a genuine mid-greeting interrupt) already
            # cleared ai_speaking before this response.done arrived — the bookkeeping flag
            # above is all that's left to clear; don't re-arm a cooldown retroactively.
            return
        if self._greeting_audio_seen:
            # #48 S1: audio started but never completed with audio.done -- treat this
            # response.done exactly like a normal on_audio_done() completion: extended
            # cooldown, no instant unmute, and no retry re-arm (nothing here to retry;
            # RateLimitRecovery only retries a response that produced *no* output at all).
            self.ai_speaking = False
            self._greeting_audio_seen = False
            actual_cooldown = ECHO_COOLDOWN_SEC * 2
            self.cooldown_end = loop.time() + actual_cooldown
            logger.debug(
                "Echo suppression: greeting response.done after partial audio (no audio.done) — "
                "extended cooldown %.1fs",
                actual_cooldown,
            )
            vlog(
                verbose,
                "─── [Echo] response.done, partial audio — ai_speaking=False, extended cooldown %.1fs ───",
                actual_cooldown,
            )
            return
        # Still latched, and no audio was ever seen — nothing was ever actually rendered to
        # the guest (no delta, no completed audio.done). With nothing played, there's no
        # residual/echo risk that would warrant on_audio_done()'s extended post-greeting
        # cooldown -- unmute immediately, the same as an explicit browser barge-in
        # (on_barge_in()), instead of imposing an artificial multi-second mute after a
        # greeting the guest never actually heard.
        self.ai_speaking = False
        self.cooldown_end = 0.0
        # #48 (PR #58 re-review, "M1"): this failed/empty attempt may still be
        # retried by the rate-limit ladder (RateLimitRecovery.on_response_done()
        # schedules a bare response.create for a rate-limited response.done) --
        # if it is, the retry's own first audio delta must re-enter greeting
        # suppression (on_audio_delta()) instead of being treated as an ordinary
        # response. Real guest speech or an explicit barge-in (both above) clear
        # this first if either happens before any retry audio arrives.
        self._greeting_awaiting_retry = True
        logger.debug("Echo suppression: greeting produced no audio — unmuting immediately")
        vlog(verbose, "─── [Echo] response.done, no audio — ai_speaking=False, no cooldown ───")
