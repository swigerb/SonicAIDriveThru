"""Unit tests proving conformance_hooks.py is completely inert unless
CONFORMANCE_TEST_HOOKS=1 is set (issue #7), plus its startup-validation
fail-fast behaviour and the infra/Dockerfile/azure.yaml guard (PR #22 review
item 14).

Mutation-checked: flip either `if HOOKS_ENABLED:` guard in conformance_hooks.py
to always take the "enabled" branch (or delete it) and at least one test in
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

import conformance_hooks  # noqa: E402

_TZ = ZoneInfo("America/Chicago")
_REPO_ROOT = Path(__file__).resolve().parents[3]


def _isolated_order_state():
    """Load a private, throwaway copy of order_state.py under an alias that is
    never registered as sys.modules['order_state'].

    order_state.py defines the SessionIdentifiers class and the module-level
    order_state_singleton instance. Every other test file does
    `from order_state import SessionIdentifiers` / `order_state_singleton` at
    collection time, capturing a reference to *that specific* class/instance
    object. importlib.reload(order_state) mutates the canonical
    sys.modules['order_state'] entry in place and, critically, creates a BRAND
    NEW SessionIdentifiers class object -- so any file that already captured
    the old class reference before the reload would then fail isinstance()
    checks against instances produced after it (a real hazard: this bit us
    when renaming this file made it collect, and reload order_state, before
    test_order_state.py collects and captures its own reference).

    Loading a private copy here instead means happy-hour re-testing under a
    reloaded conformance_hooks never touches the shared order_state module
    that the rest of the suite relies on. order_state.py itself does
    `import conformance_hooks` (a live module reference, not a bound copy), so
    this private copy still reads whatever conformance_hooks.now()/HOOKS_ENABLED
    currently are on the shared, already-reloaded conformance_hooks module.
    """
    import importlib.util

    spec = importlib.util.spec_from_file_location(
        "conformance_hooks_test_private_order_state",
        Path(__file__).resolve().parents[1] / "order_state.py",
    )
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


@pytest.fixture(autouse=True)
def _reset_conformance_hooks_module(monkeypatch):
    """Every test starts and ends with a freshly-imported, disabled
    conformance_hooks module, regardless of what a previous test (or the
    ambient shell) left in the environment. HOOKS_ENABLED is a module-level
    constant read once at import time, so env changes only take effect after
    reload()."""
    monkeypatch.delenv("CONFORMANCE_TEST_HOOKS", raising=False)
    monkeypatch.delenv("CONFORMANCE_FIXED_NOW", raising=False)
    importlib.reload(conformance_hooks)
    yield
    monkeypatch.delenv("CONFORMANCE_TEST_HOOKS", raising=False)
    monkeypatch.delenv("CONFORMANCE_FIXED_NOW", raising=False)
    importlib.reload(conformance_hooks)


def _enable(monkeypatch, **env):
    monkeypatch.setenv("CONFORMANCE_TEST_HOOKS", "1")
    for key, value in env.items():
        monkeypatch.setenv(key, value)
    importlib.reload(conformance_hooks)


class TestInertWhenUnset:
    """CONFORMANCE_TEST_HOOKS unset (or not the literal "1") -> real clock,
    real timer defaults, no matter what the override env vars say."""

    def test_hooks_disabled_by_default(self):
        assert conformance_hooks.HOOKS_ENABLED is False

    def test_now_ignores_fixed_now_when_disabled(self, monkeypatch):
        monkeypatch.setenv("CONFORMANCE_FIXED_NOW", "2020-01-01T00:00:00-06:00")
        importlib.reload(conformance_hooks)
        before = datetime.now(_TZ)
        result = conformance_hooks.now(_TZ)
        after = datetime.now(_TZ)
        assert before <= result <= after + timedelta(seconds=5)
        assert result.year != 2020

    def test_seconds_ignores_override_when_disabled(self, monkeypatch):
        monkeypatch.setenv("CONFORMANCE_IDLE_TIMEOUT_SECONDS", "1")
        importlib.reload(conformance_hooks)
        assert conformance_hooks.seconds("CONFORMANCE_IDLE_TIMEOUT_SECONDS", 300) == 300

    @pytest.mark.parametrize("flag_value", ["0", "false", "yes", "TRUE", ""])
    def test_only_the_literal_value_1_enables_hooks(self, monkeypatch, flag_value):
        # Anything other than the string "1" must leave the hooks disabled --
        # a truthy-string bug here would be easy to accidentally trip in CI.
        monkeypatch.setenv("CONFORMANCE_TEST_HOOKS", flag_value)
        importlib.reload(conformance_hooks)
        assert conformance_hooks.HOOKS_ENABLED is False

    def test_malformed_fixed_now_does_not_raise_when_hooks_disabled(self, monkeypatch):
        # A malformed CONFORMANCE_FIXED_NOW is only a startup-validation
        # concern when hooks are actually enabled -- otherwise it's simply
        # ignored, same as any other unused env var.
        monkeypatch.setenv("CONFORMANCE_FIXED_NOW", "not-a-timestamp-at-all")
        importlib.reload(conformance_hooks)  # must not raise
        result = conformance_hooks.now(_TZ)
        assert result.tzinfo is not None


class TestActiveWhenSet:
    """CONFORMANCE_TEST_HOOKS=1 -> the fixed clock and timer overrides take
    effect. Exercising this positive path is what makes the inertness tests
    above a real mutation check: a stub that always takes the disabled branch
    would still pass every TestInertWhenUnset test, but would fail these."""

    def test_hooks_enabled_flag(self, monkeypatch):
        _enable(monkeypatch)
        assert conformance_hooks.HOOKS_ENABLED is True

    def test_now_honours_fixed_now(self, monkeypatch):
        _enable(monkeypatch, CONFORMANCE_FIXED_NOW="2026-07-04T15:30:00-05:00")
        result = conformance_hooks.now(_TZ)
        assert result == datetime(2026, 7, 4, 15, 30, 0, tzinfo=_TZ)

    def test_now_converts_fixed_now_into_the_requested_zone(self, monkeypatch):
        _enable(monkeypatch, CONFORMANCE_FIXED_NOW="2026-07-04T20:30:00+00:00")
        result = conformance_hooks.now(_TZ)
        assert result.hour == 15  # 20:30 UTC == 15:30 America/Chicago (CDT, UTC-5)

    def test_now_falls_back_to_real_clock_when_fixed_now_unset(self, monkeypatch):
        _enable(monkeypatch)
        before = datetime.now(_TZ)
        result = conformance_hooks.now(_TZ)
        after = datetime.now(_TZ)
        assert before <= result <= after + timedelta(seconds=5)

    def test_seconds_honours_override(self, monkeypatch):
        _enable(monkeypatch, CONFORMANCE_IDLE_TIMEOUT_SECONDS="3")
        assert conformance_hooks.seconds("CONFORMANCE_IDLE_TIMEOUT_SECONDS", 300) == 3.0

    def test_seconds_falls_back_on_unparseable_override(self, monkeypatch):
        _enable(monkeypatch, CONFORMANCE_IDLE_TIMEOUT_SECONDS="not-a-number")
        assert conformance_hooks.seconds("CONFORMANCE_IDLE_TIMEOUT_SECONDS", 300) == 300

    def test_seconds_falls_back_when_override_unset(self, monkeypatch):
        _enable(monkeypatch)
        assert conformance_hooks.seconds("CONFORMANCE_IDLE_TIMEOUT_SECONDS", 300) == 300


class TestStartupValidationFailsFast:
    """PR #22 review item 14: a malformed CONFORMANCE_FIXED_NOW must fail the
    moment hooks are enabled (at import/reload time), not lazily the first
    time some downstream business-logic code happens to call now()."""

    def test_naive_fixed_now_raises_at_import_time_not_at_first_use(self, monkeypatch):
        monkeypatch.setenv("CONFORMANCE_TEST_HOOKS", "1")
        monkeypatch.setenv("CONFORMANCE_FIXED_NOW", "2026-07-04T15:30:00")  # no offset/zone
        with pytest.raises(ValueError, match="CONFORMANCE_FIXED_NOW"):
            importlib.reload(conformance_hooks)  # the raise must happen HERE

    def test_unparseable_fixed_now_raises_at_import_time(self, monkeypatch):
        monkeypatch.setenv("CONFORMANCE_TEST_HOOKS", "1")
        monkeypatch.setenv("CONFORMANCE_FIXED_NOW", "definitely-not-a-timestamp")
        with pytest.raises(ValueError):
            importlib.reload(conformance_hooks)

    def test_valid_fixed_now_does_not_raise_at_import_time(self, monkeypatch):
        monkeypatch.setenv("CONFORMANCE_TEST_HOOKS", "1")
        monkeypatch.setenv("CONFORMANCE_FIXED_NOW", "2026-07-04T15:30:00-05:00")
        importlib.reload(conformance_hooks)  # must not raise
        assert conformance_hooks.HOOKS_ENABLED is True

    def test_enabled_with_no_fixed_now_does_not_raise_at_import_time(self, monkeypatch):
        # CONFORMANCE_FIXED_NOW is optional -- only timer overrides might be
        # in play (e.g. the ShortTimers profile), so its absence must never
        # be treated as a startup-validation failure.
        monkeypatch.setenv("CONFORMANCE_TEST_HOOKS", "1")
        importlib.reload(conformance_hooks)  # must not raise
        assert conformance_hooks.HOOKS_ENABLED is True

    def test_enabled_logs_a_warning(self, monkeypatch, caplog):
        import logging

        with caplog.at_level(logging.WARNING, logger="sonic-drive-in"):
            _enable(monkeypatch)
        assert any("CONFORMANCE_TEST_HOOKS" in record.getMessage() for record in caplog.records)


class TestHappyHourIntegration:
    """order_state.is_happy_hour() is the primary consumer named in issue #7;
    prove the wiring is inert there too, not just in conformance_hooks.py
    itself."""

    def test_happy_hour_honours_fixed_clock_when_enabled(self, monkeypatch):
        _enable(monkeypatch, CONFORMANCE_FIXED_NOW="2026-07-04T15:00:00-05:00")
        order_state = _isolated_order_state()
        try:
            # 15:00 store-local is inside the default 14-16 happy hour window.
            assert order_state.is_happy_hour() is True
        finally:
            monkeypatch.delenv("CONFORMANCE_TEST_HOOKS", raising=False)
            monkeypatch.delenv("CONFORMANCE_FIXED_NOW", raising=False)
            importlib.reload(conformance_hooks)

    def test_happy_hour_ignores_fixed_clock_when_disabled(self, monkeypatch):
        # Hooks disabled: even though CONFORMANCE_FIXED_NOW is set (and would
        # force happy-hour-on if honoured), is_happy_hour() must fall back to
        # the real wall clock -- computed here independently so the assertion
        # is deterministic regardless of the real time of day.
        monkeypatch.setenv("CONFORMANCE_FIXED_NOW", "2026-07-04T15:00:00-05:00")
        importlib.reload(conformance_hooks)
        order_state = _isolated_order_state()
        try:
            now = datetime.now(order_state._STORE_TZ)
            start = order_state._biz_cfg.get("happy_hour_start", 14)
            end = order_state._biz_cfg.get("happy_hour_end", 16)
            expected = start <= now.hour < end
            assert order_state.is_happy_hour() == expected
        finally:
            monkeypatch.delenv("CONFORMANCE_FIXED_NOW", raising=False)
            importlib.reload(conformance_hooks)


class TestNeverInInfraOrDockerfile:
    """PR #22 review item 14: guard that CONFORMANCE_ never appears in infra/
    (bicep), the Dockerfile, or azure.yaml -- these hooks must only ever be
    set by the conformance harness's own child-process environment
    (tests/conformance's BackendEnvironment), never in a deployed
    environment. Mutation-checked: temporarily add a `CONFORMANCE_FOO: "1"`
    line to any of the scanned files and this test must fail; remove it and
    the test goes green again.
    """

    def _scanned_files(self):
        infra_dir = _REPO_ROOT / "infra"
        assert infra_dir.is_dir(), f"expected {infra_dir} to exist"
        files = sorted(infra_dir.rglob("*"))
        files = [f for f in files if f.is_file()]
        assert files, f"expected at least one file under {infra_dir}"

        dockerfile = _REPO_ROOT / "app" / "Dockerfile"
        assert dockerfile.is_file(), f"expected {dockerfile} to exist"
        files.append(dockerfile)

        azure_yaml = _REPO_ROOT / "azure.yaml"
        assert azure_yaml.is_file(), f"expected {azure_yaml} to exist"
        files.append(azure_yaml)

        return files

    def test_conformance_prefix_never_appears_in_infra_dockerfile_or_azure_yaml(self):
        offenders = []
        for path in self._scanned_files():
            text = path.read_text(encoding="utf-8", errors="strict")
            if "CONFORMANCE_" in text:
                offenders.append(str(path.relative_to(_REPO_ROOT)))
        assert offenders == [], (
            "CONFORMANCE_* test hooks must never be set in infra/Dockerfile/azure.yaml "
            f"(found the literal 'CONFORMANCE_' in): {offenders}"
        )
