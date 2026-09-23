"""Unit tests proving test_hooks.py is completely inert unless
CONFORMANCE_TEST_HOOKS=1 is set (issue #7).

Mutation-checked: flip either `if HOOKS_ENABLED:` guard in test_hooks.py to
always take the "enabled" branch (or delete it) and at least one test in
TestInertWhenUnset must fail; flip it to always take the "disabled" branch and
at least one test in TestActiveWhenSet must fail. Having both inert- and
active-path tests is what makes this a real mutation check rather than a test
that only ever exercises the disabled branch.
"""
import importlib
import sys
from datetime import datetime, timedelta
from pathlib import Path
from zoneinfo import ZoneInfo

import pytest

sys.path.append(str(Path(__file__).resolve().parents[1]))

import test_hooks  # noqa: E402

_TZ = ZoneInfo("America/Chicago")


@pytest.fixture(autouse=True)
def _reset_test_hooks_module(monkeypatch):
    """Every test starts and ends with a freshly-imported, disabled test_hooks
    module, regardless of what a previous test (or the ambient shell) left in
    the environment. HOOKS_ENABLED is a module-level constant read once at
    import time, so env changes only take effect after reload()."""
    monkeypatch.delenv("CONFORMANCE_TEST_HOOKS", raising=False)
    monkeypatch.delenv("CONFORMANCE_FIXED_NOW", raising=False)
    importlib.reload(test_hooks)
    yield
    monkeypatch.delenv("CONFORMANCE_TEST_HOOKS", raising=False)
    monkeypatch.delenv("CONFORMANCE_FIXED_NOW", raising=False)
    importlib.reload(test_hooks)


def _enable(monkeypatch, **env):
    monkeypatch.setenv("CONFORMANCE_TEST_HOOKS", "1")
    for key, value in env.items():
        monkeypatch.setenv(key, value)
    importlib.reload(test_hooks)


class TestInertWhenUnset:
    """CONFORMANCE_TEST_HOOKS unset (or not the literal "1") -> real clock,
    real timer defaults, no matter what the override env vars say."""

    def test_hooks_disabled_by_default(self):
        assert test_hooks.HOOKS_ENABLED is False

    def test_now_ignores_fixed_now_when_disabled(self, monkeypatch):
        monkeypatch.setenv("CONFORMANCE_FIXED_NOW", "2020-01-01T00:00:00-06:00")
        importlib.reload(test_hooks)
        before = datetime.now(_TZ)
        result = test_hooks.now(_TZ)
        after = datetime.now(_TZ)
        assert before <= result <= after + timedelta(seconds=5)
        assert result.year != 2020

    def test_seconds_ignores_override_when_disabled(self, monkeypatch):
        monkeypatch.setenv("CONFORMANCE_IDLE_TIMEOUT_SECONDS", "1")
        importlib.reload(test_hooks)
        assert test_hooks.seconds("CONFORMANCE_IDLE_TIMEOUT_SECONDS", 300) == 300

    @pytest.mark.parametrize("flag_value", ["0", "false", "yes", "TRUE", ""])
    def test_only_the_literal_value_1_enables_hooks(self, monkeypatch, flag_value):
        # Anything other than the string "1" must leave the hooks disabled --
        # a truthy-string bug here would be easy to accidentally trip in CI.
        monkeypatch.setenv("CONFORMANCE_TEST_HOOKS", flag_value)
        importlib.reload(test_hooks)
        assert test_hooks.HOOKS_ENABLED is False


class TestActiveWhenSet:
    """CONFORMANCE_TEST_HOOKS=1 -> the fixed clock and timer overrides take
    effect. Exercising this positive path is what makes the inertness tests
    above a real mutation check: a stub that always takes the disabled branch
    would still pass every TestInertWhenUnset test, but would fail these."""

    def test_hooks_enabled_flag(self, monkeypatch):
        _enable(monkeypatch)
        assert test_hooks.HOOKS_ENABLED is True

    def test_now_honours_fixed_now(self, monkeypatch):
        _enable(monkeypatch, CONFORMANCE_FIXED_NOW="2026-07-04T15:30:00-05:00")
        result = test_hooks.now(_TZ)
        assert result == datetime(2026, 7, 4, 15, 30, 0, tzinfo=_TZ)

    def test_now_converts_fixed_now_into_the_requested_zone(self, monkeypatch):
        _enable(monkeypatch, CONFORMANCE_FIXED_NOW="2026-07-04T20:30:00+00:00")
        result = test_hooks.now(_TZ)
        assert result.hour == 15  # 20:30 UTC == 15:30 America/Chicago (CDT, UTC-5)

    def test_now_falls_back_to_real_clock_when_fixed_now_unset(self, monkeypatch):
        _enable(monkeypatch)
        before = datetime.now(_TZ)
        result = test_hooks.now(_TZ)
        after = datetime.now(_TZ)
        assert before <= result <= after + timedelta(seconds=5)

    def test_now_rejects_naive_fixed_now(self, monkeypatch):
        _enable(monkeypatch, CONFORMANCE_FIXED_NOW="2026-07-04T15:30:00")
        with pytest.raises(ValueError, match="CONFORMANCE_FIXED_NOW"):
            test_hooks.now(_TZ)

    def test_seconds_honours_override(self, monkeypatch):
        _enable(monkeypatch, CONFORMANCE_IDLE_TIMEOUT_SECONDS="3")
        assert test_hooks.seconds("CONFORMANCE_IDLE_TIMEOUT_SECONDS", 300) == 3.0

    def test_seconds_falls_back_on_unparseable_override(self, monkeypatch):
        _enable(monkeypatch, CONFORMANCE_IDLE_TIMEOUT_SECONDS="not-a-number")
        assert test_hooks.seconds("CONFORMANCE_IDLE_TIMEOUT_SECONDS", 300) == 300

    def test_seconds_falls_back_when_override_unset(self, monkeypatch):
        _enable(monkeypatch)
        assert test_hooks.seconds("CONFORMANCE_IDLE_TIMEOUT_SECONDS", 300) == 300


class TestHappyHourIntegration:
    """order_state.is_happy_hour() is the primary consumer named in issue #7;
    prove the wiring is inert there too, not just in test_hooks.py itself."""

    def test_happy_hour_honours_fixed_clock_when_enabled(self, monkeypatch):
        import order_state

        _enable(monkeypatch, CONFORMANCE_FIXED_NOW="2026-07-04T15:00:00-05:00")
        importlib.reload(order_state)
        try:
            # 15:00 store-local is inside the default 14-16 happy hour window.
            assert order_state.is_happy_hour() is True
        finally:
            monkeypatch.delenv("CONFORMANCE_TEST_HOOKS", raising=False)
            monkeypatch.delenv("CONFORMANCE_FIXED_NOW", raising=False)
            importlib.reload(test_hooks)
            importlib.reload(order_state)

    def test_happy_hour_ignores_fixed_clock_when_disabled(self, monkeypatch):
        import order_state

        # Hooks disabled: even though CONFORMANCE_FIXED_NOW is set (and would
        # force happy-hour-on if honoured), is_happy_hour() must fall back to
        # the real wall clock -- computed here independently so the assertion
        # is deterministic regardless of the real time of day.
        monkeypatch.setenv("CONFORMANCE_FIXED_NOW", "2026-07-04T15:00:00-05:00")
        importlib.reload(test_hooks)
        importlib.reload(order_state)
        try:
            now = datetime.now(order_state._STORE_TZ)
            start = order_state._biz_cfg.get("happy_hour_start", 14)
            end = order_state._biz_cfg.get("happy_hour_end", 16)
            expected = start <= now.hour < end
            assert order_state.is_happy_hour() == expected
        finally:
            monkeypatch.delenv("CONFORMANCE_FIXED_NOW", raising=False)
            importlib.reload(test_hooks)
            importlib.reload(order_state)
