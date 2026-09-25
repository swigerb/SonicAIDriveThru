import asyncio
import base64
import copy
import hashlib
import hmac
import json
import logging
import os
import re
import time
import urllib.parse
import uuid
from collections import OrderedDict, deque
from collections.abc import Awaitable, Callable
from enum import Enum
from typing import Any

import aiohttp
from aiohttp import web
from azure.core.credentials import AzureKeyCredential
from azure.identity import DefaultAzureCredential, get_bearer_token_provider

import conformance_hooks
from audio_pipeline import (
    _GA_TO_LEGACY_EVENTS,
    _PASSTHROUGH_SERVER_TYPES,
    _VERBOSE_GLOBAL,
    _VERBOSE_RESULT_TRUNCATE,
    INPUT_AUDIO_CLEAR_MSG as _INPUT_AUDIO_CLEAR_MSG,
    MARKER_AUDIO_APPEND as _MARKER_AUDIO_APPEND,
    MARKER_AUDIO_DELTA as _MARKER_AUDIO_DELTA,
    MARKER_AUDIO_DELTA_LEGACY as _MARKER_AUDIO_DELTA_LEGACY,
    MARKER_AUDIO_DONE as _MARKER_AUDIO_DONE,
    MARKER_AUDIO_DONE_LEGACY as _MARKER_AUDIO_DONE_LEGACY,
    MARKER_END_SESSION as _MARKER_END_SESSION,
    MARKER_LOG_TO_FILE as _MARKER_LOG_TO_FILE,
    MARKER_RESPONSE_DONE as _MARKER_RESPONSE_DONE,
    MARKER_RESUME as _MARKER_RESUME,
    MARKER_SESSION_UPDATED as _MARKER_SESSION_UPDATED,
    MARKER_SET_VOICE as _MARKER_SET_VOICE,
    MARKER_SPEECH_STARTED as _MARKER_SPEECH_STARTED,
    MARKER_TRANSCRIPTION_COMPLETED as _MARKER_TRANSCRIPTION_COMPLETED,
    MARKER_VERBOSE_LOGGING as _MARKER_VERBOSE_LOGGING,
    RESPONSE_CREATE_MSG as _RESPONSE_CREATE_MSG,
    TYPE_RE as _TYPE_RE,
    EchoSuppressor,
    create_verbose_file_handler as _create_verbose_file_handler,
    remove_verbose_file_handler as _remove_verbose_file_handler,
    vlog as _vlog,
    vlogger,
)
from config_loader import get_config
from order_state import order_state_singleton
from rate_limit import RateLimitRecovery, RateLimitSettings, is_rate_limit_error
from session_manager import (
    MIDDLE_TIER_ITEM_ID_PREFIX,
    SESSION_ENDED_CLOSE_CODE,
    SESSION_ENDED_CLOSE_REASON,
    SUPERSEDED_CLOSE_CODE,
    SUPERSEDED_CLOSE_REASON,
    SessionManager,
    new_middle_tier_item_id,
    resume_id_fingerprint,
)

logger = logging.getLogger("sonic-drive-in")

# Load centralized config
_config = get_config()
_conn_cfg = _config.get("connection", {})
_security_cfg = _config.get("security", {})


def _truthy(value: Any) -> bool:
    return str(value).strip().lower() in ("1", "true", "yes", "on")


_ALLOW_CLIENT_LOG_CONTROL_ENV = "ALLOW_CLIENT_LOG_CONTROL"


def _client_log_control_allowed() -> bool:
    """swigerb/SonicAIDriveThru#53: is a browser allowed to change process-wide
    logging (extension.set_verbose_logging / extension.set_log_to_file) right now?

    Off by default in production -- both extensions are process-wide side effects
    (one connection's request affects every OTHER connection sharing the same
    worker process), so a single guest must never be able to flip them on. Allowed
    only when the conformance harness's test hooks are active (live-checked via
    `conformance_hooks.hooks_enabled_now()`, mirroring the `response.create`
    gate's own re-check-on-every-call reasoning -- see that function's
    docstring), or when an operator has explicitly opted in via config.yaml's
    `security.allow_client_log_control` (env override: ALLOW_CLIENT_LOG_CONTROL).
    """
    if conformance_hooks.hooks_enabled_now():
        return True
    env_value = os.environ.get(_ALLOW_CLIENT_LOG_CONTROL_ENV)
    if env_value is not None and env_value.strip():
        return _truthy(env_value)
    return bool(_security_cfg.get("allow_client_log_control", False))


__all__ = ["RTMiddleTier", "RTToolCall", "Tool", "ToolResult", "ToolResultDirection", "configure_realtime_model",
           "deployment_supports_reasoning", "normalize_reasoning_effort", "parse_reasoning_model"]

# Connection tuning constants
_WS_HEARTBEAT_SEC = _conn_cfg.get("ws_heartbeat_seconds", 15.0)
# permessage-deflate on the browser socket. Off by default: aiohttp 3.14.2/3.14.3
# reject the first compressed frame after an initial PONG (aio-libs/aiohttp#13274),
# which is exactly what a browser sends on a socket idle past one heartbeat.
_WS_COMPRESS = bool(_conn_cfg.get("ws_compression", False))
_WS_CONNECT_TIMEOUT = aiohttp.ClientTimeout(
    total=_conn_cfg.get("ws_connect_timeout_total", 30),
    connect=_conn_cfg.get("ws_connect_timeout_connect", 10),
)

# ── HMAC Session Token Utilities ──

def create_hmac_token(secret: bytes, expiry_seconds: int = 900) -> str:
    """Create an HMAC-signed session token with expiry."""
    payload = {"exp": int(time.time()) + expiry_seconds}
    payload_b64 = base64.urlsafe_b64encode(json.dumps(payload).encode()).decode()
    sig = hmac.new(secret, payload_b64.encode(), hashlib.sha256).hexdigest()
    return f"{payload_b64}.{sig}"


def validate_hmac_token(token: str, secret: bytes) -> bool:
    """Validate an HMAC session token (signature + expiry)."""
    if not token or "." not in token:
        return False
    try:
        payload_b64, sig = token.rsplit(".", 1)
        expected_sig = hmac.new(secret, payload_b64.encode(), hashlib.sha256).hexdigest()
        if not hmac.compare_digest(sig, expected_sig):
            return False
        payload = json.loads(base64.urlsafe_b64decode(payload_b64))
        return payload.get("exp", 0) > time.time()
    except Exception:
        return False


# ── Origin validation utilities ──

def _origin_matches_host(origin: str, host: str) -> bool:
    """True iff the `Origin` header's host (and port, if non-default) is an
    *exact* match for the request's `Host` header.

    Replaces a previous `origin.endswith(host)` check (#25), which accepted
    any origin whose netloc merely ended with `host` as a *string suffix* --
    e.g. `https://evil-legituser.example.com` passes
    `"evil-legituser.example.com".endswith("legituser.example.com")`, letting
    a lookalike domain the attacker actually controls pass origin validation
    for a site named `legituser.example.com`. `urllib.parse.urlsplit` gives
    us the real `scheme://host[:port]` authority component of the Origin
    header (never a suffix match), which we compare case-insensitively
    against the literal `Host` header value -- the same shape browsers send
    for same-origin requests (no path, and no port for the scheme's default
    port), so a genuine same-origin request is unaffected.

    An empty `host` (a missing/blank `Host` header) can never be a legitimate
    match -- without it #25's fix degenerates to `urlsplit(origin).netloc ==
    ""`, which a bare/schemeless Origin value like the literal string "null"
    satisfies (PR #30 review, "S4"). Reject outright instead.
    """
    if not host:
        return False
    return urllib.parse.urlsplit(origin).netloc.lower() == host.lower()


# ── Conversation item authorship/wire-format filtering ──

def _drop_from_client(item: dict) -> bool:
    """True if a `conversation.item.*` event's item must never reach the
    browser, across every subtype GA can send it on (`.created`, `.added`,
    `.done`, `.retrieved`):

    - `function_call` / `function_call_output` -- the model's raw tool
      invocation and its result. The browser gets `extension.middle_tier_tool_response`
      instead (see `response.output_item.done`); it must never see the raw item.
    - Anything the middle tier itself authored (the greeting, resume
      rehydration, silence nudge, or a tool's `function_call_output`) --
      identified primarily by its `sonic_mt_`-prefixed item id (authorship;
      swigerb/SonicAIDriveThru#29, PR #30 review "S1"), with the legacy
      `role == "system"` check kept as a second guard for any middle-tier
      item that predates the id convention.

    GA emits `.done` "when the item is finalized" with the *full* item
    (swigerb/SonicAIDriveThru#29 follow-up, PR #30 review "M1") -- carrying
    exactly the same leak surface as `.created`/`.added` if left unfiltered,
    so this same check must run for every subtype, not just the first two.
    """
    item_type = item.get("type")
    if item_type in ("function_call", "function_call_output"):
        return True
    item_id = item.get("id")
    if isinstance(item_id, str) and item_id.startswith(MIDDLE_TIER_ITEM_ID_PREFIX):
        return True
    return item.get("role") == "system"


# ── Browser → upstream event allow-list (swigerb/SonicAIDriveThru#31) ──

# Every event type a browser is allowed to send upstream. Derived from what
# `useRealtime.tsx` (the only frontend code that talks to this socket)
# actually transmits: `session.update`, `input_audio_buffer.append`/`.clear`,
# `response.cancel`. Neither the legacy `input_audio_buffer.commit` nor
# `response.create` is sent by the real frontend (server VAD always
# auto-triggers the model's turn) -- see PR #49 review round 2, "S1" -- so
# both were removed from the *production* allow-list. `response.create` is
# re-added, but ONLY when `conformance_hooks.hooks_enabled_now()` is true
# (see `_CLIENT_TEST_ONLY_TYPES` below): many existing conformance scenarios
# use it as a same-effect stand-in for a server-VAD-triggered turn, and that
# module's own guard test (`tests/test_conformance_hooks.py::
# TestNeverInInfraOrDockerfile`) ensures its enabling env var can never reach
# a real deployment, so a genuine browser can never regain access to it.
#
# `extension.*` types are deliberately NOT listed here: they are fully
# consumed by `_forward_messages`'s from-client-to-server loop (resume,
# end_session, set_verbose_logging, set_log_to_file, set_voice) before a
# message ever reaches `_process_message_to_server`, so they never need an
# entry on this side. A malicious `extension.middle_tier_tool_response` (or
# any other `extension.*`) sent *directly* by the browser -- documented in
# tests/conformance/README.md's GA-validation-fidelity finding #3 as
# currently forwarded unfiltered and rejected only by the real upstream
# service -- falls through unmatched and is correctly dropped by this
# allow-list, since it is not one of the entries below.
_CLIENT_ALLOWED_TYPES = frozenset({
    "session.update",
    "input_audio_buffer.append",
    "input_audio_buffer.clear",
    "response.cancel",
})

# Allowed only when conformance_hooks.hooks_enabled_now() (never in a real
# deployment -- see the module docstring above and conformance_hooks.py's own
# guard test). This deliberately calls the *live* re-check, not the frozen
# `HOOKS_ENABLED` constant: the shared Python pytest process runs many test
# files, and `test_conformance_hooks.py` intentionally `importlib.reload()`s
# conformance_hooks to a disabled state as part of *its own* isolation,
# leaving the frozen constant stuck at `False` for every test file that
# happens to run afterward in the same process (see `hooks_enabled_now()`'s
# docstring for the full explanation). `app/backend/tests/conftest.py` sets
# the enabling env var for the Python unit-test process; tests/conformance's
# BackendProfiles (Default, ShortTimers, FixedClock) all set it for the .NET
# harness's spawned backend, so every existing conformance scenario that
# relies on a browser-simulated `response.create` keeps working unchanged.
_CLIENT_TEST_ONLY_TYPES = frozenset({"response.create"})


# Per-type top-level key allow-list (PR #49 review round 2, "M2"): the
# original #31 filter allow-listed the event *type* but then forwarded the
# browser's original bytes/dict verbatim for every type except
# `response.create`, so any extra top-level key riding along on an
# otherwise-legitimate event (e.g. a forged `item` object smuggled onto
# `input_audio_buffer.clear`) still reached upstream unfiltered. Every
# forwarded event is now rebuilt from scratch (see `_filter_client_to_server`
# and `_process_message_to_server`), keeping only these keys. This also
# subsumes the old response.create-specific "strip the response override"
# logic: `response.create`'s allowed set has no `response` key at all, so no
# override object can ever survive regardless of what the browser attaches.
_CLIENT_TOP_LEVEL_KEYS: dict[str, frozenset[str]] = {
    "session.update": frozenset({"type", "event_id", "session"}),
    "input_audio_buffer.append": frozenset({"type", "event_id", "audio"}),
    "input_audio_buffer.clear": frozenset({"type", "event_id"}),
    "response.cancel": frozenset({"type", "event_id", "response_id"}),
    "response.create": frozenset({"type", "event_id"}),
}

# Value-shape validation for the top-level keys above (PR #49 review round 5,
# "S3"): the allow-list only constrains which keys survive, not their shape.
# `event_id`/`response_id` are short opaque tokens -- useRealtime.tsx never
# sends anything but a short alphanumeric string for either -- and `audio` is
# always base64. See `_filter_client_to_server`'s docstring for why an
# invalid `event_id` only drops the key while an invalid `response_id` drops
# the whole frame.
_CLIENT_EVENT_ID_RE = re.compile(r"^[A-Za-z0-9_-]{1,64}$")
_CLIENT_BASE64_RE = re.compile(r"^[A-Za-z0-9+/]*={0,2}$")

# Session keys the browser may legitimately set (PR #49 review round 2,
# "M3"): exactly what useRealtime.tsx's startSession() sends (see
# `_BOOTSTRAP_CLIENT_SESSION` below, which mirrors the same two keys).
# Everything else GA accepts at the session top level (`prompt`, `tracing`,
# `include`, `truncation`, `model`, `output_modalities`, `instructions`,
# `tools`, `tool_choice`, ...) is server-owned and must come only from
# RTMiddleTier's own configuration. Filtering the browser's session object
# down to this set BEFORE it reaches `_build_session` closes both halves of
# M3 at once: extra GA keys can never arrive (nothing downstream has to
# remember to strip them), and `instructions`'s previous "fail open when
# self.system_message is None" gap is moot, since `instructions` is never
# present in the filtered input for `_build_session` to fail to overwrite.
_CLIENT_SESSION_KEYS = frozenset({"turn_detection", "input_audio_transcription"})

# Sub-key allow-list for the browser's `turn_detection` object (PR #49 review
# round 3, sub-key hardening). Being in `_CLIENT_SESSION_KEYS` only means the
# *key* survives the top-level session filter above -- real GA `server_vad`
# also accepts `create_response`, `interrupt_response` and `idle_timeout_ms`,
# none of which `useRealtime.tsx` ever sends, and none of which are safe to
# take from the browser: `create_response: false` can silence the assistant
# entirely, `interrupt_response`/`idle_timeout_ms` can change barge-in/idle
# behaviour server operators rely on. `_sanitize_turn_detection` below closes
# that off structurally, the same way `_CLIENT_TOP_LEVEL_KEYS` closes off
# forged extra top-level keys on an event.
_TURN_DETECTION_ALLOWED_KEYS = frozenset({"type", "threshold", "prefix_padding_ms", "silence_duration_ms"})
_TURN_DETECTION_NUMERIC_BOUNDS = {
    "threshold": (0, 1),
    "prefix_padding_ms": (0, 5000),
    "silence_duration_ms": (0, 5000),
}
# PR #49 review round 5, "S3": prefix_padding_ms/silence_duration_ms are
# millisecond COUNTS -- only a plain int is a legitimate value (300.5 is
# Rick's probe). threshold legitimately is a float (e.g. 0.7) and is exempt.
_TURN_DETECTION_INT_ONLY_KEYS = frozenset({"prefix_padding_ms", "silence_duration_ms"})


def _sanitize_turn_detection(
        value: Any, session_id: str | None = None,
        limiter: "_ClientFrameDropWarningLimiter | None" = None) -> dict | None:
    """Allow-list a browser-sent `turn_detection` object down to exactly the
    four sub-keys `useRealtime.tsx`'s `startSession()` ever sends
    (`type`, `threshold`, `prefix_padding_ms`, `silence_duration_ms`).

    `type` must be the literal `"server_vad"` -- anything else (including a
    non-dict `value`, or a missing `type`) is not a partial-filter case: the
    WHOLE object is rejected (returns `None`) so the caller falls back to the
    server's own known-good default (`_BOOTSTRAP_CLIENT_SESSION`) rather than
    forwarding a half-sanitized, possibly GA-invalid `turn_detection`.

    Each numeric sub-key is bounds-checked independently and dropped (not the
    whole object) if it is out of range or the wrong type: `threshold` must be
    a number (`int` or `float`) in `[0, 1]`; `prefix_padding_ms`/
    `silence_duration_ms` must be plain `int`s (not `float` -- PR #49 review
    round 5 "S3": a millisecond count like `300.5` is never legitimate, and
    `useRealtime.tsx` never sends one) in `[0, 5000]`. `bool` is deliberately
    rejected for all three even though Python's `bool` is an `int` subclass,
    since `true`/`false` is never a legitimate value for any of them. Any
    other sub-key (`create_response`, `interrupt_response`, `idle_timeout_ms`,
    ...) is dropped unconditionally -- it is simply never in the allow-list,
    regardless of its value.
    """
    if not isinstance(value, dict) or value.get("type") != "server_vad":
        return None
    sanitized: dict = {"type": "server_vad"}
    dropped = []
    for key, (lo, hi) in _TURN_DETECTION_NUMERIC_BOUNDS.items():
        if key not in value:
            continue
        candidate = value[key]
        numeric_type_ok = (
            isinstance(candidate, int) if key in _TURN_DETECTION_INT_ONLY_KEYS
            else isinstance(candidate, (int, float))
        )
        if numeric_type_ok and not isinstance(candidate, bool) and lo <= candidate <= hi:
            sanitized[key] = candidate
        else:
            dropped.append(key)
    extra = sorted(k for k in value if k not in _TURN_DETECTION_ALLOWED_KEYS)
    if dropped or extra:
        _warn_dropped_frame(
            limiter,
            "Sanitized client turn_detection: dropped out-of-bounds/invalid sub-key(s) %s and "
            "disallowed sub-key(s) %s (session=%s)",
            _truncate_key_list_for_log(dropped), _truncate_key_list_for_log(extra), session_id)
    return sanitized


def _truncate_for_log(value: Any, max_len: int = 64) -> str:
    """Render a browser-supplied value for a log line, truncated to at most
    `max_len` characters of its `repr()` (PR #58 review round 2, "F2").

    A forged frame can put an arbitrarily large value in a field that ends
    up quoted in a WARNING line -- e.g. a multi-megabyte `type` string on a
    disallowed-type probe, or a long `voice` string on a forged
    `extension.set_voice` -- turning one bad frame into a multi-megabyte log
    line. `repr()` is computed first (so the output still looks like the
    `%r` callers previously used -- quoted and escaped) and only the
    resulting text is truncated; `repr()` is never called on anything after
    truncation, since that could reintroduce the same unbounded cost for a
    sufficiently pathological `__repr__`.
    """
    text = repr(value)
    if len(text) <= max_len:
        return text
    return f"{text[:max_len]}...(truncated, {len(text)} chars total)"


def _truncate_key_list_for_log(keys: list, max_items: int = 10) -> str:
    """Render a browser-supplied list of dict key NAMES for a log line,
    bounded in both dimensions (PR #58 re-review, "F2").

    `_truncate_for_log` bounds any single browser-supplied *value*, but the
    stripped/disallowed key-name lists logged by `_filter_client_to_server`
    (top-level keys) and `_sanitize_turn_detection` (`turn_detection`
    sub-keys) were passed straight to `%s` as a Python list -- a forged frame
    can put an arbitrarily long string as a dict key (JSON object keys are
    just strings), or supply a huge number of bogus keys, and either one
    reproduces the same unbounded-log-line risk `_truncate_for_log` closes
    for values. Each key name is truncated individually first (so one
    pathological key can't blow up the line even when `max_items` would
    otherwise keep it), then the list itself is capped to the first
    `max_items` entries with a "(+N more)" count for the rest.
    """
    rendered = [_truncate_for_log(k) for k in keys[:max_items]]
    remaining = len(keys) - len(rendered)
    text = f"[{', '.join(rendered)}]"
    if remaining > 0:
        text += f" (+{remaining} more)"
    return text


class _ClientFrameDropWarningLimiter:
    """Rate-limits the per-frame WARNING logs emitted while validating one
    browser→upstream frame or extension message (PR #58 review round 2, "F2").

    A misbehaving or actively probing browser client can otherwise flood the
    backend's logs with one WARNING line per bad frame -- `input_audio_buffer
    .append` alone is ~10 frames/sec, so a client that trips a validation
    failure on every frame (accidentally or deliberately) produces the same
    log volume as legitimate traffic. The signal that matters ("this
    connection is sending something the allow-list rejects") is already
    established by the first few lines; every one after that adds noise, not
    new information.

    Logs the first `LOG_LIMIT` per-frame warnings verbatim (each still names
    its own specific reason), then one summary line, then stays silent for
    the rest of this connection's lifetime. One instance is created per
    connection (alongside `_SessionUpdateGuard`, at the same scope) and
    threaded through `_filter_client_to_server` / `_sanitize_turn_detection`
    / `_process_message_to_server` and the extension-message handlers.
    Passing `None` (the default everywhere) disables rate limiting
    entirely -- every existing unit test that calls `_filter_client_to_server`
    directly, with no per-connection context, keeps logging every warning.
    """

    LOG_LIMIT = 5

    def __init__(self) -> None:
        self._count = 0

    def warning(self, msg: str, *args: Any) -> None:
        self._count += 1
        if self._count <= self.LOG_LIMIT:
            logger.warning(msg, *args)
        elif self._count == self.LOG_LIMIT + 1:
            logger.warning(
                "Suppressing further per-frame drop/strip warnings on this connection "
                "after the first %d (this connection is still being served normally)",
                self.LOG_LIMIT)


def _warn_dropped_frame(limiter: "_ClientFrameDropWarningLimiter | None", msg: str, *args: Any) -> None:
    """Log a per-frame validation WARNING, rate-limited if `limiter` is given."""
    if limiter is None:
        logger.warning(msg, *args)
    else:
        limiter.warning(msg, *args)


def _filter_client_to_server(
        message: dict, session_id: str | None = None,
        limiter: "_ClientFrameDropWarningLimiter | None" = None) -> dict | None:
    """Allow-list and rebuild a browser→upstream event before
    `_process_message_to_server` forwards it (swigerb/SonicAIDriveThru#31,
    hardened per PR #49 review round 2).

    Before the original #31 filter, only `session.update` was recognised by
    name -- every other client event type, including ones the real frontend
    never sends, fell through unmatched and was forwarded to the upstream
    socket completely unchanged. A malicious/compromised browser could
    therefore:

    1. Send `response.create` carrying a `response.instructions` /
       `response.tools` / `response.tool_choice` override, hijacking the
       carhop's prompt or tool surface for that one turn.
    2. Send `conversation.item.create` with `role: "system"` (or its
       `"developer"` alias), injecting an operator-trusted instruction into
       the transcript the model conditions on.
    3. Send `conversation.item.retrieve` to read back any conversation item
       verbatim -- including middle-tier-authored ones (rehydration/nudge/
       tool-result items) that `_drop_from_client` only protects on the
       server-to-client side, never on a direct client-initiated retrieval.

    The frontend legitimately sends neither `conversation.item.create` nor
    `conversation.item.retrieve` (verified: no `app/frontend/src` code sends
    either), so both are simply absent from `_CLIENT_ALLOWED_TYPES` --
    rejecting the whole event type closes vectors 2 and 3 more robustly than
    filtering their dangerous sub-fields would. Anything not in the
    allow-list (these two, or any unrecognised/future type) is dropped
    entirely: not forwarded, and the socket is not closed -- an unexpected
    frame on an otherwise-legitimate session is not itself proof of
    compromise, so we log once at WARNING and keep serving the session.

    The review round 2 "M2" finding closed the remaining hole: this now
    ALWAYS returns a freshly rebuilt dict containing only the allowed
    top-level keys for the event's type (`_CLIENT_TOP_LEVEL_KEYS`), never
    the browser's original object -- so no extra/forged top-level key can
    ride along on an otherwise-legitimate event either. Callers must not
    assume the return value `is` (identical object to) the input.

    `message["type"]` must already be a `str` -- callers are responsible for
    validating that before calling this (see `_process_message_to_server`'s
    "S2" handling of `{"type": ["x"]}`-shaped frames, which would otherwise
    raise `TypeError` on the frozenset membership test below).

    PR #49 review round 5, "S3": the top-level allow-list above only
    constrains WHICH keys survive, not the SHAPE of their values -- a forged
    non-string `audio`/`event_id`/`response_id` (an object or array instead
    of the string `useRealtime.tsx` always sends) used to ride through
    untouched. `audio` must be a base64-alphabet string (else the whole
    frame is dropped -- there is no safe partial-audio fallback);
    `event_id`/`response_id` must be short strings matching
    `^[A-Za-z0-9_-]{1,64}$`. `event_id` is advisory (the server always mints
    its own via `_SessionUpdateGuard.stamp`, "S2"), so an invalid one just
    has the key stripped; `response_id` is load-bearing for `response.cancel`
    (it says WHICH response to cancel, with no safe fallback), so an invalid
    one drops the whole frame.

    Returns the rebuilt message to forward, or `None` if the whole event
    must be dropped.
    """
    msg_type = message.get("type", "")

    allowed = msg_type in _CLIENT_ALLOWED_TYPES or (
        msg_type in _CLIENT_TEST_ONLY_TYPES and conformance_hooks.hooks_enabled_now())
    if not allowed:
        _warn_dropped_frame(
            limiter, "Dropped disallowed client→server event type %s (session=%s)",
            _truncate_for_log(msg_type), session_id)
        return None

    allowed_keys = _CLIENT_TOP_LEVEL_KEYS[msg_type]
    dropped_keys = sorted(k for k in message if k not in allowed_keys)
    if dropped_keys:
        _warn_dropped_frame(
            limiter, "Stripped disallowed top-level key(s) %s from client %s (session=%s)",
            _truncate_key_list_for_log(dropped_keys), msg_type, session_id)

    filtered = {k: v for k, v in message.items() if k in allowed_keys}

    if "audio" in filtered and not (
            isinstance(filtered["audio"], str) and _CLIENT_BASE64_RE.fullmatch(filtered["audio"])):
        _warn_dropped_frame(
            limiter,
            "Dropped input_audio_buffer.append with a non-base64-alphabet audio value (session=%s)", session_id)
        return None

    if "event_id" in filtered and not (
            isinstance(filtered["event_id"], str) and _CLIENT_EVENT_ID_RE.fullmatch(filtered["event_id"])):
        _warn_dropped_frame(limiter, "Stripped an invalid client event_id (session=%s)", session_id)
        del filtered["event_id"]

    if "response_id" in filtered and not (
            isinstance(filtered["response_id"], str) and _CLIENT_EVENT_ID_RE.fullmatch(filtered["response_id"])):
        _warn_dropped_frame(
            limiter, "Dropped response.cancel with an invalid response_id (session=%s)", session_id)
        return None

    return filtered


def _dump_client_to_server(
        payload: dict, session_id: str | None = None,
        limiter: "_ClientFrameDropWarningLimiter | None" = None) -> str | None:
    """Serialise an already-filtered client→server payload, or return `None`
    if it can't be serialised safely (PR #49 review round 5, "S3").

    Every value currently allowed through `_filter_client_to_server` /
    `_sanitize_turn_detection` is already bounds- or shape-checked, so this
    is a defense-in-depth backstop rather than a live vector today: uses
    `allow_nan=False` so a `NaN`/`Infinity`/`-Infinity` float that somehow
    reaches this call (Python's `json.loads` accepts these non-standard
    literals by default, even though `json.dumps` also emits them by
    default) raises `ValueError` instead of being silently re-serialised
    into a frame most JSON parsers -- including a future strict C#
    backend -- would reject or mishandle. The whole frame is dropped rather
    than partially fixed, since there is no way to know which value was the
    bad one without walking the whole structure.
    """
    try:
        return json.dumps(payload, allow_nan=False)
    except ValueError:
        _warn_dropped_frame(
            limiter,
            "Dropped client→server frame that failed to re-serialise (NaN/Infinity) (session=%s)", session_id)
        return None


# Voices `extension.set_voice` may adopt (PR #49 review round 5, "M1"). The
# handler used to trust ANY non-empty string the browser sent, forward it
# upstream immediately (unlocked path), and -- before #43 was fixed -- adopt
# it as the process-wide default for every later guest too. Defaults to the
# ten GA voices `app/frontend/src/lib/voices.ts` offers the picker (kept in
# sync by hand: the frontend is deliberately not imported into the Python
# backend). Overridable per brand via config.yaml's `model.allowed_voices`
# (see `configure_realtime_model`) for sibling drive-thru brand deployments.
_DEFAULT_ALLOWED_VOICES = frozenset({
    "alloy", "ash", "ballad", "coral", "echo",
    "sage", "shimmer", "verse", "marin", "cedar",
})


def _sanitize_voice(candidate: Any, allowed_voices: frozenset[str]) -> str | None:
    """Validate a browser-supplied `extension.set_voice` value.

    Returns the voice name if it is a non-empty string present in
    `allowed_voices`, else `None`. Callers must drop the whole message and
    log a WARNING on `None` rather than forwarding an unknown value upstream
    or adopting it as this connection's (or any future connection's) voice.
    """
    if isinstance(candidate, str) and candidate in allowed_voices:
        return candidate
    return None


# Sentinel default for the `voice` keyword threaded through `_build_session`
# and everything downstream of it (PR #49 review round 5, "S1"/#43). Plain
# `None` already means something ("send no voice at all" -- see
# `_build_session`), so a distinct sentinel marks "caller didn't pass one" and
# falls back to `self.voice_choice`, the pre-#43 behaviour every existing
# caller (production and tests) still relies on. `_forward_messages` is the
# only caller that ever passes an explicit voice -- this connection's own
# frozen value, per #43.
_VOICE_UNSET = object()


# Exact-match fast path for the browser's mic-audio frame (PR #49 review
# round 2, "M1"). `_process_message_to_server`'s previous fast path used
# `audio_pipeline.TYPE_RE` -- an unanchored `"type":"..."` substring search --
# and skipped JSON parsing/filtering entirely whenever the *first* such
# substring anywhere in the raw frame happened to name a passthrough type.
# Combined with `json.loads` keeping the *last* value for a repeated key,
# that let a forged frame reach upstream completely unfiltered via: a
# repeated top-level `"type"` key, a `"type"` substring nested inside an
# arbitrary free-form sub-object (e.g. `response.metadata.type`), or the same
# trick played on `session.update` specifically (which skips `_build_session`
# -- letting server-owned session keys through -- if it takes the old fast
# path). The fast path is now anchored to EXACTLY the one frame
# `useRealtime.tsx`'s `addUserAudio()` sends -- confirmed byte-for-byte
# against `JSON.stringify({type: "input_audio_buffer.append", audio:
# base64Audio})`'s compact, key-order-stable output. Anything that doesn't
# match this exactly -- extra whitespace, a different key order, extra
# keys, a spoofed nested "type" -- falls through to the slow (parse +
# filter) path below, which still allows a *genuine* append frame in any
# other shape (see `_CLIENT_TOP_LEVEL_KEYS`), just at the cost of a full
# parse instead of a regex match.
_CLIENT_APPEND_FAST_PATH_RE = re.compile(
    r'\{"type":"input_audio_buffer\.append","audio":"[A-Za-z0-9+/=]*"\}')


# swigerb/SonicAIDriveThru#36, PR #58 re-review "S2"/"S1": after this many
# *consecutive* failed tool rounds on one connection with no guest turn in
# between, stop auto-continuing the model (see the "response.done" case's
# tools_pending handling below). Retrying the identical broken flow silently a
# third time in a row is more likely to compound a bad order state than help --
# the model still gets the function_call_output(s) (so it can tell the guest
# something went wrong), and at the cap it's given one server-authored,
# tool-free response.create so it can apologise out loud and ask the guest,
# instead of either silently retrying tools again or going dead-air.
_TOOL_FAILURE_CAP = 2

# swigerb/SonicAIDriveThru#36, PR #58 re-review "S1": sent (server-authored,
# never client-originated -- the #31 browser->upstream allow-list in
# _filter_client_to_server is unaffected) in place of a bare response.create
# once the consecutive-failed-round cap is reached. `response.tool_choice` is
# a documented GA field on response.create's `response` object -- this same
# codebase's own #31 work already established it as a real override the
# model honours (rtmt.py strips a *browser*-supplied response.tool_choice
# for exactly that reason; see the README "backend contract" section and
# _filter_client_to_server's RESPONSE_OVERRIDE_KEYS) -- "none" tells the
# model it must not call a tool on this turn, so it can only speak.
#
# PR #58 re-review round 3: `response.tool_choice="none"` ALONE is not enough. A live
# probe against Sonic's real gpt-realtime-2.1 deployment
# (session-state/.../probe_tool_choice_none.py, 2026-09-25), with two failed update_order
# rounds' function_call_output already in context, showed the model falsely told the
# guest an item was added/changed in 2 of 3 runs even though tool_choice="none" correctly
# stopped it from calling a tool. Response-level `instructions` (also a documented GA
# field on response.create's `response` object, and -- like tool_choice -- one for THIS
# response only, replacing rather than merging with the session's own instructions for
# its duration) explicitly telling the model nothing was added or changed fixed it in 3
# of 3 runs. `_tool_failure_cap_instructions()` prefers the brand prompt config's
# `tool_failure_cap_instructions` (so each brand can keep its own carhop/crew-member
# voice) and falls back to a neutral built-in default -- deliberately NOT via
# PromptLoader.render_error()'s own generic "Unknown error message key" placeholder,
# which would be a worse, more visibly broken instruction than a plain neutral one -- so
# there is never an empty (or placeholder) `instructions` field.
_TOOL_FAILURE_CAP_INSTRUCTIONS_FALLBACK = (
    "The order system just failed twice in a row and nothing was added or changed. "
    "Do not say an item was added, removed, or changed. Briefly apologise, say you "
    "couldn't update the order just now, and ask the guest to repeat what they'd like."
)


def _tool_failure_cap_instructions(prompt_loader) -> str:
    """Return the response-level `instructions` text for the tool-failure cap notice.

    Reads the brand prompt config's `error_messages.yaml` key
    `tool_failure_cap_instructions` if one is configured; otherwise returns
    `_TOOL_FAILURE_CAP_INSTRUCTIONS_FALLBACK`. Deliberately checks
    `get_error_messages()` directly rather than calling `prompt_loader.render_error()`
    unconditionally -- `render_error()`'s own fallback for an unknown key is a generic
    "An error occurred (...)" placeholder, not this function's neutral default, and a
    placeholder string sent to the model as its ONLY instructions for this response
    would be worse than nothing.
    """
    if prompt_loader is not None and "tool_failure_cap_instructions" in prompt_loader.get_error_messages():
        return prompt_loader.render_error("tool_failure_cap_instructions")
    return _TOOL_FAILURE_CAP_INSTRUCTIONS_FALLBACK


def _build_tool_failure_cap_notice_msg(prompt_loader) -> str:
    """Build the server-authored response.create sent once per capped failure streak.

    `tool_choice: "none"` stops the model from calling a tool again with no guest
    input; `instructions` (see `_tool_failure_cap_instructions()` above) stops it from
    falsely claiming the order changed anyway, on top of not calling a tool.
    """
    return json.dumps({
        "type": "response.create",
        "response": {
            "tool_choice": "none",
            "instructions": _tool_failure_cap_instructions(prompt_loader),
        },
    })


class _ToolFailureTracker:
    """Per-connection consecutive-failed-tool-*round*-counter (#36, PR #58 re-review "S1"/"S2").

    Counts failed tool rounds since the last guest turn, not failed calls or
    tool successes (PR #58 re-review "S1"): the earlier per-call, reset-on-
    any-success version let a model loop `update_order` (fails) -> `get_order`
    (succeeds -- the very call our own error text tells it to make) ->
    `update_order` (fails) -> ... forever without ever reaching the cap, since
    each `get_order` reset the count back to zero with no guest input at all.
    A round with several parallel tool calls where only some fail is also
    counted once, not once per failing call.

    Once the cap is reached, `consume_cap_notice()` grants exactly ONE
    server-authored, tool-free response.create (see
    `_RESPONSE_CREATE_TOOL_CHOICE_NONE_MSG`) so the model can apologise out
    loud -- but every response.done *after* that, while still at cap and
    with no guest turn in between, goes back to sending nothing at all. A
    model (or, in the fake upstream's deterministic scripting, a test) that
    keeps calling tools every round regardless of what `tool_choice` said
    must not be able to ride an unbounded ladder of one-more-apology
    responses with zero guest input -- only a single apology per capped
    streak, exactly like the plain "stop auto-continuing" cap this replaces.
    """
    __slots__ = ("count", "_round_had_failure", "_cap_notice_sent")

    def __init__(self):
        self.count = 0
        self._round_had_failure = False
        self._cap_notice_sent = False

    def record_call_failure(self) -> None:
        """One tool call in the current round raised an unhandled exception.

        Marks the round as failed; does not touch `count` yet -- `end_round()`
        does that once per round, so several parallel failing calls in one
        round (e.g. two tool calls in the same response, both raising) still
        only count as a single failed round.
        """
        self._round_had_failure = True

    def end_round(self) -> None:
        """Call once per response.done that had >=1 tool call pending.

        Increments the streak only if at least one call in this round failed.
        A round with only successful calls (e.g. the `get_order` the model's
        own error text tells it to make after a failure) leaves the streak
        unchanged -- it must NOT reset it back to zero, or the cap could
        never be reached no matter how long the loop runs.
        """
        if self._round_had_failure:
            self.count += 1
            self._round_had_failure = False

    def reset_for_new_turn(self) -> None:
        """Genuine guest activity (speech_started / a completed input
        transcription) breaks the streak -- only the guest, not the model
        retrying tools on its own, gets to start the count over."""
        self.count = 0
        self._round_had_failure = False
        self._cap_notice_sent = False

    def at_cap(self) -> bool:
        return self.count >= _TOOL_FAILURE_CAP

    def consume_cap_notice(self) -> bool:
        """True (and marks the notice sent) the first time this is called
        after the cap is reached; False every time after that, until
        `reset_for_new_turn()` runs. Lets the response.done handler send
        exactly one tool_choice="none" apology per capped streak, then fall
        back to sending nothing at all for as long as the streak continues
        with no guest turn -- see the class docstring.
        """
        if self._cap_notice_sent:
            return False
        self._cap_notice_sent = True
        return True


class ToolResultDirection(Enum):
    TO_SERVER = 1
    TO_CLIENT = 2
    TO_BOTH = 3

class ToolResult:
    __slots__ = ("text", "destination", "_client_text")

    def __init__(self, text: str, destination: ToolResultDirection, client_text: str | None = None):
        self.text = text
        self.destination = destination
        self._client_text = client_text

    def to_text(self) -> str:
        if self.text is None:
            return ""
        return self.text if isinstance(self.text, str) else json.dumps(self.text)

    def to_client_text(self) -> str:
        """Text for client display. Falls back to to_text() if no separate client payload."""
        if self._client_text is not None:
            return self._client_text
        return self.to_text()

class Tool:
    __slots__ = ("target", "schema")

    def __init__(self, target: Any, schema: Any):
        self.target = target
        self.schema = schema

class RTToolCall:
    __slots__ = ("tool_call_id", "previous_id")

    def __init__(self, tool_call_id: str, previous_id: str):
        self.tool_call_id = tool_call_id
        self.previous_id = previous_id


# Session keys the GA realtime API accepts at the top level. Anything else that
# the legacy (2024-10-01-preview) clients send is dropped, because GA rejects
# unknown parameters outright instead of ignoring them.
# `reasoning` ({effort}) and `parallel_tool_calls` exist only for reasoning
# realtime models (gpt-realtime-2 / 2.1). gpt-realtime-1.5 rejects the whole
# session.update if they are present, so RTMiddleTier only sets them when the
# deployment is a reasoning model (see `RTMiddleTier._reasoning_model`).
_GA_SESSION_TOP_LEVEL = frozenset({
    "type", "model", "instructions", "tools", "tool_choice",
    "max_output_tokens", "output_modalities", "audio", "tracing",
    "include", "prompt", "truncation",
    "reasoning", "parallel_tool_calls",
})

# Legacy audio formats were bare strings ("pcm16"); GA expects an object.
_GA_AUDIO_FORMATS = {
    "pcm16": {"type": "audio/pcm", "rate": 24000},
    "g711_ulaw": {"type": "audio/pcmu"},
    "g711_alaw": {"type": "audio/pcma"},
}


def _ga_audio_format(value: Any) -> Any:
    if isinstance(value, str):
        return _GA_AUDIO_FORMATS.get(value, {"type": "audio/pcm", "rate": 24000})
    return value


def _to_ga_session(session: dict) -> dict:
    """Translate a legacy realtime `session` object into the GA shape.

    The browser client speaks the 2024-10-01-preview dialect. The GA endpoint
    moved most audio settings under `audio.input` / `audio.output`, renamed a
    couple of fields, requires a `type` discriminator, and errors on unknown
    parameters rather than ignoring them. Doing the translation here keeps the
    client contract stable and keeps the failure modes in one place.
    """
    ga: dict = dict(session)
    audio: dict = dict(ga.get("audio") or {})
    audio_in: dict = dict(audio.get("input") or {})
    audio_out: dict = dict(audio.get("output") or {})

    # input side
    if (turn_detection := ga.pop("turn_detection", None)) is not None:
        audio_in["turn_detection"] = turn_detection
    if (transcription := ga.pop("input_audio_transcription", None)) is not None:
        audio_in["transcription"] = transcription
    if (in_fmt := ga.pop("input_audio_format", None)) is not None:
        audio_in["format"] = _ga_audio_format(in_fmt)
    if (noise := ga.pop("input_audio_noise_reduction", None)) is not None:
        audio_in["noise_reduction"] = noise

    # output side
    if (voice := ga.pop("voice", None)) is not None:
        audio_out["voice"] = voice
    if (out_fmt := ga.pop("output_audio_format", None)) is not None:
        audio_out["format"] = _ga_audio_format(out_fmt)
    if (speed := ga.pop("speed", None)) is not None:
        audio_out["speed"] = speed

    # renamed top-level fields
    if (max_tokens := ga.pop("max_response_output_tokens", None)) is not None:
        ga["max_output_tokens"] = max_tokens
    if (modalities := ga.pop("modalities", None)) is not None:
        ga["output_modalities"] = modalities

    if audio_in:
        audio["input"] = audio_in
    if audio_out:
        audio["output"] = audio_out
    if audio:
        ga["audio"] = audio

    ga["type"] = "realtime"

    # `temperature` and `disable_audio` are not part of the GA session object.
    dropped = [k for k in ga if k not in _GA_SESSION_TOP_LEVEL]
    for key in dropped:
        ga.pop(key)
    if dropped:
        logger.debug("session.update: dropped non-GA keys %s", dropped)

    return ga


# What the browser's useRealtime.startSession() sends. The middle tier applies
# the same values itself the moment the upstream socket opens, so a socket the
# browser never configures (e.g. react-use-websocket auto-reconnected while the
# mic was live) behaves exactly like one it did.
_BOOTSTRAP_CLIENT_SESSION: dict = {
    "turn_detection": {
        "type": "server_vad",
        "threshold": 0.7,
        "prefix_padding_ms": 300,
        "silence_duration_ms": 500,
    },
    "input_audio_transcription": {"model": "whisper-1"},
}

# How long the greeting waits for the server to confirm the session config.
_SESSION_CONFIGURED_TIMEOUT_SEC = conformance_hooks.seconds("CONFORMANCE_GREETING_TIMEOUT_SECONDS", 5.0)

# Fire-and-forget tasks (e.g. closing a superseded socket) kept alive until done.
_BACKGROUND_TASKS: set[asyncio.Task] = set()


def _spawn(coro) -> asyncio.Task:
    task = asyncio.ensure_future(coro)
    _BACKGROUND_TASKS.add(task)
    task.add_done_callback(_on_background_task_done)
    return task


def _on_background_task_done(task: asyncio.Task) -> None:
    # swigerb/SonicAIDriveThru#59 (PR #58 re-review, "F1" completeness): a
    # done callback that only discards from the tracking set still leaves
    # any exception the task raised unretrieved -- asyncio logs those as
    # "Task exception was never retrieved" at ERROR, the exact noisy-log
    # shape #59 fixed for the echo flush specifically. Retrieving it here
    # (even just to log it at DEBUG and drop it) is what actually silences
    # that for every task spawned via `_spawn`, not just the two echo-flush
    # sends #59 originally covered.
    _BACKGROUND_TASKS.discard(task)
    if task.cancelled():
        return
    exc = task.exception()
    if exc is not None:
        logger.debug("Background task raised (retrieved, not re-raised): %r", exc)


async def _close_superseded(stale_ws: web.WebSocketResponse) -> None:
    try:
        await stale_ws.close(code=SUPERSEDED_CLOSE_CODE, message=SUPERSEDED_CLOSE_REASON.encode())
    except Exception:
        pass


def _extension_type(data: str, marker: str) -> str | None:
    """The `type` of a client frame that contains `marker`, else None (cheap for audio frames)."""
    if marker not in data:
        return None
    try:
        message = json.loads(data)
    except ValueError:
        return None
    return message.get("type") if isinstance(message, dict) else None


def _strip_output_voice(ga_session: dict) -> bool:
    """Remove `audio.output.voice` from a GA session in place. Returns True if removed."""
    audio = ga_session.get("audio")
    if not isinstance(audio, dict):
        return False
    output = audio.get("output")
    if not isinstance(output, dict) or "voice" not in output:
        return False
    output.pop("voice")
    if not output:
        audio.pop("output")
    if not audio:
        ga_session.pop("audio")
    return True


# Values accepted by gpt-realtime-2.1 for `reasoning.effort` (probed live 2026-09-22).
REASONING_EFFORTS = frozenset({"none", "minimal", "low", "medium", "high", "xhigh"})
# Config values that mean "do not send `reasoning` at all".
_REASONING_DISABLED_VALUES = frozenset({"", "off", "disabled", "false", "null"})

# Realtime model families that are NOT reasoning models. gpt-realtime-1.5 answers
# `reasoning` (any effort, even "none") and `parallel_tool_calls: true` with
# `invalid_value` "Unsupported option for this model" -- and drops the whole
# session.update, tools included. The dated `gpt-realtime-2025-08-28` snapshot is
# the original non-reasoning gpt-realtime, not gpt-realtime-2.
_NON_REASONING_DEPLOYMENT_RE = re.compile(
    r"^(gpt-4o.*|gpt-realtime(-mini.*|-1(\.\d+)?(-.*)?|-\d{4}-\d{2}-\d{2})?)$",
    re.IGNORECASE,
)


def deployment_supports_reasoning(deployment: str | None) -> bool:
    """Best-effort check from the deployment name, used only when
    `model.reasoning_model` is "auto". azd names deployments after the model, so
    a rollback to `gpt-realtime-1.5` is recognised. Unrecognised custom names
    are assumed to support reasoning; if they don't, the rejected session.update
    is caught by the fallback in RTMiddleTier and reasoning is switched off for
    the rest of the process."""
    if not isinstance(deployment, str) or not deployment.strip():
        return True
    return _NON_REASONING_DEPLOYMENT_RE.match(deployment.strip()) is None


def parse_reasoning_model(value: Any) -> bool | None:
    """`model.reasoning_model` / AZURE_OPENAI_REALTIME_REASONING_MODEL:
    True / False force it; None ("auto", empty, unknown) infers it from the
    deployment name."""
    if isinstance(value, bool):
        return value
    text = "" if value is None else str(value).strip().lower()
    if text in ("true", "yes", "on", "1"):
        return True
    if text in ("false", "no", "off", "0"):
        return False
    if text not in ("", "auto", "null", "none"):
        logger.warning("Ignoring unknown reasoning_model %r (expected auto|true|false)", value)
    return None


def normalize_reasoning_effort(value: Any) -> str | None:
    """Map a configured effort to the wire value, or None to omit `reasoning`.

    Empty / "off" / "disabled" omit the field. "none" is a real effort level on
    gpt-realtime-2.1 (no reasoning tokens) and is sent as-is.
    """
    if value is None:
        return None
    effort = str(value).strip().lower()
    if effort in _REASONING_DISABLED_VALUES:
        return None
    if effort not in REASONING_EFFORTS:
        logger.warning("Ignoring unknown reasoning effort %r (expected one of %s)", value, sorted(REASONING_EFFORTS))
        return None
    return effort


def _new_event_id(prefix: str) -> str:
    return f"{prefix}_{uuid.uuid4().hex[:20]}"


# The fallback carries only what the conversation cannot work without. No voice
# (cannot_update_voice), no audio config, no reasoning -- the usual suspects when
# GA rejects an update.
_FALLBACK_SESSION_KEYS = ("type", "instructions", "tools", "tool_choice")


class _SessionUpdateGuard:
    """Tracks the session.updates sent on ONE upstream socket so a rejection can
    be correlated back to them.

    GA rejects an invalid session.update wholesale and reports it only as an
    `error` event. Most rejections echo our `event_id` in `error.event_id`, but
    some do not (gpt-realtime-1.5 rejecting `reasoning` returns no event_id and
    no param), so an uncorrelated invalid_request_error that arrives while one
    of our updates is still unacknowledged is attributed to the oldest one --
    the service processes client events in order.
    """

    _MAX_TRACKED = 64

    def __init__(self) -> None:
        # event_id -> event_id of the original if this is a fallback, else None
        self._sent: OrderedDict[str, str | None] = OrderedDict()
        self._payloads: dict[str, dict] = {}
        self._in_flight: deque[str] = deque()
        self._fallback_sent_for: set[str] = set()

    def stamp(self, message: dict, fallback_of: str | None = None) -> dict:
        """Ensure `message` carries an event_id and start tracking it.

        The candidate event_id must be a non-empty string -- it's used as a dict
        key below, so anything else (a browser-forged {"event_id": {...}} or
        [...]) would raise an unhashable-type TypeError and kill the socket
        instead of being handled. Any non-string/empty candidate is discarded
        and a fresh, server-generated id is used instead.
        """
        candidate = message.get("event_id")
        event_id = candidate if isinstance(candidate, str) and candidate else \
            _new_event_id("sonic_fallback" if fallback_of else "sonic_su")
        message["event_id"] = event_id
        self._sent[event_id] = fallback_of
        self._payloads[event_id] = message.get("session") or {}
        self._in_flight.append(event_id)
        while len(self._sent) > self._MAX_TRACKED:
            old, _ = self._sent.popitem(last=False)
            self._payloads.pop(old, None)
        return message

    def track(self, payload: str, fallback_of: str | None = None) -> str:
        """`stamp` for an already-serialised session.update."""
        message = json.loads(payload)
        had_id = bool(message.get("event_id"))
        self.stamp(message, fallback_of)
        return payload if had_id else json.dumps(message)

    def on_session_updated(self) -> None:
        if self._in_flight:
            self._in_flight.popleft()

    def correlate(self, error_event: dict) -> str | None:
        """Return the event_id of our session.update this error rejects, or None."""
        err = error_event.get("error") or {}
        event_id = err.get("event_id")
        if event_id:
            if event_id not in self._sent:
                return None
            try:
                self._in_flight.remove(event_id)
            except ValueError:
                pass
            return event_id
        param = err.get("param") or ""
        # A rate limit is never a session.update rejection; only an echoed
        # event_id (above) ties one to an update.
        if (self._in_flight and err.get("type") == "invalid_request_error" and not is_rate_limit_error(err)
                and (not param or param.startswith("session"))):
            return self._in_flight.popleft()
        return None

    def original_of(self, event_id: str) -> str | None:
        return self._sent.get(event_id)

    def payload_of(self, event_id: str) -> dict:
        return self._payloads.get(event_id, {})

    def claim_fallback(self, event_id: str) -> bool:
        """True exactly once per original session.update."""
        if event_id in self._fallback_sent_for:
            return False
        self._fallback_sent_for.add(event_id)
        return True


class RTMiddleTier:
    endpoint: str
    deployment: str
    key: str | None = None
    
    # Tools are server-side only for now, though the case could be made for client-side tools
    # in addition to server-side tools that are invisible to the client
    tools: dict[str, Tool]

    # Server-enforced configuration, if set, these will override the client's configuration
    # Typically at least the model name and system message will be set by the server
    model: str | None = None
    system_message: str | None = None
    temperature: float | None = None
    max_tokens: int | None = None
    disable_audio: bool | None = None
    voice_choice: str | None = None
    # Server-side allow-list extension.set_voice may pick from (PR #49 review
    # round 5, "M1"); see `_DEFAULT_ALLOWED_VOICES` and `configure_realtime_model`.
    allowed_voices: frozenset[str] = _DEFAULT_ALLOWED_VOICES
    # audio.input.transcription.model. whisper-1 works on Azure without its own
    # deployment; gpt-4o(-mini)-transcribe are ACCEPTED by session.update but
    # then fail every turn with DeploymentNotFound unless deployed separately.
    transcription_model: str | None = None
    # reasoning.effort for reasoning realtime models; None omits the field.
    reasoning_effort: str | None = None
    parallel_tool_calls: bool | None = None
    # Whether the deployment is a reasoning model (accepts `reasoning` and
    # `parallel_tool_calls`). None = infer from the deployment name.
    reasoning_model: bool | None = None
    _reasoning_rejected: bool = False

    def __init__(self, endpoint: str, deployment: str, credentials: AzureKeyCredential | DefaultAzureCredential, voice_choice: str | None = None, prompt_loader=None):
        self.endpoint = endpoint
        self.deployment = deployment
        self.voice_choice = voice_choice
        # #43 fix (PR #49 review round 6, "S1"): self.voice_choice above is the
        # config-level default and is NEVER mutated again after this point.
        # There is deliberately no process-wide "current voice" field here any
        # more -- a validated extension.set_voice pick belongs to the guest's
        # OWN session, not to this RTMiddleTier instance (which is shared by
        # every guest on the worker). It is stored per session_id on
        # `self._sessions` (see `SessionManager.set_voice`/`get_voice`), read
        # back only for that SAME session on resume, and never consulted for
        # any other, brand-new connection -- see `_forward_messages`.
        self.tools = {}
        self._token_provider = None
        self._cached_token: str | None = None
        self._token_refresh_task: asyncio.Task | None = None
        self._prompt_loader = prompt_loader
        self._sessions = SessionManager(prompt_loader=prompt_loader)
        self.app_secret: bytes = b""  # set by app.py at startup
        # Flipped if the deployment rejects `reasoning` at runtime despite the
        # name check, so later sessions stop sending it.
        self._reasoning_rejected = False
        # Rate-limit recovery (config.yaml resilience.rate_limit); the sleep is
        # swappable so tests never really wait.
        self.rate_limit_settings = RateLimitSettings.from_config(_config, os.environ)
        self._rate_limit_sleep = asyncio.sleep
        if voice_choice is not None:
            logger.info("Realtime voice choice set to %s", voice_choice)
        if isinstance(credentials, AzureKeyCredential):
            self.key = credentials.key
        else:
            self._token_provider = get_bearer_token_provider(credentials, "https://cognitiveservices.azure.com/.default")
            self._token_provider() # Warm up during startup so we have a token cached when the first request arrives

    def build_voice_update(self, voice: str, event_id: str | None = None) -> str:
        """Serialise a session.update that switches the assistant voice.

        The voice is expressed in the legacy shape and then translated, so it
        lands at `audio.output.voice` where the GA endpoint expects it. Sending
        the legacy top-level `voice` key instead gets the whole session.update
        rejected and the voice silently never changes.
        """
        ga_session = _to_ga_session({"voice": voice})
        return json.dumps({"type": "session.update", "event_id": event_id or _new_event_id("sonic_voice"),
                           "session": ga_session})

    def _reasoning_model(self) -> bool:
        """Whether reasoning-model-only fields may be sent upstream at all.

        A runtime rejection always wins; then the explicit `reasoning_model`
        switch; the deployment-name check is only the default."""
        if self._reasoning_rejected:
            return False
        if self.reasoning_model is not None:
            return self.reasoning_model
        return deployment_supports_reasoning(getattr(self, "deployment", None))

    def reasoning_enabled(self) -> bool:
        """Whether `reasoning` will be sent upstream."""
        return normalize_reasoning_effort(self.reasoning_effort) is not None and self._reasoning_model()

    def _build_session(self, session: dict, voice_locked: bool = False, voice: str | None = _VOICE_UNSET) -> dict:
        """Overlay the server-owned configuration onto a legacy-shaped session
        and translate it to the GA shape.

        `voice_locked` must be True once the upstream conversation contains
        assistant audio. From then on GA rejects any session.update whose voice
        differs from the current one with `cannot_update_voice` -- and it
        rejects the WHOLE event, so tools, tool_choice and instructions are
        silently lost along with the voice.

        `voice`: the voice to apply, or omit for `self.voice_choice` (the
        config-level default) -- see `_VOICE_UNSET`. `_forward_messages`
        always passes this connection's own frozen voice explicitly (#43).
        """
        if self.system_message is not None:
            session["instructions"] = self.system_message
        if self.temperature is not None:
            session["temperature"] = self.temperature
        if self.max_tokens is not None:
            session["max_response_output_tokens"] = self.max_tokens
        if self.disable_audio is not None:
            session["disable_audio"] = self.disable_audio
        effective_voice = self.voice_choice if voice is _VOICE_UNSET else voice
        if effective_voice is not None:
            session["voice"] = effective_voice
        session["tool_choice"] = "auto" if len(self.tools) > 0 else "none"
        session["tools"] = [tool.schema for tool in self.tools.values()]
        # Server-owned (PR #49 review round 3, sub-key hardening): the model
        # always comes from RTMiddleTier's own configuration, never merged
        # with whatever the browser sent. Previously this only overwrote the
        # `model` sub-key while merging in the rest of the browser's dict, so
        # a browser could still smuggle other sub-keys (e.g. a Whisper
        # `prompt`) through unfiltered; and if `self.transcription_model` was
        # falsy, the browser's `input_audio_transcription` (model included)
        # was forwarded completely unchanged -- a fail-open gap.
        if self.transcription_model:
            session["input_audio_transcription"] = {"model": self.transcription_model}
        else:
            session.pop("input_audio_transcription", None)
        # Server-owned: never trust a client-supplied value for these, since
        # an unsupported one takes the tools down with it.
        session.pop("reasoning", None)
        session.pop("parallel_tool_calls", None)
        if self._reasoning_model():
            if (effort := normalize_reasoning_effort(self.reasoning_effort)) is not None:
                session["reasoning"] = {"effort": effort}
            if self.parallel_tool_calls is not None:
                session["parallel_tool_calls"] = bool(self.parallel_tool_calls)
        # Clients speak the legacy (2024-10-01-preview) session shape.
        # Translate to the GA shape here so the browser contract is
        # unchanged, and so unsupported legacy keys are dropped rather
        # than rejected outright by the server.
        ga_session = _to_ga_session(session)
        if voice_locked and _strip_output_voice(ga_session):
            logger.info("session.update: assistant audio already present — omitting voice so the update is not rejected")
        return ga_session

    def build_bootstrap_session_update(self, event_id: str | None = None, voice: str | None = _VOICE_UNSET) -> str:
        """Serialise the session.update the middle tier sends as the very first
        frame on every upstream socket, before any browser traffic is relayed.

        Without it the upstream session runs on the service defaults (no tools,
        generic instructions, server VAD auto-responding) until the browser's
        own session.update arrives -- and if the model speaks in that window the
        voice locks and every later session.update carrying our voice is
        rejected, so tools are never registered for that conversation.
        """
        session = self._build_session(copy.deepcopy(_BOOTSTRAP_CLIENT_SESSION), voice=voice)
        return json.dumps({"type": "session.update", "event_id": event_id or _new_event_id("sonic_bootstrap"),
                           "session": session})

    def build_fallback_session_update(self, event_id: str | None = None, voice: str | None = _VOICE_UNSET) -> str:
        """Serialise the minimal session.update sent when GA rejects one of ours.

        Only `type`, `instructions`, `tools` and `tool_choice` -- whatever field
        got the original rejected, the carhop keeps its tools and persona.
        """
        full = self._build_session({}, voice_locked=True, voice=voice)
        session = {key: full[key] for key in _FALLBACK_SESSION_KEYS if key in full}
        return json.dumps({"type": "session.update", "event_id": event_id or _new_event_id("sonic_fallback"),
                           "session": session})

    async def _recover_rejected_session_update(self, message: dict, server_ws, guard: "_SessionUpdateGuard | None",
                                               session_id: str | None, voice: str | None = _VOICE_UNSET) -> bool:
        """Handle an upstream `error` that rejects one of our session.updates.

        Returns True if the error was consumed (a fallback was sent), False if
        it should reach the browser: unrelated errors, and a rejected fallback.
        """
        if guard is None:
            return False
        event_id = guard.correlate(message)
        if event_id is None:
            return False
        err = message.get("error") or {}
        code, param, text = err.get("code"), err.get("param"), err.get("message")
        original = guard.original_of(event_id)
        if original is not None or not guard.claim_fallback(event_id):
            logger.error(
                "Fallback session.update %s (for %s) was ALSO rejected: code=%s param=%s message=%s -- "
                "tools may NOT be registered for this conversation (session=%s)",
                event_id, original, code, param, text, session_id)
            return False
        logger.error(
            "Upstream REJECTED session.update %s: code=%s param=%s message=%s -- resending a minimal "
            "session.update (instructions + tools only) so the tools survive (session=%s)",
            event_id, code, param, text, session_id)
        rejected = guard.payload_of(event_id)
        if (("reasoning" in rejected or "parallel_tool_calls" in rejected)
                and (not param or param.startswith(("session.reasoning", "session.parallel_tool_calls")))):
            self._reasoning_rejected = True
            logger.error("Deployment %s rejected reasoning-model options; no longer sending `reasoning` / "
                         "`parallel_tool_calls` from this process. Set model.reasoning_effort to \"\" for this "
                         "deployment.", getattr(self, "deployment", "?"))
        fallback = guard.track(self.build_fallback_session_update(voice=voice), fallback_of=event_id)
        await server_ws.send_str(fallback)
        return True

    def _get_auth_token(self) -> str:
        """Return the cached token, falling back to a synchronous call if needed."""
        if self._cached_token is not None:
            return self._cached_token
        if self._token_provider is not None:
            return self._token_provider()
        return ""

    async def _refresh_token_loop(self) -> None:
        """Background task: proactively refresh the Azure AD token every 5 minutes."""
        while True:
            try:
                loop = asyncio.get_event_loop()
                token = await loop.run_in_executor(None, self._token_provider)
                self._cached_token = token
                logger.debug("Azure AD token refreshed successfully")
            except Exception as e:
                logger.warning("Token refresh failed: %s", e)
            await asyncio.sleep(300)  # 5 minutes

    def start_background_tasks(self) -> None:
        """Start background tasks (token refresh, idle checker). Called once at app startup."""
        if self._token_provider is not None:
            self._token_refresh_task = asyncio.ensure_future(self._refresh_token_loop())
        self._sessions.start_idle_checker()

    def stop_background_tasks(self) -> None:
        """Cancel background tasks. Called on app shutdown."""
        if self._token_refresh_task and not self._token_refresh_task.done():
            self._token_refresh_task.cancel()
        self._sessions.stop_idle_checker()

    def _client_session_echo(self, message: dict, voice: str | None = _VOICE_UNSET) -> dict:
        """Build the minimal allow-listed browser-bound copy of a GA
        `session.created`/`session.updated` echo (swigerb/SonicAIDriveThru#45).

        Replaces the previous `_scrub_session_for_client`, a deny-list scrub
        that mutated the *full* upstream session object in place and had to
        be updated every time GA added a new top-level key. GA has since
        shipped `prompt`, `tracing`, `include` and `truncation` -- none were
        ever added to that deny-list, so each would have been relayed to the
        browser completely unscrubbed. An allow-list can't leak a key nobody
        has thought to deny yet: this builds a brand-new dict containing
        exactly the shape below and nothing else, no matter what upstream
        adds next.

        `useRealtime.tsx` has no handler at all for either event type
        (verified: neither `session.created` nor `session.updated` appears in
        its message-type switch), so this shape is deliberately minimal
        rather than driven by any actual frontend field read -- `voice` is
        kept only because a hypothetical future handler might reasonably
        want to know the active voice.

        Every event that echoes the full session object -- currently
        `session.created` (on connect) and `session.updated` (after every
        accepted session.update: our own bootstrap one, the voice picker, the
        browser's own handshake, a rejection fallback...) -- must route
        through this one helper so both are reduced identically.

        Caller must only invoke this when `message["session"]` is present:
        `session.updated` is not guaranteed to carry one, and a malformed/
        unexpected frame missing it is passed through unchanged instead (see
        the `session.updated` case below) rather than echoed as this shape
        with every field null.

        `voice`: the voice this echo reports, or omit for `self.voice_choice`
        -- see `_VOICE_UNSET`. #43: `_forward_messages` always passes this
        connection's own frozen voice so a pick made on ANOTHER, already-open
        guest's connection never appears in this one's echo.
        """
        session = message["session"]
        effective_voice = self.voice_choice if voice is _VOICE_UNSET else voice
        return {
            "type": message.get("type"),
            "event_id": message.get("event_id"),
            "session": {
                "id": session.get("id"),
                "object": session.get("object"),
                "audio": {"output": {"voice": effective_voice}},
            },
        }

    async def _process_message_to_client(self, msg: str, client_ws: web.WebSocketResponse, server_ws: web.WebSocketResponse, tools_pending: dict[str, RTToolCall], verbose: bool = False, guard: "_SessionUpdateGuard | None" = None, on_session_created: Callable[[], Awaitable[None]] | None = None, recovery: RateLimitRecovery | None = None, voice: str | None = _VOICE_UNSET, tool_failures: "_ToolFailureTracker | None" = None) -> str | None:
        data = msg.data

        # FAST PATH: extract type via regex without full JSON parse.
        # Audio deltas are ~95% of server messages — avoid json.loads entirely.
        m = _TYPE_RE.search(data)
        if m is not None and m.group(1) in _PASSTHROUGH_SERVER_TYPES:
            # Translate GA event names to legacy names for client compatibility
            event_type = m.group(1)
            legacy_name = _GA_TO_LEGACY_EVENTS.get(event_type)
            if legacy_name is not None:
                data = data.replace(f'"{event_type}"', f'"{legacy_name}"', 1)
                event_type = legacy_name
            # Verbose: log passthrough types (skip audio delta data to avoid flooding)
            if verbose or _VERBOSE_GLOBAL:
                if event_type == "response.audio.delta":
                    _vlog(verbose, "─── [Server → Client] response.audio.delta (audio data) ───")
                elif event_type == "response.audio_transcript.delta":
                    # Extract transcript snippet from raw JSON
                    td_match = re.search(r'"delta"\s*:\s*"([^"]{0,120})', data)
                    snippet = td_match.group(1) if td_match else ""
                    _vlog(verbose, '─── [AI → Client] response.audio_transcript.delta ───\n"%s"', snippet)
                elif event_type == "response.audio_transcript.done":
                    td_match = re.search(r'"transcript"\s*:\s*"([^"]{0,200})', data)
                    snippet = td_match.group(1) if td_match else ""
                    _vlog(verbose, '─── [AI → Client] response.audio_transcript.done ───\n"%s"', snippet)
                elif event_type == "input_audio_buffer.speech_started":
                    _vlog(verbose, "─── [Server] input_audio_buffer.speech_started ───")
                elif event_type == "input_audio_buffer.speech_stopped":
                    _vlog(verbose, "─── [Server] input_audio_buffer.speech_stopped ───")
                else:
                    _vlog(verbose, "─── [Server → Client] %s ───", event_type)
            return data

        message = json.loads(data)
        msg_type = message.get("type", "")

        updated_message = data
        session_id = self._sessions.get_session_id(client_ws)
        if message is not None:
            _vlog(verbose, "─── [Server → Client] %s ───", msg_type)
            match msg_type:
                case "error":
                    # A rejected session.update of ours is recovered here (minimal
                    # fallback) instead of surfacing as a user-facing failure.
                    if await self._recover_rejected_session_update(message, server_ws, guard, session_id, voice=voice):
                        _vlog(verbose, "  ⚠ session.update rejected — fallback sent: %s", json.dumps(message, default=str)[:500])
                        return None
                    # A rate-limited response is retried (see rate_limit.py), not surfaced.
                    if recovery is not None and await recovery.on_error(message):
                        _vlog(verbose, "  ⚠ rate-limited — recovery ladder: %s", json.dumps(message, default=str)[:500])
                        return None
                    # Surface OpenAI errors (e.g. rejected session.update, malformed tool schemas)
                    # so they don't silently vanish into the client.
                    logger.error("OpenAI Realtime API error: %s", json.dumps(message, default=str)[:1000])
                    _vlog(verbose, "  ⚠ ERROR: %s", json.dumps(message, default=str)[:500])

                case "conversation.item.input_audio_transcription.failed":
                    # e.g. DeploymentNotFound when the configured transcription
                    # model has no Azure deployment: the session.update was
                    # accepted, but no guest speech is ever transcribed.
                    logger.error("Input audio transcription failed (model=%s): %s", self.transcription_model,
                                 json.dumps(message.get("error"), default=str)[:500])

                case "session.created":
                    session = message["session"]
                    _vlog(verbose, "  Session ID: %s", session.get("id", "?"))
                    updated_message = json.dumps(self._client_session_echo(message, voice=voice))
                    if on_session_created is not None:
                        # The forwarder announces the session (metadata or resume)
                        # once it knows whether this socket is resuming.
                        await on_session_created()
                    elif session_id is not None:
                        identifiers = order_state_singleton.get_session_identifiers(session_id)
                        await self._sessions.emit_session_identifiers(client_ws, "extension.session_metadata", identifiers)
                        _vlog(verbose, "─── [SESSION TOKEN] ───\n"
                                       "Token: %s\n"
                                       "Round Trip: #%d (token: %s)\n"
                                       "───────────────────────",
                              identifiers.session_token,
                              identifiers.round_trip_index,
                              identifiers.round_trip_token)

                case "session.updated":
                    # Same leak surface as session.created: this event fires
                    # after every accepted session.update (ours or the
                    # browser's) and echoes the full session object right
                    # back -- instructions/tools/max-tokens included.
                    if message.get("session") is not None:
                        updated_message = json.dumps(self._client_session_echo(message, voice=voice))

                case "response.created":
                    if recovery is not None:
                        recovery.on_response_created()

                case "response.output_item.added":
                    if "item" in message and message["item"]["type"] == "function_call":
                        # Fallback registration — ensures tools_pending is populated even
                        # if conversation.item.created fires late or is skipped by newer
                        # API versions.  conversation.item.created overwrites with the
                        # correct previous_item_id when it arrives.
                        item = message["item"]
                        call_id = item.get("call_id")
                        if call_id and call_id not in tools_pending:
                            logger.info("Tool call received: name=%s, call_id=%s", item.get("name"), call_id)
                            tools_pending[call_id] = RTToolCall(call_id, "")
                        _vlog(verbose, "  Tool call registered: %s (call_id=%s)", item.get("name"), item.get("call_id"))
                        updated_message = None

                case "conversation.item.created" | "conversation.item.added":
                    if "item" in message and message["item"]["type"] == "function_call":
                        item = message["item"]
                        # Always overwrite — may upgrade fallback from output_item.added
                        # with the correct previous_item_id
                        tools_pending[item["call_id"]] = RTToolCall(item["call_id"], message.get("previous_item_id", ""))
                        _vlog(verbose, "  Tool pending confirmed: call_id=%s, prev=%s", item["call_id"], message.get("previous_item_id", ""))
                        updated_message = None
                    elif "item" in message and _drop_from_client(message["item"]):
                        # Covers function_call_output (tool result) and any
                        # middle-tier-authored item (rehydration/nudge/tool
                        # echo, by id prefix; role="system" as a backstop) --
                        # see _drop_from_client.
                        _vlog(verbose, "  Server-authored/tool item suppressed from client (id=%s)", message["item"].get("id", "?"))
                        updated_message = None
                    elif "item" in message and message["item"].get("role") == "assistant":
                        # Log AI conversation items (non-tool)
                        _vlog(verbose, "  AI conversation item created")

                case "conversation.item.done" | "conversation.item.retrieved":
                    # GA emits `.done` "when the item is finalized" (and
                    # `.retrieved` in reply to a conversation.item.retrieve)
                    # with the *full* item -- the same leak surface as
                    # `.created`/`.added` above, so the same filter must run
                    # here too (swigerb/SonicAIDriveThru#29 follow-up, PR #30
                    # review "M1"). Unlike `.created`/`.added`, function_call
                    # items don't need tools_pending registered again here --
                    # that already happened on the earlier `.created`/`.added`
                    # (or the response.output_item.added fallback).
                    if "item" in message and _drop_from_client(message["item"]):
                        _vlog(verbose, "  Server-authored/tool item suppressed from client (%s, id=%s)",
                              msg_type, message["item"].get("id", "?"))
                        updated_message = None

                case "response.function_call_arguments.delta":
                    updated_message = None
                
                case "response.function_call_arguments.done":
                    _vlog(verbose, "  Tool args complete: %s", message.get("arguments", "")[:200])
                    updated_message = None

                case "response.output_item.done":
                    if "item" in message and message["item"]["type"] == "function_call":
                        item = message["item"]
                        tool_call = tools_pending.get(item["call_id"])
                        if tool_call is None:
                            logger.warning("Tool call %s not found in pending tools", item["call_id"])
                            updated_message = None
                        else:
                            tool = self.tools.get(item["name"])
                            if tool is None:
                                logger.error("Unknown tool requested: %s", item["name"])
                                updated_message = None
                            else:
                                try:
                                    args = json.loads(item["arguments"])
                                    logger.info("Executing tool '%s' (session=%s)", item["name"], session_id)
                                    t0 = time.monotonic()
                                    if item["name"] in ("update_order", "get_order", "reset_order"):
                                        result = await tool.target(args, session_id)
                                    else:
                                        result = await tool.target(args)
                                    elapsed_ms = (time.monotonic() - t0) * 1000
                                    logger.info("Tool '%s' result direction=%s", item["name"], result.destination)

                                    # ── Verbose: full tool call lifecycle ──
                                    result_text = result.to_text()[:_VERBOSE_RESULT_TRUNCATE]
                                    _vlog(verbose,
                                          "\n═══ [TOOL CALL] %s ═══\n"
                                          "Args: %s\n"
                                          "Result: %s\n"
                                          "Direction: %s\n"
                                          "Time: %.1fms\n"
                                          "═══════════════════════════",
                                          item["name"],
                                          json.dumps(args, indent=2),
                                          result_text,
                                          result.destination.name,
                                          elapsed_ms)

                                    # Track tool call args + result in context window
                                    ctx_monitor = self._sessions.get_context_monitor(session_id)
                                    if ctx_monitor:
                                        ctx_monitor.add_content(item.get("arguments", ""))
                                        ctx_monitor.add_content(result.to_text())

                                    output_text = result.to_text() if result.destination in (ToolResultDirection.TO_SERVER, ToolResultDirection.TO_BOTH) else ""
                                    send_to_client = result.destination in (ToolResultDirection.TO_CLIENT, ToolResultDirection.TO_BOTH)
                                    client_text = result.to_client_text() if send_to_client else None
                                    # #36 S1 (PR #58 re-review): a successful tool call no
                                    # longer resets the failure streak here -- see
                                    # _ToolFailureTracker's docstring for why (the model's own
                                    # prescribed get_order retry must not be what un-caps it).
                                except Exception:
                                    # #36: a genuinely unhandled exception inside a tool handler
                                    # (e.g. a malformed call missing a required argument) used to
                                    # propagate all the way up through _forward_messages's
                                    # connection-wide catch-all, tearing down the guest's whole
                                    # WebSocket instead of giving the model a graceful, recoverable
                                    # error. Log server-side only (tool name + session id) and
                                    # hand the model a neutral function_call_output so the
                                    # conversation, and the guest's session, survive.
                                    logger.exception("Tool '%s' raised an unhandled exception (session=%s)",
                                                      item["name"], session_id)
                                    output_text = self._prompt_loader.render_error("tool_execution_failed") if self._prompt_loader else (
                                        "Something went wrong with that action and it did not complete. "
                                        "Don't retry it yet -- call get_order to confirm the order's current "
                                        "state, then ask the guest to repeat what they'd like."
                                    )
                                    send_to_client = False
                                    client_text = None
                                    # #36 S2 (PR #58 re-review "F3"): refresh the guest-visible
                                    # order ticket from order_state_singleton's CACHED order
                                    # summary (get_order_summary_json returns order_summary_json,
                                    # refreshed by _update_summary() at the end of the *previous*
                                    # successful mutation) -- not from the failed tool's own
                                    # result. This is still strictly better than doing nothing:
                                    # it reflects every mutation that completed successfully
                                    # before this call, so a guest speaking again after an earlier
                                    # order change isn't shown a stale pre-that-change ticket. It
                                    # is NOT a live re-read of order_state -- if this call's own
                                    # exception landed after some in-place mutation but before its
                                    # own _update_summary() ran, that partial change won't be in
                                    # the cache either. Best-effort: if the order state isn't
                                    # readable for this session at all, skip it -- the guest still
                                    # gets the function_call_output below regardless.
                                    if session_id is not None:
                                        try:
                                            ticket_json = order_state_singleton.get_order_summary_json(session_id)
                                        except Exception:
                                            logger.warning(
                                                "Could not read order state to refresh the ticket after a tool "
                                                "failure (session=%s)", session_id,
                                            )
                                        else:
                                            await client_ws.send_json({
                                                "type": "extension.middle_tier_tool_response",
                                                "previous_item_id": tool_call.previous_id,
                                                "tool_name": "get_order",
                                                "tool_result": ticket_json,
                                            })
                                    if tool_failures is not None:
                                        tool_failures.record_call_failure()


                                await server_ws.send_json({
                                    "type": "conversation.item.create",
                                    "item": {
                                        "id": new_middle_tier_item_id(),
                                        "type": "function_call_output",
                                        "call_id": item["call_id"],
                                        "output": output_text
                                    }
                                })
                                if send_to_client:
                                    await client_ws.send_json({
                                        "type": "extension.middle_tier_tool_response",
                                        "previous_item_id": tool_call.previous_id,
                                        "tool_name": item["name"],
                                        "tool_result": client_text
                                    })
                                updated_message = None


                case "response.done":
                    if recovery is not None and await recovery.on_response_done(message):
                        # Rate-limited with no output; dropped here, the ladder retries.
                        # A failed tool follow-up is retried with a bare response.create:
                        # its function_call_output is already in the conversation, so the
                        # tool is never re-run.
                        _vlog(verbose, "  ⚠ response rate-limited — recovery ladder")
                        return None
                    if tools_pending:
                        tools_pending.clear()
                        if tool_failures is not None:
                            # #36 S1 (PR #58 re-review): tally this round's outcome once,
                            # here -- not per call -- so several parallel failing tool calls
                            # in the same response only count as a single failed round.
                            tool_failures.end_round()
                        if tool_failures is not None and tool_failures.at_cap():
                            # #36 S1/S2: two (or more) consecutive *failed rounds* on this
                            # connection with no guest turn in between. The FIRST
                            # response.done that reaches the cap gets one server-authored,
                            # tool-free response.create instead of nothing: the model
                            # already has the function_call_output(s) in context, so it can
                            # apologise out loud and ask the guest what to do, but
                            # tool_choice="none" stops it from calling a tool again on this
                            # turn. This frame is server-authored (never derived from
                            # browser input), so the #31 browser->upstream allow-list in
                            # _filter_client_to_server is unaffected. Every response.done
                            # AFTER that one, while still at the cap with no guest turn in
                            # between, goes back to sending nothing at all -- otherwise a
                            # model (or a deterministic test double) that keeps calling
                            # tools regardless of tool_choice could ride an unbounded
                            # ladder of one-more-apology responses with zero guest input.
                            if tool_failures.consume_cap_notice():
                                logger.warning(
                                    "Capping auto response.create with tool_choice=none "
                                    "after %d consecutive failed tool round(s) (session=%s)",
                                    tool_failures.count, session_id,
                                )
                                await server_ws.send_str(_build_tool_failure_cap_notice_msg(self._prompt_loader))
                            else:
                                logger.warning(
                                    "Suppressing auto response.create -- still at the "
                                    "%d-round cap with no guest turn since the apology "
                                    "(session=%s)", tool_failures.count, session_id,
                                )
                        else:
                            await server_ws.send_str(_RESPONSE_CREATE_MSG)
                    is_tool_call_response = False
                    if "response" in message:
                        output = message["response"]["output"]
                        fn_calls = [o for o in output if o.get("type") == "function_call"]
                        if fn_calls:
                            is_tool_call_response = True
                            logger.info("Response contained %d tool call(s): %s",
                                        len(fn_calls), [o.get("name", "?") for o in fn_calls])
                        else:
                            out_types = [o.get("type", "?") for o in output]
                            logger.info("Response completed with NO tool calls (output types: %s)", out_types)
                        _vlog(verbose, "  Response done — output types: %s",
                              [o.get("type", "?") for o in output])
                        # swigerb/SonicAIDriveThru#32: the browser gets the tool
                        # result via extension.middle_tier_tool_response instead
                        # (see response.output_item.done) -- it must never see
                        # the model's raw function_call (tool name + JSON
                        # arguments) or a function_call_output (tool result)
                        # embedded in response.done's output array. GA never
                        # actually places a function_call_output here (it's a
                        # client-authored item, not model output), but the
                        # second type is scrubbed anyway for defense in depth
                        # and to mirror _drop_from_client's identical check on
                        # the conversation-item side.
                        filtered = [o for o in output if o.get("type") not in ("function_call", "function_call_output")]
                        if len(filtered) != len(output):
                            message["response"]["output"] = filtered
                            updated_message = json.dumps(message)
                    if session_id is not None and not is_tool_call_response:
                        identifiers = order_state_singleton.advance_round_trip(session_id)
                        await self._sessions.emit_session_identifiers(client_ws, "extension.round_trip_token", identifiers)
                        _vlog(verbose, "─── [ROUND TRIP] #%d ───\n"
                                       "Token: %s\n"
                                       "────────────────────────",
                              identifiers.round_trip_index,
                              identifiers.round_trip_token)
                    # Track context usage from response output
                    ctx_monitor = self._sessions.get_context_monitor(session_id)
                    if ctx_monitor and "response" in message:
                        for out_item in message["response"].get("output", []):
                            for content in out_item.get("content", []):
                                ctx_monitor.add_content(content.get("text", ""))
                                ctx_monitor.add_content(content.get("transcript", ""))
                    # Remember what the carhop said, for rehydrating a resumed session.
                    if session_id is not None and "response" in message:
                        spoken = " ".join(
                            (content.get("transcript") or content.get("text") or "").strip()
                            for out_item in message["response"].get("output", [])
                            if out_item.get("type") == "message"
                            for content in out_item.get("content", []))
                        self._sessions.record_turn(session_id, "carhop", spoken)

        return updated_message

    async def _process_message_to_server(self, msg: str, ws: web.WebSocketResponse, verbose: bool = False, voice_locked: bool = False, guard: "_SessionUpdateGuard | None" = None, voice: str | None = _VOICE_UNSET, limiter: "_ClientFrameDropWarningLimiter | None" = None) -> "tuple[str | None, str | None]":
        """Validate and forward one browser→upstream frame, or drop it.

        Returns `(forwarded, sent_type)`: `forwarded` is the exact string to
        forward, or `None` if the frame must be dropped -- never raises, and
        never closes the caller's socket (PR #49 review round 2, "S2": every
        malformed-frame shape below used to raise an uncaught exception out
        of this coroutine, which propagated up through `_forward_messages`
        and tore down that guest's own session). `sent_type` is the
        already-validated type of that same frame (or `None` iff `forwarded`
        is `None`) -- callers key their own side effects (idle reset, nudge
        cancel, greeting trigger, barge-in) on it directly instead of
        re-`json.loads`-ing `forwarded` themselves (PR #49 review round 5,
        "F4"). Callers must not assume `forwarded` `is` (identical object to)
        `msg.data` -- see "M2" below.

        `limiter`, if given, rate-limits this call's own per-frame drop/strip
        WARNING logs (PR #58 review round 2, "F2") -- see
        `_ClientFrameDropWarningLimiter`.
        """
        data = msg.data

        # FAST PATH (PR #49 review round 2, "M1"): skip JSON parsing entirely
        # for the one exact frame shape useRealtime.tsx's addUserAudio() sends
        # (~10 frames/sec). Anything that doesn't match this exactly --
        # including a malformed/spoofed frame merely *resembling* an append --
        # falls through to the slow path, which parses, validates and rebuilds
        # it from scratch.
        if _CLIENT_APPEND_FAST_PATH_RE.fullmatch(data):
            return data, "input_audio_buffer.append"

        session_id = self._sessions.get_session_id(ws)

        # S2: never let a malformed frame raise out of this coroutine. Drop
        # with a WARNING and keep the socket open -- an unexpected frame on an
        # otherwise-legitimate session is not itself proof of compromise.
        try:
            message = json.loads(data)
        except (json.JSONDecodeError, ValueError):
            _warn_dropped_frame(limiter, "Dropped unparseable client→server frame (session=%s)", session_id)
            return None, None
        if not isinstance(message, dict):
            _warn_dropped_frame(
                limiter, "Dropped non-object client→server frame of type %s (session=%s)",
                type(message).__name__, session_id)
            return None, None
        if not isinstance(message.get("type"), str):
            # Also covers `{"type": ["x"]}`: an unhashable `type` would raise
            # TypeError on _filter_client_to_server's frozenset membership
            # test below if it weren't caught here first.
            _warn_dropped_frame(
                limiter, "Dropped client→server frame with a non-string/missing type (session=%s)", session_id)
            return None, None

        filtered = _filter_client_to_server(message, session_id=session_id, limiter=limiter)
        if filtered is None:
            return None, None
        msg_type = filtered["type"]
        # M2: always rebuilt from the allow-listed dict -- never the browser's
        # original bytes/object -- so no extra top-level key can survive.
        updated_message = _dump_client_to_server(filtered, session_id, limiter=limiter)
        if updated_message is None:
            return None, None
        _vlog(verbose, "─── [Client → Server] %s ───", msg_type)

        if msg_type == "session.update":
            client_session = filtered.get("session")
            if not isinstance(client_session, dict):
                _warn_dropped_frame(
                    limiter, "Dropped session.update with a missing/invalid session object (session=%s)", session_id)
                return None, None
            # M3: keep only the session keys the frontend actually sends;
            # everything server-owned is applied fresh by _build_session below.
            session_in = {k: v for k, v in client_session.items() if k in _CLIENT_SESSION_KEYS}
            if "turn_detection" in client_session:
                sanitized_td = _sanitize_turn_detection(client_session["turn_detection"], session_id=session_id, limiter=limiter)
                if sanitized_td is None:
                    _warn_dropped_frame(
                        limiter,
                        "Rejected browser turn_detection (missing/invalid type=server_vad) — falling back "
                        "to the server's own default (session=%s)", session_id)
                    sanitized_td = copy.deepcopy(_BOOTSTRAP_CLIENT_SESSION["turn_detection"])
                session_in["turn_detection"] = sanitized_td
            session = self._build_session(session_in, voice_locked=voice_locked, voice=voice)
            tool_names = [t.get("name", "?") for t in session["tools"]]
            filtered["session"] = session
            # Every session.update carries an event_id so a rejection can
            # be correlated and recovered (see _recover_rejected_session_update).
            if guard is not None:
                guard.stamp(filtered)
            else:
                filtered.setdefault("event_id", _new_event_id("sonic_su"))
            logger.info(
                "session.update: injected %d tools %s, tool_choice=%s, max_tokens=%s, reasoning=%s",
                len(session["tools"]), tool_names, session["tool_choice"],
                session.get("max_output_tokens"), session.get("reasoning"),
            )
            _vlog(verbose, "  Injected %d tools: %s, tool_choice=%s",
                  len(session["tools"]), tool_names, session["tool_choice"])
            updated_message = _dump_client_to_server(filtered, session_id)
            if updated_message is None:
                return None, None
            # Track system message + tool schemas in context window
            ctx_monitor = self._sessions.get_context_monitor(session_id)
            if ctx_monitor:
                ctx_monitor.add_content(session.get("instructions", ""))
                for tool_schema in session.get("tools", []):
                    ctx_monitor.add_content(json.dumps(tool_schema))

        return updated_message, msg_type

    async def _forward_messages(self, ws: web.WebSocketResponse):
        # Per-connection tool tracking — prevents cross-connection interference
        tools_pending: dict[str, RTToolCall] = {}
        # #36 S2: per-connection consecutive-tool-failure counter.
        tool_failures = _ToolFailureTracker()

        # Per-connection verbose logging toggle (set by frontend extension message)
        verbose = _VERBOSE_GLOBAL
        audio_frame_count = 0  # Counter for verbose audio frame logging
        # Per-connection file handler for verbose log-to-file (set by frontend or env var)
        session_file_handler: logging.FileHandler | None = None

        # Echo suppression — delegates to EchoSuppressor
        echo = EchoSuppressor()

        async with aiohttp.ClientSession(
            base_url=self.endpoint,
            timeout=_WS_CONNECT_TIMEOUT,
        ) as session:
            params = {"model": self.deployment}
            headers = {}
            if "x-ms-client-request-id" in ws.headers:
                headers["x-ms-client-request-id"] = ws.headers["x-ms-client-request-id"]
            if self.key is not None:
                headers = { "api-key": self.key }
            else:
                headers = { "Authorization": f"Bearer {self._get_auth_token()}" }
            async with session.ws_connect(
                "/openai/v1/realtime",
                headers=headers,
                params=params,
                heartbeat=_WS_HEARTBEAT_SEC,
                compress=0,  # Azure OpenAI declines deflate anyway; don't offer it.
            ) as target_ws:
                loop = asyncio.get_running_loop()
                session_id = self._sessions.get_session_id(ws)
                greeting_sent = self._sessions.has_sent_greeting(session_id) if session_id else False
                # Set once the model has produced audio on this upstream socket;
                # from then on GA refuses voice changes (see _build_session).
                assistant_audio_seen = False
                session_configured = asyncio.Event()
                guard = _SessionUpdateGuard()
                # F2: one rate-limiter per connection, shared across the
                # client→server validation path and the extension-message
                # handlers below (both log per-frame WARNINGs a probing/
                # misbehaving client could otherwise flood).
                drop_limiter = _ClientFrameDropWarningLimiter()
                recovery = RateLimitRecovery(self.rate_limit_settings, target_ws.send_str, ws.send_json,
                                             sleep=self._rate_limit_sleep, session_id=session_id)

                # ── Resume handshake state (one decision per socket) ──
                # A resume is honoured only as the first client frame. Until that
                # decision is made (first frame, or first_frame_timeout), the
                # session announcement is held back so a socket gets exactly one
                # of extension.session_metadata / extension.session_resumed.
                first_frame_pending = True
                resume_decided = asyncio.Event()
                upstream_created = False
                announced = False
                # Silent-guest nudge after a resume (once per resume).
                nudge_task: asyncio.Task | None = None

                # #43 fix (PR #49 review round 6, "S1"): THIS connection's own
                # voice is a purely local variable, initialised ONLY from the
                # config default. It is never seeded from any other guest's
                # pick (there is no shared "last picked voice" field any
                # more) -- so a brand-new connection always bootstraps with
                # the server default, never another guest's, or even this
                # same guest's PREVIOUS (non-resumed) session's, choice. A
                # resume is the one exception: once `handle_resume` confirms
                # WHICH prior session this socket is continuing, it looks up
                # that session's own persisted pick (`self._sessions.
                # get_voice`) and updates this local + sends a follow-up
                # session.update, so a Wi-Fi blip doesn't silently revert the
                # guest's own choice back to the default. Every later use of
                # "voice" in this coroutine (session.update rebuilding, echo)
                # reads this local, never a shared field.
                voice = self.voice_choice

                _vlog(verbose, "\n═══ [SESSION] Connected ═══\n"
                               "Session ID: %s\n"
                               "═══════════════════════════", session_id or "?")

                # Configure the upstream session BEFORE relaying a single browser
                # frame. Events are processed in order, so nothing the browser
                # sends (mic audio included) can reach an unconfigured session.
                await target_ws.send_str(guard.track(self.build_bootstrap_session_update(voice=voice)))
                logger.info("Upstream session bootstrapped with %d tools before relaying client traffic "
                            "(reasoning=%s, session=%s)", len(self.tools),
                            normalize_reasoning_effort(self.reasoning_effort) if self.reasoning_enabled() else "off",
                            session_id)

                async def send_greeting_once(trigger: str = "unknown"):
                    nonlocal greeting_sent
                    if greeting_sent:
                        return
                    # Same fix as the sibling brand repo's ba8c94d: don't greet until the
                    # server has confirmed the session configuration.
                    try:
                        await asyncio.wait_for(session_configured.wait(), timeout=_SESSION_CONFIGURED_TIMEOUT_SEC)
                    except TimeoutError:
                        logger.warning("No session.updated within %.0fs; sending greeting anyway (session=%s)",
                                       _SESSION_CONFIGURED_TIMEOUT_SEC, session_id)
                    if greeting_sent:
                        return
                    greeting_sent = True
                    logger.info("Greeting firing via trigger=%s (session=%s)", trigger, session_id)
                    _vlog(verbose, "─── [Lifecycle] Greeting trigger=%s ───", trigger)
                    echo.start_greeting_suppression(verbose)
                    # Flush any stale audio that arrived before session was configured
                    await target_ws.send_str(_INPUT_AUDIO_CLEAR_MSG)
                    greeting_msg = self._sessions.build_greeting_msg()
                    await target_ws.send_str(greeting_msg)
                    await target_ws.send_str(_RESPONSE_CREATE_MSG)
                    if session_id is not None:
                        self._sessions.mark_greeting_sent(session_id)
                    # Track greeting in context window
                    ctx_monitor = self._sessions.get_context_monitor(session_id)
                    if ctx_monitor:
                        ctx_monitor.add_content(greeting_msg)

                async def announce_fresh():
                    """Send extension.session_metadata (with a resume id) once the resume
                    decision is made and the upstream session exists."""
                    nonlocal announced
                    if announced or not resume_decided.is_set() or not upstream_created or session_id is None:
                        return
                    announced = True
                    identifiers = order_state_singleton.get_session_identifiers(session_id)
                    resume_id = self._sessions.issue_resume_id(session_id)
                    await self._sessions.emit_session_identifiers(
                        ws, "extension.session_metadata", identifiers,
                        extra={"resumeId": resume_id} if resume_id else None)
                    _vlog(verbose, "─── [SESSION TOKEN] ───\n"
                                   "Token: %s\n"
                                   "Round Trip: #%d (token: %s)\n"
                                   "───────────────────────",
                          identifiers.session_token, identifiers.round_trip_index, identifiers.round_trip_token)

                async def on_session_created():
                    nonlocal upstream_created
                    upstream_created = True
                    await announce_fresh()

                async def first_frame_deadline():
                    await asyncio.sleep(self._sessions.first_frame_timeout_seconds)
                    if not resume_decided.is_set():
                        resume_decided.set()
                        await announce_fresh()

                async def nudge_after_silence():
                    """If the guest says nothing for nudge_after_seconds after a resume, have
                    the carhop ask once whether they need anything else. Goes through the
                    same session.updated gate as the greeting so voice/tools are confirmed."""
                    await asyncio.sleep(self._sessions.nudge_after_seconds)
                    await session_configured.wait()
                    if recovery.busy:
                        # The carhop is already retrying a rate-limited response; a
                        # nudge now would stack a second response on top of it.
                        logger.info("Resume nudge skipped: a rate-limit retry is in progress (session=%s)", session_id)
                        return
                    logger.info("Guest silent %.0fs after resume; carhop nudges (session=%s)",
                                self._sessions.nudge_after_seconds, session_id)
                    nudge = self._sessions.build_nudge_item()
                    await target_ws.send_str(nudge)
                    await target_ws.send_str(_RESPONSE_CREATE_MSG)
                    ctx_monitor = self._sessions.get_context_monitor(session_id)
                    if ctx_monitor:
                        ctx_monitor.add_content(nudge)

                def cancel_nudge(reason: str) -> None:
                    nonlocal nudge_task
                    if nudge_task is not None and not nudge_task.done():
                        nudge_task.cancel()
                        logger.info("Resume nudge cancelled: %s (session=%s)", reason, session_id)
                    nudge_task = None

                async def handle_resume(data: str):
                    nonlocal session_id, announced, greeting_sent, nudge_task, voice
                    try:
                        presented = json.loads(data).get("resume_id")
                    except (ValueError, AttributeError):
                        presented = None
                    outcome = self._sessions.resume(ws, presented)
                    resume_decided.set()
                    if not outcome.accepted:
                        logger.info("Resume rejected (reason=%s, resume id %s); starting fresh session %s",
                                    outcome.reason, resume_id_fingerprint(presented), session_id)
                        await ws.send_json({"type": "extension.resume_rejected", "reason": outcome.reason})
                        await announce_fresh()
                        return
                    session_id = outcome.session_id
                    recovery.session_id = session_id
                    # #43 fix (PR #49 review round 6, "S1"): restore THIS
                    # session's own previously-picked voice, if any -- a
                    # resume opens a brand-new upstream connection (see
                    # module docstring), so it bootstrapped on the config
                    # default before we knew which session this socket was
                    # continuing. Without this, a Wi-Fi blip would silently
                    # revert the guest's own voice choice.
                    # #57 FU1: `assistant_audio_seen` is always False here in
                    # practice (handle_resume only ever runs on the first
                    # frame -- reject_late_resume handles every later one),
                    # so this guard changes nothing TODAY. It's kept anyway
                    # as a free, explicit safety net: sending a voice
                    # session.update after GA has voice-locked the upstream
                    # (assistant audio already produced) would be rejected
                    # wholesale (`cannot_update_voice`, see
                    # SessionUpdateFallbackTests) -- a real risk for a future
                    # refactor of this seam (e.g. other brand ports, or a C#
                    # backend) that no longer guarantees "resume is
                    # first-frame-only".
                    persisted_voice = self._sessions.get_voice(session_id)
                    if persisted_voice is not None and persisted_voice != voice and not assistant_audio_seen:
                        voice = persisted_voice
                        await target_ws.send_str(guard.track(self.build_voice_update(persisted_voice)))
                        logger.info("Restored voice %s for resumed session %s", persisted_voice, session_id)
                    if outcome.stale_ws is not None:
                        _spawn(_close_superseded(outcome.stale_ws))
                    identifiers = order_state_singleton.get_session_identifiers(session_id)
                    announced = True
                    await ws.send_json({
                        "type": "extension.session_resumed",
                        "order_summary": json.loads(order_state_singleton.get_order_summary_json(session_id)),
                        "session_token": identifiers.session_token,
                        "round_trip_index": identifiers.round_trip_index,
                        "round_trip_token": identifiers.round_trip_token,
                        "resume_id": outcome.resume_id,
                    })
                    if not outcome.conversation_started:
                        return                  # never greeted: the normal greeting still runs
                    # Mid-conversation: no greeting, no "welcome back". Brief the new
                    # upstream (after the bootstrap session.update, before any
                    # response.create) and stay silent until the guest speaks.
                    greeting_sent = True
                    rehydration = self._sessions.build_rehydration_item(session_id)
                    await target_ws.send_str(rehydration)
                    ctx_monitor = self._sessions.get_context_monitor(session_id)
                    if ctx_monitor:
                        ctx_monitor.add_content(rehydration)
                    logger.info("Resumed session %s rehydrated (%d recent turns); greeting suppressed",
                                session_id, len(self._sessions.recent_turns(session_id)))
                    if self._sessions.nudge_after_seconds > 0:
                        nudge_task = _spawn(nudge_after_silence())

                async def reject_late_resume(data: str):
                    nonlocal announced
                    try:
                        presented = json.loads(data).get("resume_id")
                    except (ValueError, AttributeError):
                        presented = None
                    logger.info("Resume rejected (reason=not_first_frame, resume id %s); session %s continues",
                                resume_id_fingerprint(presented), session_id)
                    await ws.send_json({"type": "extension.resume_rejected", "reason": "not_first_frame"})
                    # The browser drops its stored id on any rejection, so re-announce
                    # this socket's own session (with a rotated id) if already announced.
                    if announced:
                        announced = False
                        await announce_fresh()

                async def from_client_to_server():
                    nonlocal verbose, audio_frame_count, session_file_handler, first_frame_pending, voice
                    async for msg in ws:
                        if msg.type == aiohttp.WSMsgType.TEXT:
                            # Resume handshake: only the very first client frame may resume.
                            if first_frame_pending:
                                first_frame_pending = False
                                if not resume_decided.is_set() and _extension_type(msg.data, _MARKER_RESUME) == "extension.resume":
                                    await handle_resume(msg.data)
                                    continue
                                if not resume_decided.is_set():
                                    resume_decided.set()
                                    await announce_fresh()
                            if _extension_type(msg.data, _MARKER_RESUME) == "extension.resume":
                                await reject_late_resume(msg.data)
                                continue
                            if _extension_type(msg.data, _MARKER_END_SESSION) == "extension.end_session":
                                logger.info("Guest ended session %s", session_id)
                                self._sessions.end_session(session_id, "guest ended the session")
                                await ws.close(code=SESSION_ENDED_CLOSE_CODE, message=SESSION_ENDED_CLOSE_REASON.encode())
                                break
                            # Intercept extension messages — don't forward to OpenAI
                            if _MARKER_VERBOSE_LOGGING in msg.data:
                                try:
                                    ext_msg = json.loads(msg.data)
                                    if ext_msg.get("type") == "extension.set_verbose_logging":
                                        if not _client_log_control_allowed():
                                            # #53: process-wide log verbosity is never a
                                            # single connection's call to make in production
                                            # -- drop silently (from the guest's perspective)
                                            # and just log a WARNING server-side.
                                            _warn_dropped_frame(
                                                drop_limiter,
                                                "Dropped extension.set_verbose_logging from session %s "
                                                "(client log control is disabled in this deployment)",
                                                session_id)
                                            continue
                                        if session_id:
                                            self._sessions.touch_activity(session_id)
                                        verbose = bool(ext_msg.get("enabled", False))
                                        if verbose and not _VERBOSE_GLOBAL:
                                            vlogger.setLevel(logging.DEBUG)
                                            if not vlogger.handlers:
                                                _h = logging.StreamHandler()
                                                _h.setFormatter(logging.Formatter("%(message)s"))
                                                vlogger.addHandler(_h)
                                        logger.info("Verbose logging %s for session %s",
                                                    "ENABLED" if verbose else "DISABLED", session_id)
                                        _vlog(verbose,
                                              "\n╔══════════════════════════════════════╗\n"
                                              "║  VERBOSE LOGGING: %-8s           ║\n"
                                              "╚══════════════════════════════════════╝",
                                              "ENABLED" if verbose else "DISABLED")
                                        continue
                                except (json.JSONDecodeError, KeyError):
                                    pass

                            if _MARKER_LOG_TO_FILE in msg.data:
                                try:
                                    ext_msg = json.loads(msg.data)
                                    if ext_msg.get("type") == "extension.set_log_to_file":
                                        if not _client_log_control_allowed():
                                            # #53: same reasoning as extension.set_verbose_logging
                                            # above -- additionally, file logging risks filling
                                            # the disk and writing guest data to disk, so this
                                            # must never be a single connection's call in
                                            # production.
                                            _warn_dropped_frame(
                                                drop_limiter,
                                                "Dropped extension.set_log_to_file from session %s "
                                                "(client log control is disabled in this deployment)",
                                                session_id)
                                            continue
                                        if session_id:
                                            self._sessions.touch_activity(session_id)
                                        enabled = bool(ext_msg.get("enabled", False))
                                        if enabled and session_file_handler is None:
                                            vlogger.setLevel(logging.DEBUG)
                                            if not any(isinstance(h, logging.StreamHandler) and not isinstance(h, logging.FileHandler) for h in vlogger.handlers):
                                                _h = logging.StreamHandler()
                                                _h.setFormatter(logging.Formatter("%(message)s"))
                                                vlogger.addHandler(_h)
                                            session_file_handler = _create_verbose_file_handler()
                                            vlogger.addHandler(session_file_handler)
                                        elif not enabled and session_file_handler is not None:
                                            _remove_verbose_file_handler(session_file_handler)
                                            session_file_handler = None
                                        logger.info("Verbose log-to-file %s for session %s",
                                                    "ENABLED" if enabled else "DISABLED", session_id)
                                        _vlog(verbose or enabled,
                                              "\n╔══════════════════════════════════════╗\n"
                                              "║  LOG TO FILE: %-8s              ║\n"
                                              "╚══════════════════════════════════════╝",
                                              "ENABLED" if enabled else "DISABLED")
                                        continue
                                except (json.JSONDecodeError, KeyError):
                                    pass

                            if _MARKER_SET_VOICE in msg.data:
                                try:
                                    ext_msg = json.loads(msg.data)
                                    if ext_msg.get("type") == "extension.set_voice":
                                        if session_id:
                                            self._sessions.touch_activity(session_id)
                                        # M1: only a value from the server's own allow-list may
                                        # ever be forwarded or adopted -- anything else (a forged
                                        # voice name) is dropped with a WARNING, not forwarded
                                        # upstream and not adopted anywhere.
                                        new_voice = _sanitize_voice(ext_msg.get("voice"), self.allowed_voices)
                                        if new_voice is None:
                                            _warn_dropped_frame(
                                                drop_limiter,
                                                "Dropped extension.set_voice with an unknown/invalid voice %s (session=%s)",
                                                _truncate_for_log(ext_msg.get("voice")), session_id)
                                        else:
                                            # #43 (PR #49 review round 6, "S1"): persist the
                                            # pick on THIS session (self._sessions), never on
                                            # RTMiddleTier -- so it can only ever be read back
                                            # for the SAME guest's session (on resume) and can
                                            # never become any other, brand-new connection's
                                            # bootstrap default. This connection's own `voice`
                                            # local is updated too, so ITS subsequent
                                            # session.update rebuilding / echo reflect the pick
                                            # immediately, same as before #43.
                                            if session_id:
                                                self._sessions.set_voice(session_id, new_voice)
                                            voice = new_voice
                                            logger.info("Voice changed to %s for session %s", new_voice, session_id)
                                            if assistant_audio_seen:
                                                # GA would reject this with cannot_update_voice.
                                                logger.info("Assistant audio already present — voice %s applies from the next conversation", new_voice)
                                            else:
                                                await target_ws.send_str(guard.track(self.build_voice_update(new_voice)))
                                        continue
                                except (json.JSONDecodeError, KeyError):
                                    pass

                            # Echo suppression: drop mic audio while AI is speaking or cooling down.
                            if _MARKER_AUDIO_APPEND in msg.data:
                                if echo.should_suppress_audio(loop.time()):
                                    continue
                                audio_frame_count += 1
                                if (verbose or _VERBOSE_GLOBAL) and audio_frame_count % 50 == 0:
                                    _vlog(verbose, "─── [Client → Server] Audio frame #%d ───", audio_frame_count)
                            # Forward client message to OpenAI.
                            new_msg, sent_type = await self._process_message_to_server(msg, ws, verbose, voice_locked=assistant_audio_seen, guard=guard, voice=voice, limiter=drop_limiter)
                            # PR #49 review round 2, "F1": idle reset, nudge
                            # cancel and the greeting trigger used to be keyed
                            # on raw substring checks against msg.data,
                            # evaluated before (or independently of) the
                            # filter above -- so a frame the filter would drop
                            # (e.g. one merely *containing* the substring
                            # "response.cancel" or "session.update" somewhere,
                            # without actually being that type) could still
                            # trigger them. They're now keyed on the
                            # already-validated `sent_type` returned directly
                            # by `_process_message_to_server` (PR #49 review
                            # round 5, "F4" -- previously this line
                            # re-`json.loads`-ed `new_msg` itself to recover
                            # the type) -- never from the browser's raw
                            # bytes -- so a dropped or malformed frame
                            # triggers nothing.
                            if new_msg is not None:
                                await target_ws.send_str(new_msg)
                            # Guest activity drives the idle clock. Mic frames
                            # stream constantly (silence included), so they
                            # don't count; the guest actually speaking does
                            # (speech_started/transcripts from upstream, or any
                            # other forwarded client event here).
                            if session_id and sent_type is not None and sent_type != "input_audio_buffer.append":
                                self._sessions.touch_activity(session_id)
                            if sent_type == "response.create":
                                if nudge_task is not None:
                                    cancel_nudge("guest-initiated response")
                                recovery.on_external_response_create("browser")
                                # PR #58 re-review Nit: the browser's own response.create is
                                # not the rate-limit ladder's retry of a greeting that produced
                                # no audio (#48 M1) -- cancel any pending re-arm so this new
                                # response's audio isn't mistaken for the greeting's own.
                                echo.on_external_response_create()
                            # The browser's session.update marks the start of a conversation.
                            if not greeting_sent and sent_type == "session.update":
                                logger.info("Client session.update forwarded — sending greeting")
                                await send_greeting_once(trigger="client-session.update")
                            # PR #49 review round 5, "F1": barge-in used to be
                            # keyed on the raw `_MARKER_RESPONSE_CANCEL in
                            # msg.data` substring check, evaluated on the
                            # browser's raw bytes before the filter above ever
                            # ran -- so a frame merely *containing* the
                            # substring "response.cancel" somewhere (e.g.
                            # buried in an unrelated field), without actually
                            # being that type, could still disable echo
                            # suppression. Same seam/fix as the idle/nudge/
                            # greeting triggers above: keyed on the validated
                            # `sent_type`, never the raw bytes.
                            if sent_type == "response.cancel":
                                echo.on_barge_in(verbose)
                        elif msg.type == aiohttp.WSMsgType.ERROR:
                            logger.error("Client WebSocket error: %s", ws.exception())
                            break
                        elif msg.type in (aiohttp.WSMsgType.CLOSE, aiohttp.WSMsgType.CLOSING, aiohttp.WSMsgType.CLOSED):
                            break
                    
                    if target_ws and not target_ws.closed:
                        logger.info("Closing OpenAI's realtime socket connection.")
                        _vlog(verbose, "─── [Lifecycle] Disconnect — closing OpenAI socket ───")
                        await target_ws.close()
                        
                async def from_server_to_client():
                    nonlocal assistant_audio_seen
                    async for msg in target_ws:
                        if msg.type == aiohttp.WSMsgType.TEXT:
                            data = msg.data
                            if _MARKER_AUDIO_DELTA in data or _MARKER_AUDIO_DELTA_LEGACY in data:
                                assistant_audio_seen = True
                                echo.on_audio_delta(verbose)
                            elif _MARKER_AUDIO_DONE in data or _MARKER_AUDIO_DONE_LEGACY in data:
                                # swigerb/SonicAIDriveThru#59: route the echo
                                # flush's fire-and-forget sends through this
                                # module's own background-task tracking so
                                # they're held alive until done, same as
                                # every other spawned task on the connection.
                                echo.on_audio_done(loop, target_ws, verbose, spawn=_spawn)
                            elif _MARKER_SPEECH_STARTED in data:
                                echo.on_speech_started(verbose)
                                if session_id:
                                    self._sessions.touch_activity(session_id)
                                cancel_nudge("guest speech")
                                recovery.on_guest_speech()
                                if tool_failures is not None:
                                    # #36 S1 (PR #58 re-review): input_audio_buffer.speech_started
                                    # is in _PASSTHROUGH_SERVER_TYPES (the fast path below returns
                                    # before _process_message_to_client's switch/case ever runs),
                                    # so this marker check -- not that switch/case -- is the only
                                    # reachable place to reset the tool-failure streak on genuine
                                    # guest speech.
                                    tool_failures.reset_for_new_turn()
                            elif _MARKER_TRANSCRIPTION_COMPLETED in data and session_id:
                                self._sessions.touch_activity(session_id)
                                cancel_nudge("guest transcript")
                                try:
                                    self._sessions.record_turn(session_id, "guest", json.loads(data).get("transcript"))
                                except (ValueError, AttributeError):
                                    pass
                                if tool_failures is not None:
                                    # #36 S1 (PR #58 re-review): a completed input transcription
                                    # is the other guest-turn signal that breaks the failure streak.
                                    tool_failures.reset_for_new_turn()
                            elif _MARKER_RESPONSE_DONE in data:
                                # swigerb/SonicAIDriveThru#48: a greeting that produced no
                                # audio (text-only fallback, cancelled/failed before any
                                # audio, a no-output rate-limited retry) never reaches
                                # echo.on_audio_done() -- response.done is the guaranteed
                                # event for every response, so it's the fallback that ends
                                # greeting suppression instead of leaving the mic muted
                                # until the guest physically interrupts.
                                echo.on_response_done(loop, target_ws, verbose)

                            # The bootstrap session.updated arrives as soon as the socket
                            # opens, so it must NOT trigger the greeting -- the browser's
                            # session.update (conversation start) does that.
                            if _MARKER_SESSION_UPDATED in data:
                                guard.on_session_updated()
                                if not session_configured.is_set():
                                    logger.info("session.updated received — tools are configured (session=%s)", session_id)
                                    _vlog(verbose, "─── [Lifecycle] session.updated — session configured ───")
                                    session_configured.set()

                            # Verbose: log conversation transcription events
                            if (verbose or _VERBOSE_GLOBAL):
                                if '"conversation.item.input_audio_transcription.completed"' in data:
                                    try:
                                        _tr_msg = json.loads(data)
                                        _tr_text = _tr_msg.get("transcript", "")[:200]
                                        _vlog(verbose, '\n─── [User] transcription.completed ───\n"%s"', _tr_text)
                                        # Track user input in context window
                                        ctx_monitor = self._sessions.get_context_monitor(session_id)
                                        if ctx_monitor:
                                            ctx_monitor.add_content(_tr_text)
                                    except (json.JSONDecodeError, KeyError):
                                        pass

                            new_msg = await self._process_message_to_client(msg, ws, target_ws, tools_pending, verbose, guard=guard,
                                                                            on_session_created=on_session_created,
                                                                            recovery=recovery, voice=voice, tool_failures=tool_failures)
                            if new_msg is not None:
                                await ws.send_str(new_msg)
                        elif msg.type == aiohttp.WSMsgType.ERROR:
                            logger.error("Server WebSocket error: %s", target_ws.exception())
                            break
                        elif msg.type in (aiohttp.WSMsgType.CLOSE, aiohttp.WSMsgType.CLOSING, aiohttp.WSMsgType.CLOSED):
                            break

                deadline_task = _spawn(first_frame_deadline())
                try:
                    await asyncio.gather(from_client_to_server(), from_server_to_client())
                except ConnectionResetError:
                    pass
                except Exception:
                    logger.exception("Unexpected error in WebSocket forwarding")
                finally:
                    deadline_task.cancel()
                    cancel_nudge("socket closed")
                    recovery.cancel("socket closed")
                    # swigerb/SonicAIDriveThru#59: cancel any delayed echo
                    # flush timer so it can't fire (and attempt a send)
                    # after this connection has already gone away.
                    echo.close()
                    _vlog(verbose, "\n═══ [SESSION] Disconnected ═══\n"
                                   "Session ID: %s\n"
                                   "══════════════════════════════", session_id or "?")
                    if session_file_handler is not None:
                        _remove_verbose_file_handler(session_file_handler)
                        session_file_handler = None
                    self._sessions.detach_session(ws, session_id, reason=f"client close code={ws.close_code}")

    async def _websocket_handler(self, request: web.Request):
        # ── Origin validation (Task 3) ──
        # Missing/empty Origin is accepted unchanged (non-browser and same-
        # process callers legitimately omit it) -- #25 only hardens the case
        # where an Origin *is* present.
        origin = request.headers.get("Origin", "")
        allowed_origins = _security_cfg.get("allowed_origins", [])
        host = request.headers.get("Host", "")
        if origin and not _origin_matches_host(origin, host) and origin not in allowed_origins:
            logger.warning("Rejected WebSocket from disallowed origin: host=%s origin=%s", host, origin)
            return web.Response(status=403, text="Origin not allowed")

        # ── HMAC session token validation (Task 4) ──
        if _security_cfg.get("require_session_token", False):
            token = request.query.get("token", "")
            if not validate_hmac_token(token, self.app_secret):
                logger.warning("Rejected WebSocket with invalid/expired session token")
                return web.Response(status=401, text="Invalid or expired token")

        # ── Concurrency limit (Task 2) ──
        if not self._sessions.can_accept_session():
            logger.warning("Rejected WebSocket — session limit reached (%d)", self._sessions.active_session_count)
            ws = web.WebSocketResponse()
            await ws.prepare(request)
            await ws.send_json({"type": "error", "message": "Server is busy — please try again in a moment."})
            await ws.close()
            return ws

        ws = web.WebSocketResponse(
            heartbeat=_WS_HEARTBEAT_SEC,
            autoping=True,
            autoclose=True,
            compress=_WS_COMPRESS,
        )
        await ws.prepare(request)
        
        self._sessions.create_session(ws)

        try:
            await self._forward_messages(ws)
        finally:
            # Covers an upstream connect failure, which never reaches the
            # forwarder's own finally. A no-op if that already ran.
            self._sessions.detach_session(ws, self._sessions.get_session_id(ws),
                                          reason=f"handler exit code={ws.close_code}")
        return ws
    
    def attach_to_app(self, app: web.Application, path: str) -> None:
        app.router.add_get(path, self._websocket_handler)


def configure_realtime_model(rtmt: RTMiddleTier, model_cfg: dict, environ: Any = None) -> RTMiddleTier:
    """Apply `config.yaml` `model:` settings plus their env overrides to `rtmt`.

    Shared by app.py and scripts/smoke_realtime.py so the smoke check sends
    exactly the session the app sends.
    """
    env = os.environ if environ is None else environ
    rtmt.temperature = model_cfg.get("temperature", 0.6)
    rtmt.max_tokens = model_cfg.get("max_response_output_tokens", 4096)
    rtmt.transcription_model = (env.get("AZURE_OPENAI_REALTIME_TRANSCRIPTION_MODEL")
                                or model_cfg.get("transcription_model") or "whisper-1")
    effort = env.get("AZURE_OPENAI_REALTIME_REASONING_EFFORT")
    rtmt.reasoning_effort = normalize_reasoning_effort(effort if effort else model_cfg.get("reasoning_effort"))
    parallel = model_cfg.get("parallel_tool_calls")
    rtmt.parallel_tool_calls = None if parallel is None else bool(parallel)
    switch = env.get("AZURE_OPENAI_REALTIME_REASONING_MODEL")
    rtmt.reasoning_model = parse_reasoning_model(switch if switch else model_cfg.get("reasoning_model"))
    configured_voices = model_cfg.get("allowed_voices")
    if configured_voices:
        # #57 FU2: a bare string (e.g. "marin") is iterable character-by-character
        # in Python, so `frozenset(str(v) for v in "marin")` would silently become
        # {"m", "a", "r", "i", "n"} instead of raising -- fail loudly at startup
        # instead of shipping a config typo that rejects every voice pick.
        if not isinstance(configured_voices, list):
            raise ValueError(
                f"model.allowed_voices must be a list of voice names, got "
                f"{type(configured_voices).__name__} ({configured_voices!r})")
        rtmt.allowed_voices = frozenset(str(v) for v in configured_voices)
    else:
        rtmt.allowed_voices = _DEFAULT_ALLOWED_VOICES
    # #57 FU2: the default voice (AZURE_OPENAI_REALTIME_VOICE_CHOICE / model.default_voice,
    # already applied to rtmt.voice_choice by the caller) must itself be an allowed voice --
    # otherwise every guest's bootstrap session.update would request a voice GA rejects.
    if isinstance(rtmt.voice_choice, str) and rtmt.voice_choice not in rtmt.allowed_voices:
        raise ValueError(
            f"The default voice {rtmt.voice_choice!r} (AZURE_OPENAI_REALTIME_VOICE_CHOICE / "
            f"model.default_voice) is not in model.allowed_voices ({sorted(rtmt.allowed_voices)})")
    if rtmt.reasoning_effort is not None and not rtmt._reasoning_model():
        logger.info("Deployment %s is not treated as a reasoning model (reasoning_model=%s); `reasoning` "
                    "(effort=%s) will not be sent", rtmt.deployment,
                    "auto" if rtmt.reasoning_model is None else rtmt.reasoning_model, rtmt.reasoning_effort)
    return rtmt
