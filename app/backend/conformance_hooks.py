"""Test-only hooks for the .NET conformance harness (tests/conformance, issue #7).

Everything in this module is a no-op unless ``CONFORMANCE_TEST_HOOKS=1`` is set
in the process environment. It exists so the black-box conformance suite can
exercise happy-hour pricing and timer-driven behaviour (idle timeout, resume
grace, resume nudge, the first-frame resume timeout, the greeting timeout, the
idle/grace sweep interval, and rate-limit retry delays) deterministically and
fast, without sleeping through real wall-clock minutes or hard-coding guesses
about production timer values.

Kept deliberately small and centralised: every other module reads the clock or
a timer default through this file rather than rolling its own env var
handling, so the entire gated surface is auditable in one place.

Two independent mechanisms -- don't conflate them:

- ``now(tz)`` freezes *business-logic* wall-clock reads only. Today the only
  consumer is ``order_state.py``'s happy-hour / time-based pricing lookup. It
  has **no effect** on any asyncio timer duration.
- ``seconds(env_var, default)`` overrides *timer durations* fed into
  ``asyncio.sleep`` / deadline arithmetic (idle timeout, resume grace, resume
  nudge, the first-frame resume timeout, the greeting timeout, the idle/grace
  sweep interval, and the two rate-limit retry delays). It has **no effect**
  on what ``now()`` returns.

Both are gated independently behind ``CONFORMANCE_TEST_HOOKS=1`` and either
can be used without the other.

One caveat worth flagging here even though it lives in ``rate_limit.py``:
the *default* delay fed into ``retry_delay()`` is overridable via ``seconds()``
above, but the clamp bounds (``FIRST_RETRY_BOUNDS`` / ``SECOND_RETRY_BOUNDS``)
are **not** -- a scripted rate-limit hint still clamps into the *production*
bounds even when hooks are enabled. See ``rate_limit.py``'s module docstring.

Startup validation: if hooks are enabled and ``CONFORMANCE_FIXED_NOW`` is set
but malformed (missing an explicit numeric UTC offset, or not a parseable
ISO-8601/RFC 3339 timestamp), this module raises **immediately at import
time** -- not lazily the first time some downstream code happens to call
``now()`` -- so a bad harness configuration fails the backend's startup with
a clear message instead of surfacing as a confusing failure deep inside
unrelated business-logic code much later in a test run. ``seconds()`` is
equally fail-fast for its own override: an enabled, non-empty override that
isn't a positive finite number raises ``ValueError`` at the call site, which
every consumer evaluates at its own module import time (see
``session_manager.py``, ``rate_limit.py``, ``rtmt.py``), so a bad
``CONFORMANCE_*_SECONDS`` value also fails backend startup rather than
silently falling back to the production default for an entire test run.

NEVER set CONFORMANCE_TEST_HOOKS in infra/ (bicep), the Dockerfile, or
azure.yaml. It must only ever be set by the conformance harness's own
child-process environment (see tests/conformance's BackendEnvironment).
Guarded by a dedicated guard test
(``tests/test_conformance_hooks.py::TestNeverInInfraOrDockerfile``) that scans
those files for the literal string and fails the build if it ever appears.
"""
from __future__ import annotations

import logging
import math
import os
from datetime import datetime
from zoneinfo import ZoneInfo

__all__ = ["HOOKS_ENABLED", "now", "seconds"]

logger = logging.getLogger("sonic-drive-in")

_ENABLED_ENV = "CONFORMANCE_TEST_HOOKS"
_FIXED_NOW_ENV = "CONFORMANCE_FIXED_NOW"

# Read once at import time, like every other env-driven constant in this
# codebase (see e.g. session_manager.py's module-level _RESUME_* constants).
# Tests that need a different value re-import the module in-process (see
# app/backend/tests/test_conformance_hooks.py) rather than mutating this at
# runtime.
HOOKS_ENABLED = os.environ.get(_ENABLED_ENV, "").strip() == "1"


def _parse_fixed_now(raw: str) -> datetime:
    """Parse ``CONFORMANCE_FIXED_NOW``, raising a clear error if it's naive.

    RFC 3339 with an explicit numeric UTC offset (e.g.
    ``"2026-07-04T15:30:00-05:00"``, or a trailing ``Z`` for UTC) is required
    so the fixed instant is unambiguous regardless of the store timezone
    under test. Note this is a numeric offset only -- ``datetime.fromisoformat``
    does **not** accept a trailing IANA zone name (e.g. ``"... America/Chicago"``
    raises ``ValueError``), so despite this module and issue #7 sometimes
    describing the format loosely as "ISO-8601 plus IANA zone", an IANA zone
    *name* is never a valid value for this env var -- only a numeric offset is.
    """
    fixed = datetime.fromisoformat(raw)
    if fixed.tzinfo is None:
        raise ValueError(
            f"{_FIXED_NOW_ENV} must be an RFC 3339 timestamp with an explicit numeric UTC "
            f"offset (e.g. '2026-07-04T15:30:00-05:00' or '...Z'), got: {raw!r}"
        )
    return fixed


def _validate_at_startup() -> None:
    """Fail fast at import time if hooks are enabled but misconfigured.

    Without this, a malformed CONFORMANCE_FIXED_NOW would only surface the
    first time some downstream business-logic code happened to call now() --
    possibly minutes into a test run, far from the actual misconfiguration.
    """
    if not HOOKS_ENABLED:
        return
    raw = os.environ.get(_FIXED_NOW_ENV)
    if raw:
        _parse_fixed_now(raw)
    logger.warning(
        "%s=1 -- test-only clock/timer overrides are ACTIVE for this process. This must "
        "never be set in a deployed environment (see conformance_hooks.py's module "
        "docstring and its infra/Dockerfile/azure.yaml guard test).",
        _ENABLED_ENV,
    )


_validate_at_startup()


def now(tz: ZoneInfo) -> datetime:
    """Return the current time in ``tz``.

    Identical to ``datetime.now(tz)`` unless test hooks are enabled AND
    ``CONFORMANCE_FIXED_NOW`` is set, in which case that fixed instant is
    returned instead (converted into ``tz`` so callers always get a tz-aware
    datetime in the zone they asked for). See the module docstring: this
    affects business-logic wall-clock reads only, never timer durations.
    """
    if HOOKS_ENABLED:
        raw = os.environ.get(_FIXED_NOW_ENV)
        if raw:
            return _parse_fixed_now(raw).astimezone(tz)
    return datetime.now(tz)


def seconds(env_var: str, default: float) -> float:
    """Return a timer override read from ``env_var``, or ``default`` unchanged.

    The override only applies when test hooks are enabled AND ``env_var`` is
    set to a non-empty value; in that case the value must parse as a finite,
    strictly-positive float or this raises ``ValueError`` immediately -- it
    does **not** silently fall back to ``default``. Since every call site
    assigns the result to a module-level constant at its own import time
    (see ``session_manager.py``, ``rate_limit.py``, ``rtmt.py``), an invalid
    override fails the backend's startup with a clear, non-zero-exit error
    instead of quietly using a wrong-but-plausible timer value for an entire
    test run. Hooks disabled, or the var unset/empty, returns ``default``
    untouched -- neither of those is a misconfiguration. See the module
    docstring: this affects timer durations only, never what ``now()``
    returns.
    """
    if HOOKS_ENABLED:
        raw = os.environ.get(env_var)
        if raw:
            try:
                value = float(raw)
            except ValueError as exc:
                raise ValueError(
                    f"{env_var} must be a positive, finite number of seconds "
                    f"(test hooks are enabled), got unparseable value: {raw!r}"
                ) from exc
            if not math.isfinite(value) or value <= 0:
                raise ValueError(
                    f"{env_var} must be a positive, finite number of seconds "
                    f"(test hooks are enabled), got: {raw!r}"
                )
            return value
    return default
