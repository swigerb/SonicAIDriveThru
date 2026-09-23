"""Rate-limit recovery for realtime model responses.

All three drive-thru demos share one Azure OpenAI quota. When a response is
rate-limited the service fails it with no output, and without this the guest
hears silence -- a frozen carhop. The ladder, per failed response (never per
session):

1. silent retry: wait `retry_delay_seconds` (or the service's hint, clamped to
   [0.5 s, 5 s]) and send `response.create` again. The failed response produced
   nothing, so the guest's input (or a tool's function_call_output) is still the
   last thing in the conversation and the model simply regenerates.
2. retry 1 also rate-limited: tell the browser (`extension.rate_limited`,
   attempt 1) so it plays a pre-recorded apology clip, then retry once more after
   `second_retry_delay_seconds` (or the hint, clamped to [2 s, 8 s]).
3. retry 2 fails too: `extension.rate_limited` with `final: true` and stop. The
   session stays up and the guest's next turn proceeds normally.

A pending retry is dropped as soon as anything else takes the turn: guest speech,
any response that starts (`response.created` that is not our retry, e.g. VAD or a
tool follow-up), a `response.create` from the browser, or the socket detaching.
The resume nudge checks `busy` and stays quiet while a retry is pending or
running. A retry never fires while another response is in flight, and it is not
guest activity (it never touches the idle clock).
"""

from __future__ import annotations

import asyncio
import json
import logging
import re
from collections.abc import Awaitable, Callable, Mapping
from dataclasses import dataclass
from typing import Any

import test_hooks

logger = logging.getLogger("sonic-drive-in")

RATE_LIMITED_EVENT = "extension.rate_limited"
ENABLED_ENV = "RATE_LIMIT_RECOVERY_ENABLED"

FIRST_RETRY_BOUNDS = (0.5, 5.0)
SECOND_RETRY_BOUNDS = (2.0, 8.0)

_RESPONSE_CREATE_MSG = json.dumps({"type": "response.create"})

# "Please try again in 1.5s", "try again in 250ms", "retry after 7 seconds".
_HINT_RE = re.compile(
    r"(?:try\s+again|retry)\s+(?:in|after)\s+(\d+(?:\.\d+)?)\s*"
    r"(ms|msec|millisecond|milliseconds|s|sec|secs|second|seconds)\b",
    re.IGNORECASE,
)


def is_rate_limit_error(error: Any) -> bool:
    """True if an error object's `code` or `type` names a rate limit."""
    if not isinstance(error, dict):
        return False
    return any("rate_limit" in str(error.get(field) or "").lower() for field in ("code", "type"))


def rate_limit_error_of_response_done(message: dict) -> dict | None:
    """The rate-limit error of a failed `response.done`, else None."""
    response = message.get("response")
    if not isinstance(response, dict) or response.get("status") != "failed":
        return None
    details = response.get("status_details")
    error = details.get("error") if isinstance(details, dict) else None
    return error if is_rate_limit_error(error) else None


def rate_limit_error_of_error_event(message: dict) -> dict | None:
    error = message.get("error")
    return error if is_rate_limit_error(error) else None


def parse_retry_hint(text: Any) -> float | None:
    """Seconds the service asked us to wait, from an error message, else None."""
    if not isinstance(text, str):
        return None
    match = _HINT_RE.search(text)
    if match is None:
        return None
    value = float(match.group(1))
    return value / 1000.0 if match.group(2).lower().startswith("m") else value


def retry_delay(hint: float | None, default: float, bounds: tuple[float, float]) -> float:
    """The service's hint clamped to `bounds`; `default` when there is no hint."""
    if hint is None:
        return default
    low, high = bounds
    return min(max(hint, low), high)


def _truthy(value: Any) -> bool:
    return str(value).strip().lower() in ("1", "true", "yes", "on")


@dataclass(frozen=True)
class RateLimitSettings:
    enabled: bool = True
    retry_delay_seconds: float = 1.5
    second_retry_delay_seconds: float = 4.0
    max_retries: int = 2

    @classmethod
    def from_config(cls, config: Mapping[str, Any], environ: Mapping[str, str] | None = None) -> RateLimitSettings:
        """`resilience.rate_limit` from config.yaml; RATE_LIMIT_RECOVERY_ENABLED overrides `enabled`."""
        resilience = config.get("resilience") or {}
        cfg = resilience.get("rate_limit") or {}
        enabled = bool(cfg.get("enabled", True))
        env_value = (environ or {}).get(ENABLED_ENV)
        if env_value is not None and str(env_value).strip():
            enabled = _truthy(env_value)
        return cls(
            enabled=enabled,
            retry_delay_seconds=test_hooks.seconds(
                "CONFORMANCE_RATE_LIMIT_RETRY_DELAY_SECONDS", float(cfg.get("retry_delay_seconds", 1.5))
            ),
            second_retry_delay_seconds=test_hooks.seconds(
                "CONFORMANCE_RATE_LIMIT_SECOND_RETRY_DELAY_SECONDS", float(cfg.get("second_retry_delay_seconds", 4.0))
            ),
            max_retries=max(0, int(cfg.get("max_retries", 2))),
        )


class RateLimitRecovery:
    """Per-connection recovery ladder. Single-threaded asyncio, no locks.

    `send_upstream` / `send_client` take a serialised frame and a dict; `sleep`
    is injectable so tests never really wait.
    """

    def __init__(self, settings: RateLimitSettings,
                 send_upstream: Callable[[str], Awaitable[Any]],
                 send_client: Callable[[dict], Awaitable[Any]],
                 sleep: Callable[[float], Awaitable[Any]] = asyncio.sleep,
                 session_id: str | None = None) -> None:
        self.settings = settings
        self._send_upstream = send_upstream
        self._send_client = send_client
        self._sleep = sleep
        self.session_id = session_id
        # Retries already sent for the response currently being recovered.
        self.attempt = 0
        # A retry of ours was sent and its response has not finished yet.
        self.awaiting_retry = False
        self.response_in_flight = False
        # The ladder ran out; ignore duplicate failure reports until the guest's next turn.
        self.exhausted = False
        self._pending: asyncio.Task | None = None
        self.retries_sent = 0

    @property
    def enabled(self) -> bool:
        return self.settings.enabled

    @property
    def pending(self) -> bool:
        return self._pending is not None and not self._pending.done()

    @property
    def busy(self) -> bool:
        """A retry is scheduled or its response is still running."""
        return self.pending or self.awaiting_retry

    # ── signals from the upstream socket ──

    def on_response_created(self) -> None:
        self.response_in_flight = True
        if self.awaiting_retry:
            return                              # our own retry starting
        if self.pending:
            self._cancel_pending("a new response started")
        self._reset()

    async def on_response_done(self, message: dict) -> bool:
        """Returns True if this `response.done` was a rate-limit failure we handled
        (the caller then drops it); False leaves existing behaviour untouched."""
        self.response_in_flight = False
        error = rate_limit_error_of_response_done(message) if self.enabled else None
        if error is None:
            if self.awaiting_retry:
                self._reset()                   # our retry finished (or failed for another reason)
            return False
        response_id = (message.get("response") or {}).get("id")
        await self._on_failure(error, f"response.done {response_id}")
        return True

    async def on_error(self, message: dict) -> bool:
        """An `error` event already known NOT to reject one of our session.updates."""
        error = rate_limit_error_of_error_event(message) if self.enabled else None
        if error is None:
            return False
        if self.response_in_flight:
            # The running response's own response.done will report the failure.
            logger.warning("Rate-limit error while a response is in flight (code=%s); waiting for its "
                           "response.done (session=%s)", error.get("code"), self.session_id)
            return True
        await self._on_failure(error, "error event")
        return True

    def on_guest_speech(self) -> None:
        if self.pending:
            self._cancel_pending("guest started speaking")
        self._reset()

    def on_external_response_create(self, source: str) -> None:
        """Someone else (greeting, nudge, tool follow-up, browser) asked for a response."""
        if self.pending:
            self._cancel_pending(f"{source} requested a response")
        self._reset()

    def cancel(self, reason: str) -> None:
        if self.pending:
            self._cancel_pending(reason)
        self._reset()

    # ── ladder ──

    def _reset(self) -> None:
        self.attempt = 0
        self.awaiting_retry = False
        self.exhausted = False

    def _cancel_pending(self, reason: str) -> None:
        assert self._pending is not None
        self._pending.cancel()
        self._pending = None
        logger.info("Rate-limit retry cancelled: %s (session=%s)", reason, self.session_id)

    async def _on_failure(self, error: dict, source: str) -> None:
        hint = parse_retry_hint(error.get("message"))
        logger.warning("Model response rate-limited (%s): code=%s type=%s retry_hint=%s (session=%s)",
                       source, error.get("code"), error.get("type"),
                       f"{hint:.3f}s" if hint is not None else "none", self.session_id)
        if self.pending:
            logger.info("Rate-limit failure while a retry is already pending; same attempt (session=%s)",
                        self.session_id)
            return
        if self.exhausted:
            logger.info("Rate-limit failure after the final retry; waiting for the guest's next turn "
                        "(session=%s)", self.session_id)
            return
        if not self.awaiting_retry:
            self.attempt = 0                    # a fresh failure, not one of our retries
        self.awaiting_retry = False
        attempt = self.attempt
        if attempt >= self.settings.max_retries:
            logger.warning("Rate-limit retries exhausted after %d attempt(s); asking the guest to repeat "
                           "(session=%s)", attempt, self.session_id)
            self._reset()
            self.exhausted = True
            await self._notify({"type": RATE_LIMITED_EVENT, "attempt": attempt, "final": True})
            return
        if attempt == 0:
            delay = retry_delay(hint, self.settings.retry_delay_seconds, FIRST_RETRY_BOUNDS)
        else:
            delay = retry_delay(hint, self.settings.second_retry_delay_seconds, SECOND_RETRY_BOUNDS)
            await self._notify({"type": RATE_LIMITED_EVENT, "attempt": attempt})
        self._pending = asyncio.ensure_future(self._retry_after(delay, attempt + 1))

    async def _retry_after(self, delay: float, attempt: int) -> None:
        await self._sleep(delay)
        self._pending = None
        if self.response_in_flight:
            logger.info("Rate-limit retry %d skipped: another response is already running (session=%s)",
                        attempt, self.session_id)
            self._reset()
            return
        self.attempt = attempt
        self.awaiting_retry = True
        self.retries_sent += 1
        logger.info("Rate-limit retry %d: response.create after %.2fs (session=%s)", attempt, delay, self.session_id)
        try:
            await self._send_upstream(_RESPONSE_CREATE_MSG)
        except Exception as exc:  # noqa: BLE001 - a closing socket must not crash the forwarder
            logger.info("Rate-limit retry %d not sent: %s (session=%s)", attempt, exc, self.session_id)
            self._reset()

    async def _notify(self, payload: dict) -> None:
        try:
            await self._send_client(payload)
        except Exception as exc:  # noqa: BLE001
            logger.info("Could not send %s to the browser: %s (session=%s)", payload.get("type"), exc, self.session_id)
