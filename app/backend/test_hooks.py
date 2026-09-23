"""Test-only hooks for the .NET conformance harness (tests/conformance, issue #7).

Everything in this module is a no-op unless ``CONFORMANCE_TEST_HOOKS=1`` is set
in the process environment. It exists so the black-box conformance suite can
exercise happy-hour pricing and timer-driven behaviour (idle timeout, resume
grace, resume nudge, the first-frame resume timeout, the greeting timeout, and
rate-limit retry delays) deterministically and fast, without sleeping through
real wall-clock minutes or hard-coding guesses about production timer values.

Kept deliberately small and centralised: every other module reads the clock or
a timer default through this file rather than rolling its own env var
handling, so the entire gated surface is auditable in one place.

NEVER set CONFORMANCE_TEST_HOOKS in infra/ (bicep) or the Dockerfile. It must
only ever be set by the conformance harness's own child-process environment
(see tests/conformance's BackendEnvironment), never in a deployed environment.
"""
from __future__ import annotations

import os
from datetime import datetime
from zoneinfo import ZoneInfo

__all__ = ["HOOKS_ENABLED", "now", "seconds"]

_ENABLED_ENV = "CONFORMANCE_TEST_HOOKS"
_FIXED_NOW_ENV = "CONFORMANCE_FIXED_NOW"

# Read once at import time, like every other env-driven constant in this
# codebase (see e.g. session_manager.py's module-level _RESUME_* constants).
# Tests that need a different value re-import the module in-process (see
# app/backend/tests/test_test_hooks.py) rather than mutating this at runtime.
HOOKS_ENABLED = os.environ.get(_ENABLED_ENV, "").strip() == "1"


def now(tz: ZoneInfo) -> datetime:
    """Return the current time in ``tz``.

    Identical to ``datetime.now(tz)`` unless test hooks are enabled AND
    ``CONFORMANCE_FIXED_NOW`` is set, in which case that fixed instant is
    returned instead (converted into ``tz`` so callers always get a tz-aware
    datetime in the zone they asked for).

    ``CONFORMANCE_FIXED_NOW`` must be an ISO-8601 timestamp with an explicit
    UTC offset or IANA zone (e.g. ``"2026-07-04T15:30:00-05:00"``) so the
    fixed instant is unambiguous regardless of the store timezone under test.
    """
    if HOOKS_ENABLED:
        raw = os.environ.get(_FIXED_NOW_ENV)
        if raw:
            fixed = datetime.fromisoformat(raw)
            if fixed.tzinfo is None:
                raise ValueError(
                    f"{_FIXED_NOW_ENV} must include a UTC offset or zone, got: {raw!r}"
                )
            return fixed.astimezone(tz)
    return datetime.now(tz)


def seconds(env_var: str, default: float) -> float:
    """Return a timer override read from ``env_var``, or ``default`` unchanged.

    The override only applies when test hooks are enabled AND ``env_var`` is
    set to a value that parses as a float; any other case (hooks disabled,
    the var unset, or an unparseable value) returns ``default`` untouched.
    """
    if HOOKS_ENABLED:
        raw = os.environ.get(env_var)
        if raw:
            try:
                return float(raw)
            except ValueError:
                pass
    return default
