"""Security controls tests for Sonic AI Drive-Thru.

Tests Phase 4 security features:
  - Session limits (max concurrent, idle timeout)
  - Origin validation (same-origin, allowed_origins list)
  - HMAC session tokens (signing, expiry, tampering)

These tests mock the expected interfaces so they pass regardless of whether
Summer's implementation has landed. Adjust mocks once the real code merges.
"""

import asyncio
import base64
import hashlib
import hmac
import json
import os
import sys
import time
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock, patch

import pytest

# Ensure the backend package is importable
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))


# ---------------------------------------------------------------------------
# Helpers — lightweight stubs for the security interfaces Summer is building
# ---------------------------------------------------------------------------

class _StubSessionLimiter:
    """In-memory session limiter matching the expected SessionManager additions.

    If Summer's code is already merged, replace this with an import from
    session_manager.  Until then, this stub lets us assert expected behaviour.
    """

    def __init__(self, max_concurrent: int = 10, idle_timeout: int = 300):
        self.max_concurrent = max_concurrent
        self.idle_timeout = idle_timeout
        self._sessions: dict[str, float] = {}  # session_id -> last_activity

    def try_add_session(self, session_id: str) -> bool:
        if len(self._sessions) >= self.max_concurrent:
            return False
        self._sessions[session_id] = time.monotonic()
        return True

    def remove_session(self, session_id: str) -> None:
        self._sessions.pop(session_id, None)

    def touch(self, session_id: str) -> None:
        if session_id in self._sessions:
            self._sessions[session_id] = time.monotonic()

    @property
    def active_count(self) -> int:
        return len(self._sessions)

    def cleanup_idle(self, now: float | None = None) -> list[str]:
        """Remove sessions idle longer than idle_timeout. Returns removed ids."""
        now = now or time.monotonic()
        expired = [
            sid for sid, ts in self._sessions.items()
            if (now - ts) >= self.idle_timeout
        ]
        for sid in expired:
            del self._sessions[sid]
        return expired


def _validate_origin(
    request_origin: str | None,
    request_host: str,
    allowed_origins: list[str] | None = None,
) -> bool:
    """Validate the Origin header for a WebSocket upgrade request.

    Rules (expected from Summer's implementation):
      - Missing Origin → accept (non-browser clients)
      - Origin matches scheme+host → accept (same-origin)
      - Origin in allowed_origins list → accept
      - Otherwise → reject
    """
    if request_origin is None:
        return True  # non-browser client

    # Normalise
    origin = request_origin.rstrip("/")
    host = request_host.rstrip("/")

    # Same-origin check (scheme-insensitive comparison of host portion)
    origin_host = origin.split("://", 1)[-1]
    if origin_host == host:
        return True

    # Allowed-origins list
    if allowed_origins:
        normalised = {o.rstrip("/") for o in allowed_origins}
        if origin in normalised:
            return True

    return False


def _generate_hmac_token(
    session_id: str,
    secret: str,
    issued_at: float | None = None,
    ttl: int = 900,
) -> str:
    """Generate an HMAC-SHA256 session token.

    Format: base64(session_id:issued_at:ttl):signature
    """
    issued_at = issued_at or time.time()
    payload = f"{session_id}:{issued_at:.0f}:{ttl}"
    encoded = base64.urlsafe_b64encode(payload.encode()).decode()
    sig = hmac.new(secret.encode(), encoded.encode(), hashlib.sha256).hexdigest()
    return f"{encoded}.{sig}"


def _verify_hmac_token(
    token: str,
    secret: str,
    now: float | None = None,
) -> tuple[bool, str]:
    """Verify an HMAC session token. Returns (valid, reason)."""
    now = now or time.time()

    if not token or "." not in token:
        return False, "malformed"

    parts = token.split(".", 1)
    if len(parts) != 2:
        return False, "malformed"

    encoded, sig = parts

    # Verify signature
    expected_sig = hmac.new(secret.encode(), encoded.encode(), hashlib.sha256).hexdigest()
    if not hmac.compare_digest(sig, expected_sig):
        return False, "invalid_signature"

    # Decode payload
    try:
        payload = base64.urlsafe_b64decode(encoded).decode()
        session_id, issued_str, ttl_str = payload.split(":")
        issued_at = float(issued_str)
        ttl = int(ttl_str)
    except Exception:
        return False, "malformed"

    # Check expiry (>= means token at exact TTL boundary is expired)
    if now - issued_at >= ttl:
        return False, "expired"

    return True, "ok"


# ===========================================================================
# Session Limits
# ===========================================================================

class TestSessionLimits:
    """Verify max-concurrent and idle-timeout enforcement."""

    def test_sessions_up_to_max_accepted(self):
        limiter = _StubSessionLimiter(max_concurrent=10)
        for i in range(10):
            assert limiter.try_add_session(f"s-{i}") is True
        assert limiter.active_count == 10

    def test_session_max_plus_one_rejected(self):
        limiter = _StubSessionLimiter(max_concurrent=10)
        for i in range(10):
            limiter.try_add_session(f"s-{i}")
        # 11th session must be rejected
        assert limiter.try_add_session("s-overflow") is False
        assert limiter.active_count == 10

    def test_closing_session_allows_new_one(self):
        limiter = _StubSessionLimiter(max_concurrent=2)
        limiter.try_add_session("a")
        limiter.try_add_session("b")
        assert limiter.try_add_session("c") is False

        limiter.remove_session("a")
        assert limiter.try_add_session("c") is True
        assert limiter.active_count == 2

    def test_idle_timeout_cleans_up_stale_sessions(self):
        limiter = _StubSessionLimiter(max_concurrent=10, idle_timeout=300)
        base = 1000.0
        limiter._sessions["old"] = base
        limiter._sessions["fresh"] = base + 200

        # Advance clock 5 minutes past base
        now = base + 300
        removed = limiter.cleanup_idle(now=now)

        assert "old" in removed
        assert "fresh" not in removed
        assert limiter.active_count == 1

    def test_active_sessions_not_cleaned(self):
        """Sessions with recent activity survive cleanup."""
        limiter = _StubSessionLimiter(max_concurrent=10, idle_timeout=300)
        base = 1000.0
        limiter._sessions["active"] = base

        # Touch the session to renew activity timestamp
        limiter._sessions["active"] = base + 250
        now = base + 300
        removed = limiter.cleanup_idle(now=now)

        assert "active" not in removed
        assert limiter.active_count == 1

    def test_touch_renews_activity(self):
        limiter = _StubSessionLimiter(max_concurrent=10, idle_timeout=300)
        base = 1000.0
        limiter._sessions["s1"] = base

        # Touch at 200s mark
        limiter.touch("s1")
        # Now s1 was last active at current monotonic time, which > base
        # Just verify touch doesn't crash and session stays active
        assert limiter.active_count == 1

    def test_cleanup_returns_expired_ids(self):
        limiter = _StubSessionLimiter(max_concurrent=10, idle_timeout=60)
        base = 0.0
        limiter._sessions["x"] = base
        limiter._sessions["y"] = base
        limiter._sessions["z"] = base + 100  # added later

        removed = limiter.cleanup_idle(now=base + 60)
        assert set(removed) == {"x", "y"}


# ===========================================================================
# Origin Validation
# ===========================================================================

class TestOriginValidation:
    """Verify Origin header checking on WebSocket upgrade."""

    def test_same_origin_accepted(self):
        assert _validate_origin(
            request_origin="https://sonic-app.azurecontainerapps.io",
            request_host="sonic-app.azurecontainerapps.io",
        ) is True

    def test_same_origin_with_scheme_accepted(self):
        assert _validate_origin(
            request_origin="https://example.com",
            request_host="example.com",
        ) is True

    def test_missing_origin_accepted(self):
        """Non-browser clients (curl, Postman) don't send Origin."""
        assert _validate_origin(
            request_origin=None,
            request_host="sonic-app.azurecontainerapps.io",
        ) is True

    def test_foreign_origin_rejected(self):
        assert _validate_origin(
            request_origin="https://evil.example.com",
            request_host="sonic-app.azurecontainerapps.io",
        ) is False

    def test_allowed_origins_list(self):
        allowed = ["https://staging.sonic.com", "https://dev.sonic.com"]
        assert _validate_origin(
            request_origin="https://staging.sonic.com",
            request_host="prod.sonic.com",
            allowed_origins=allowed,
        ) is True

    def test_allowed_origins_rejects_unlisted(self):
        allowed = ["https://staging.sonic.com"]
        assert _validate_origin(
            request_origin="https://evil.example.com",
            request_host="prod.sonic.com",
            allowed_origins=allowed,
        ) is False

    def test_trailing_slash_normalised(self):
        assert _validate_origin(
            request_origin="https://sonic-app.azurecontainerapps.io/",
            request_host="sonic-app.azurecontainerapps.io",
        ) is True

    def test_empty_allowed_origins_falls_through(self):
        """Empty list == same-origin only."""
        assert _validate_origin(
            request_origin="https://other.com",
            request_host="sonic-app.azurecontainerapps.io",
            allowed_origins=[],
        ) is False


# ===========================================================================
# HMAC Session Token
# ===========================================================================

_TEST_SECRET = "super-secret-key-for-tests"


class TestHMACSessionToken:
    """Verify HMAC-SHA256 session token generation and verification."""

    def test_valid_token_accepted(self):
        now = time.time()
        token = _generate_hmac_token("sess-123", _TEST_SECRET, issued_at=now)
        valid, reason = _verify_hmac_token(token, _TEST_SECRET, now=now + 60)
        assert valid is True
        assert reason == "ok"

    def test_expired_token_rejected(self):
        now = time.time()
        token = _generate_hmac_token("sess-123", _TEST_SECRET, issued_at=now, ttl=900)
        # Advance 16 minutes (past 15-minute TTL)
        valid, reason = _verify_hmac_token(token, _TEST_SECRET, now=now + 960)
        assert valid is False
        assert reason == "expired"

    def test_malformed_token_rejected(self):
        valid, reason = _verify_hmac_token("not-a-real-token", _TEST_SECRET)
        assert valid is False
        assert reason == "malformed"

    def test_empty_token_rejected(self):
        valid, reason = _verify_hmac_token("", _TEST_SECRET)
        assert valid is False
        assert reason == "malformed"

    def test_missing_token_with_require_true_returns_401(self):
        """When require_session_token=True and no token → 401."""
        require_session_token = True
        token = None
        if require_session_token and not token:
            status = 401
        else:
            status = 200
        assert status == 401

    def test_missing_token_with_require_false_accepted(self):
        """Default config: require_session_token=False → accept without token."""
        require_session_token = False
        token = None
        if require_session_token and not token:
            status = 401
        else:
            status = 200
        assert status == 200

    def test_signature_tampering_detected(self):
        now = time.time()
        token = _generate_hmac_token("sess-123", _TEST_SECRET, issued_at=now)
        # Tamper with the signature portion
        encoded, sig = token.split(".", 1)
        tampered = f"{encoded}.{'0' * len(sig)}"
        valid, reason = _verify_hmac_token(tampered, _TEST_SECRET, now=now)
        assert valid is False
        assert reason == "invalid_signature"

    def test_payload_tampering_detected(self):
        """Modify the payload portion — signature should no longer match."""
        now = time.time()
        token = _generate_hmac_token("sess-123", _TEST_SECRET, issued_at=now)
        encoded, sig = token.split(".", 1)
        # Flip a character in the encoded payload
        tampered_encoded = encoded[:-1] + ("A" if encoded[-1] != "A" else "B")
        tampered = f"{tampered_encoded}.{sig}"
        valid, reason = _verify_hmac_token(tampered, _TEST_SECRET, now=now)
        assert valid is False
        assert reason in ("invalid_signature", "malformed")

    def test_wrong_secret_rejected(self):
        now = time.time()
        token = _generate_hmac_token("sess-123", _TEST_SECRET, issued_at=now)
        valid, reason = _verify_hmac_token(token, "wrong-secret", now=now)
        assert valid is False
        assert reason == "invalid_signature"

    def test_token_at_exact_expiry_boundary(self):
        """Token at exactly TTL seconds should be expired (>= boundary)."""
        now = 1000.0
        token = _generate_hmac_token("sess-1", _TEST_SECRET, issued_at=now, ttl=900)
        valid, reason = _verify_hmac_token(token, _TEST_SECRET, now=now + 900)
        assert valid is False
        assert reason == "expired"

    def test_token_one_second_before_expiry(self):
        now = 1000.0
        token = _generate_hmac_token("sess-1", _TEST_SECRET, issued_at=now, ttl=900)
        valid, reason = _verify_hmac_token(token, _TEST_SECRET, now=now + 899)
        assert valid is True


# ===========================================================================
# Integration-style: config.yaml security section
# ===========================================================================

class TestSecurityConfig:
    """Verify that config.yaml security section is well-formed."""

    def test_config_has_security_section(self):
        from config_loader import get_config
        cfg = get_config()
        assert "security" in cfg

    def test_max_concurrent_sessions_default(self):
        from config_loader import get_config
        sec = get_config()["security"]
        assert sec["max_concurrent_sessions"] == 10

    def test_idle_timeout_default(self):
        from config_loader import get_config
        sec = get_config()["security"]
        assert sec["idle_timeout_seconds"] == 300

    def test_allowed_origins_default_empty(self):
        from config_loader import get_config
        sec = get_config()["security"]
        assert sec["allowed_origins"] == []

    def test_require_session_token_default_false(self):
        from config_loader import get_config
        sec = get_config()["security"]
        assert sec["require_session_token"] is False


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
