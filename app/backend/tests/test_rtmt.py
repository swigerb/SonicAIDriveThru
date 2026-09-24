"""Tests for the real-time middle tier (rtmt.py), session manager (session_manager.py),
and audio pipeline (audio_pipeline.py).

Covers WebSocket lifecycle, session management, message routing, echo suppression,
and error recovery — all with mocked external services (no real OpenAI/Azure calls).
"""

import asyncio
import json
import re
import sys
import time
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock, patch

sys.path.append(str(Path(__file__).resolve().parents[1]))

from aiohttp import web
from azure.core.credentials import AzureKeyCredential

from audio_pipeline import (
    _GA_TO_LEGACY_EVENTS,
    _PASSTHROUGH_CLIENT_TYPES,
    _PASSTHROUGH_SERVER_TYPES,
    ECHO_COOLDOWN_SEC,
    INPUT_AUDIO_CLEAR_MSG,
    RESPONSE_CREATE_MSG,
    TYPE_RE,
    EchoSuppressor,
)
from order_state import order_state_singleton
from rtmt import (
    RTMiddleTier,
    RTToolCall,
    Tool,
    ToolResult,
    ToolResultDirection,
    _drop_from_client,
    _origin_matches_host,
    _to_ga_session,
    create_hmac_token,
    validate_hmac_token,
)

# ── Imports under test ──
from session_manager import (
    MIDDLE_TIER_ITEM_ID_PREFIX,
    ContextMonitor,
    SessionManager,
    new_middle_tier_item_id,
)

# ── Helpers ──

def _make_mock_ws():
    """Create a mock WebSocket response with required attributes."""
    ws = MagicMock(spec=web.WebSocketResponse)
    ws.closed = False
    ws.send_json = AsyncMock()
    ws.send_str = AsyncMock()
    ws.close = AsyncMock()
    return ws


# ═══════════════════════════════════════════════════════════════════════════════
# SESSION MANAGER TESTS
# ═══════════════════════════════════════════════════════════════════════════════

class SessionManagerCreationTests(unittest.TestCase):
    """Test session creation, tracking, and ID uniqueness."""

    def setUp(self):
        order_state_singleton.sessions = {}
        self.sm = SessionManager()

    def test_create_session_returns_uuid(self):
        ws = _make_mock_ws()
        sid = self.sm.create_session(ws)
        self.assertIsInstance(sid, str)
        self.assertGreater(len(sid), 10)

    def test_create_session_maps_ws_to_session(self):
        ws = _make_mock_ws()
        sid = self.sm.create_session(ws)
        self.assertEqual(self.sm.get_session_id(ws), sid)

    def test_session_id_uniqueness(self):
        ids = set()
        for _ in range(20):
            ws = _make_mock_ws()
            sid = self.sm.create_session(ws)
            ids.add(sid)
        self.assertEqual(len(ids), 20)

    def test_active_session_count_tracks_correctly(self):
        self.assertEqual(self.sm.active_session_count, 0)
        ws1 = _make_mock_ws()
        ws2 = _make_mock_ws()
        self.sm.create_session(ws1)
        self.assertEqual(self.sm.active_session_count, 1)
        self.sm.create_session(ws2)
        self.assertEqual(self.sm.active_session_count, 2)

    def test_get_session_id_for_unknown_ws_returns_none(self):
        ws = _make_mock_ws()
        self.assertIsNone(self.sm.get_session_id(ws))


class SessionManagerCleanupTests(unittest.TestCase):
    """Test cleanup frees all resources."""

    def setUp(self):
        order_state_singleton.sessions = {}
        self.sm = SessionManager()

    def test_cleanup_removes_session(self):
        ws = _make_mock_ws()
        sid = self.sm.create_session(ws)
        self.sm.cleanup_session(ws, sid)
        self.assertEqual(self.sm.active_session_count, 0)
        self.assertIsNone(self.sm.get_session_id(ws))

    def test_cleanup_removes_order_state(self):
        ws = _make_mock_ws()
        sid = self.sm.create_session(ws)
        self.assertIn(sid, order_state_singleton.sessions)
        self.sm.cleanup_session(ws, sid)
        self.assertNotIn(sid, order_state_singleton.sessions)

    def test_cleanup_clears_greeting_state(self):
        ws = _make_mock_ws()
        sid = self.sm.create_session(ws)
        self.sm.mark_greeting_sent(sid)
        self.assertTrue(self.sm.has_sent_greeting(sid))
        self.sm.cleanup_session(ws, sid)
        self.assertFalse(self.sm.has_sent_greeting(sid))

    def test_cleanup_clears_context_monitor(self):
        ws = _make_mock_ws()
        sid = self.sm.create_session(ws)
        self.assertIsNotNone(self.sm.get_context_monitor(sid))
        self.sm.cleanup_session(ws, sid)
        self.assertIsNone(self.sm.get_context_monitor(sid))

    def test_cleanup_with_none_session_id_is_safe(self):
        ws = _make_mock_ws()
        self.sm.cleanup_session(ws, None)  # should not raise


class SessionManagerConcurrencyTests(unittest.TestCase):
    """Test concurrent session limits."""

    def setUp(self):
        order_state_singleton.sessions = {}
        self.sm = SessionManager()

    @patch("session_manager._MAX_CONCURRENT_SESSIONS", 3)
    def test_can_accept_session_within_limit(self):
        sm = SessionManager()
        for _ in range(3):
            ws = _make_mock_ws()
            sm.create_session(ws)
        self.assertFalse(sm.can_accept_session())

    @patch("session_manager._MAX_CONCURRENT_SESSIONS", 3)
    def test_can_accept_session_after_cleanup(self):
        sm = SessionManager()
        sessions = []
        for _ in range(3):
            ws = _make_mock_ws()
            sid = sm.create_session(ws)
            sessions.append((ws, sid))
        self.assertFalse(sm.can_accept_session())
        sm.cleanup_session(*sessions[0])
        self.assertTrue(sm.can_accept_session())

    def test_multiple_concurrent_connections_independent(self):
        ws1, ws2 = _make_mock_ws(), _make_mock_ws()
        sid1 = self.sm.create_session(ws1)
        sid2 = self.sm.create_session(ws2)
        self.assertNotEqual(sid1, sid2)
        self.assertEqual(self.sm.get_session_id(ws1), sid1)
        self.assertEqual(self.sm.get_session_id(ws2), sid2)


class SessionManagerGreetingTests(unittest.TestCase):
    """Test greeting state tracking per session."""

    def setUp(self):
        order_state_singleton.sessions = {}
        self.sm = SessionManager()

    def test_greeting_not_sent_initially(self):
        ws = _make_mock_ws()
        sid = self.sm.create_session(ws)
        self.assertFalse(self.sm.has_sent_greeting(sid))

    def test_mark_greeting_sent(self):
        ws = _make_mock_ws()
        sid = self.sm.create_session(ws)
        self.sm.mark_greeting_sent(sid)
        self.assertTrue(self.sm.has_sent_greeting(sid))

    def test_greeting_state_per_session(self):
        ws1, ws2 = _make_mock_ws(), _make_mock_ws()
        sid1 = self.sm.create_session(ws1)
        sid2 = self.sm.create_session(ws2)
        self.sm.mark_greeting_sent(sid1)
        self.assertTrue(self.sm.has_sent_greeting(sid1))
        self.assertFalse(self.sm.has_sent_greeting(sid2))

    def test_greeting_msg_default(self):
        msg = json.loads(self.sm.greeting_msg)
        self.assertEqual(msg["type"], "conversation.item.create")

    def test_greeting_msg_default_carries_middle_tier_item_id(self):
        """swigerb/SonicAIDriveThru#29 follow-up (PR #30 review "S1"): the
        greeting is role="user", so a role-based drop in rtmt.py can never
        catch it -- it must be identifiable by authorship (item id prefix)
        instead."""
        msg = json.loads(self.sm.greeting_msg)
        self.assertEqual(msg["item"]["role"], "user")
        self.assertTrue(msg["item"]["id"].startswith(MIDDLE_TIER_ITEM_ID_PREFIX))

    def test_greeting_msg_id_is_stable_across_reads(self):
        """The id is baked in once at construction time, not regenerated per
        read -- session_manager.py's own exact-dict-equality tests
        (test_order_resume.py) compare two independent reads of greeting_msg
        against each other and against what was actually sent upstream."""
        self.assertEqual(self.sm.greeting_msg, self.sm.greeting_msg)

    def test_greeting_msg_from_prompt_loader(self):
        loader = MagicMock()
        loader.get_greeting_json_str.return_value = '{"type":"custom_greeting"}'
        sm = SessionManager(prompt_loader=loader)
        self.assertEqual(sm.greeting_msg, '{"type":"custom_greeting"}')

    def test_greeting_msg_from_prompt_loader_with_item_gets_id_injected(self):
        loader = MagicMock()
        loader.get_greeting_json_str.return_value = json.dumps({
            "type": "conversation.item.create",
            "item": {"type": "message", "role": "user", "content": []},
        })
        sm = SessionManager(prompt_loader=loader)
        msg = json.loads(sm.greeting_msg)
        self.assertTrue(msg["item"]["id"].startswith(MIDDLE_TIER_ITEM_ID_PREFIX))


class MiddleTierItemIdTests(unittest.TestCase):
    """swigerb/SonicAIDriveThru#29 follow-up (PR #30 review "S1"/"M1")."""

    def test_new_middle_tier_item_id_carries_prefix(self):
        self.assertTrue(new_middle_tier_item_id().startswith(MIDDLE_TIER_ITEM_ID_PREFIX))

    def test_new_middle_tier_item_id_is_unique_per_call(self):
        self.assertNotEqual(new_middle_tier_item_id(), new_middle_tier_item_id())


class SessionManagerIdleTimeoutTests(unittest.TestCase):
    """Test idle timeout detection with mocked time."""

    def setUp(self):
        order_state_singleton.sessions = {}
        self.sm = SessionManager()

    def test_touch_activity_updates_timestamp(self):
        ws = _make_mock_ws()
        sid = self.sm.create_session(ws)
        t1 = self.sm._last_activity[sid]
        time.sleep(0.05)
        self.sm.touch_activity(sid)
        t2 = self.sm._last_activity[sid]
        self.assertGreaterEqual(t2, t1)

    def test_idle_session_closed(self):
        with patch("session_manager._IDLE_TIMEOUT_SECONDS", 0):
            sm = SessionManager()
            ws = _make_mock_ws()
            sid = sm.create_session(ws)
            # Backdate activity so it appears idle
            sm._last_activity[sid] = time.monotonic() - 10
            asyncio.run(sm.close_idle_sessions())
            ws.close.assert_called_once()
            self.assertEqual(sm.active_session_count, 0)

    def test_active_session_not_cleaned(self):
        with patch("session_manager._IDLE_TIMEOUT_SECONDS", 9999):
            sm = SessionManager()
            ws = _make_mock_ws()
            sid = sm.create_session(ws)
            sm.touch_activity(sid)
            asyncio.run(sm.close_idle_sessions())
            ws.close.assert_not_called()
            self.assertEqual(sm.active_session_count, 1)


class SessionManagerEmitIdentifiersTests(unittest.IsolatedAsyncioTestCase):
    """Test session identifier emission."""

    def setUp(self):
        order_state_singleton.sessions = {}
        self.sm = SessionManager()

    async def test_emit_session_identifiers(self):
        ws = _make_mock_ws()
        sid = self.sm.create_session(ws)
        identifiers = order_state_singleton.get_session_identifiers(sid)
        await self.sm.emit_session_identifiers(ws, "extension.session_metadata", identifiers)
        ws.send_json.assert_called_once()
        payload = ws.send_json.call_args[0][0]
        self.assertEqual(payload["type"], "extension.session_metadata")
        self.assertIn("sessionToken", payload)

    async def test_emit_none_identifiers_is_noop(self):
        ws = _make_mock_ws()
        await self.sm.emit_session_identifiers(ws, "test", None)
        ws.send_json.assert_not_called()


# ═══════════════════════════════════════════════════════════════════════════════
# CONTEXT MONITOR TESTS
# ═══════════════════════════════════════════════════════════════════════════════

class ContextMonitorTests(unittest.TestCase):
    """Test context window token estimation and threshold warnings."""

    def test_initial_state(self):
        cm = ContextMonitor("test-session")
        self.assertEqual(cm.estimated_tokens, 0)
        self.assertAlmostEqual(cm.usage_pct, 0.0)

    def test_add_content_increases_token_estimate(self):
        cm = ContextMonitor("test-session")
        cm.add_content("a" * 400)  # ~100 tokens
        self.assertEqual(cm.estimated_tokens, 100)

    def test_add_empty_content_is_safe(self):
        cm = ContextMonitor("test-session")
        cm.add_content("")
        cm.add_content(None)
        self.assertEqual(cm.estimated_tokens, 0)

    def test_warning_threshold_logged(self):
        with patch("session_manager._CTX_MAX_TOKENS", 100):
            cm = ContextMonitor("test-session")
            with self.assertLogs("sonic-drive-in", level="WARNING") as log:
                cm.add_content("a" * 400)  # 100 tokens = 100% of 100 max
            self.assertTrue(any("CRITICAL" in m for m in log.output))


# ═══════════════════════════════════════════════════════════════════════════════
# ECHO SUPPRESSOR TESTS
# ═══════════════════════════════════════════════════════════════════════════════

class EchoSuppressorTests(unittest.TestCase):
    """Test the echo suppression state machine."""

    def test_initial_state_not_suppressing(self):
        echo = EchoSuppressor()
        self.assertFalse(echo.ai_speaking)
        self.assertFalse(echo.greeting_in_progress)
        self.assertFalse(echo.should_suppress_audio(0.0))

    def test_audio_delta_activates_suppression(self):
        echo = EchoSuppressor()
        echo.on_audio_delta()
        self.assertTrue(echo.ai_speaking)
        self.assertTrue(echo.should_suppress_audio(0.0))

    def test_audio_done_deactivates_speaking_starts_cooldown(self):
        async def _run():
            echo = EchoSuppressor()
            echo.on_audio_delta()
            loop = asyncio.get_running_loop()
            target_ws = MagicMock()
            target_ws.closed = False
            target_ws.send_str = AsyncMock()
            t = loop.time()
            echo.on_audio_done(loop, target_ws)
            self.assertFalse(echo.ai_speaking)
            self.assertAlmostEqual(echo.cooldown_end, t + ECHO_COOLDOWN_SEC, delta=0.1)
        asyncio.run(_run())

    def test_cooldown_suppresses_audio(self):
        echo = EchoSuppressor()
        echo.cooldown_end = 200.0
        self.assertTrue(echo.should_suppress_audio(199.0))
        self.assertFalse(echo.should_suppress_audio(201.0))

    def test_speech_started_resets_suppression(self):
        echo = EchoSuppressor()
        echo.on_audio_delta()
        self.assertTrue(echo.ai_speaking)
        ignored = echo.on_speech_started()
        self.assertFalse(ignored)
        self.assertFalse(echo.ai_speaking)
        self.assertEqual(echo.cooldown_end, 0.0)

    def test_speech_started_during_greeting_is_ignored(self):
        echo = EchoSuppressor()
        echo.start_greeting_suppression()
        self.assertTrue(echo.greeting_in_progress)
        ignored = echo.on_speech_started()
        self.assertTrue(ignored)

    def test_barge_in_resets_all_suppression(self):
        echo = EchoSuppressor()
        echo.on_audio_delta()
        echo.cooldown_end = 999.0
        echo.on_barge_in()
        self.assertFalse(echo.ai_speaking)
        self.assertEqual(echo.cooldown_end, 0.0)

    def test_greeting_suppression_doubles_cooldown(self):
        async def _run():
            echo = EchoSuppressor()
            echo.start_greeting_suppression()
            loop = asyncio.get_running_loop()
            target_ws = MagicMock()
            target_ws.closed = False
            target_ws.send_str = AsyncMock()
            t = loop.time()
            echo.on_audio_done(loop, target_ws)
            expected_cooldown = ECHO_COOLDOWN_SEC * 2
            self.assertAlmostEqual(echo.cooldown_end, t + expected_cooldown, delta=0.1)
            self.assertFalse(echo.greeting_in_progress)  # reset after done
        asyncio.run(_run())

    def test_start_greeting_suppression_sets_state(self):
        echo = EchoSuppressor()
        echo.start_greeting_suppression()
        self.assertTrue(echo.ai_speaking)
        self.assertTrue(echo.greeting_in_progress)

    def test_audio_done_sends_clear_to_openai(self):
        async def _run():
            echo = EchoSuppressor()
            loop = asyncio.get_running_loop()
            target_ws = MagicMock()
            target_ws.closed = False
            target_ws.send_str = AsyncMock()
            echo.on_audio_done(loop, target_ws)
            # Give the ensure_future a tick to execute
            await asyncio.sleep(0)
            target_ws.send_str.assert_called()
        asyncio.run(_run())


# ═══════════════════════════════════════════════════════════════════════════════
# AUDIO PIPELINE UTILITY TESTS
# ═══════════════════════════════════════════════════════════════════════════════

class TypeRegexTests(unittest.TestCase):
    """Test the TYPE_RE regex used for fast message routing."""

    def test_extracts_type_from_json(self):
        data = '{"type": "response.audio.delta", "data": "..."}'
        m = TYPE_RE.search(data)
        self.assertIsNotNone(m)
        self.assertEqual(m.group(1), "response.audio.delta")

    def test_passthrough_server_types_recognized(self):
        for msg_type in _PASSTHROUGH_SERVER_TYPES:
            data = json.dumps({"type": msg_type})
            m = TYPE_RE.search(data)
            self.assertIsNotNone(m, f"TYPE_RE should match {msg_type}")
            self.assertEqual(m.group(1), msg_type)

    def test_passthrough_client_types_recognized(self):
        for msg_type in _PASSTHROUGH_CLIENT_TYPES:
            data = json.dumps({"type": msg_type})
            m = TYPE_RE.search(data)
            self.assertIsNotNone(m, f"TYPE_RE should match {msg_type}")
            self.assertEqual(m.group(1), msg_type)

    def test_no_type_field_returns_none(self):
        data = '{"data": "no type here"}'
        m = TYPE_RE.search(data)
        self.assertIsNone(m)


class PreSerializedMessagesTests(unittest.TestCase):
    """Test pre-serialized static messages are valid JSON."""

    def test_response_create_msg(self):
        parsed = json.loads(RESPONSE_CREATE_MSG)
        self.assertEqual(parsed["type"], "response.create")

    def test_input_audio_clear_msg(self):
        parsed = json.loads(INPUT_AUDIO_CLEAR_MSG)
        self.assertEqual(parsed["type"], "input_audio_buffer.clear")


class GAEventTranslationTests(unittest.TestCase):
    """Test GA-to-legacy event name translation mapping."""

    def test_all_ga_events_are_in_passthrough_set(self):
        for ga_name in _GA_TO_LEGACY_EVENTS:
            self.assertIn(ga_name, _PASSTHROUGH_SERVER_TYPES,
                          f"GA event {ga_name} must be in passthrough set")

    def test_all_legacy_events_are_in_passthrough_set(self):
        for legacy_name in _GA_TO_LEGACY_EVENTS.values():
            self.assertIn(legacy_name, _PASSTHROUGH_SERVER_TYPES,
                          f"Legacy event {legacy_name} must be in passthrough set")


# ═══════════════════════════════════════════════════════════════════════════════
# RTMT CORE CLASSES TESTS
# ═══════════════════════════════════════════════════════════════════════════════

class ToolResultTests(unittest.TestCase):
    """Test ToolResult value object."""

    def test_to_text_returns_string(self):
        tr = ToolResult("hello", ToolResultDirection.TO_SERVER)
        self.assertEqual(tr.to_text(), "hello")

    def test_to_text_none_returns_empty(self):
        tr = ToolResult(None, ToolResultDirection.TO_SERVER)
        self.assertEqual(tr.to_text(), "")

    def test_to_client_text_falls_back_to_text(self):
        tr = ToolResult("server text", ToolResultDirection.TO_BOTH)
        self.assertEqual(tr.to_client_text(), "server text")

    def test_to_client_text_uses_separate_payload(self):
        tr = ToolResult("server text", ToolResultDirection.TO_BOTH, client_text='{"order": []}')
        self.assertEqual(tr.to_client_text(), '{"order": []}')

    def test_direction_to_both(self):
        tr = ToolResult("x", ToolResultDirection.TO_BOTH)
        self.assertEqual(tr.destination, ToolResultDirection.TO_BOTH)
        self.assertEqual(tr.destination.value, 3)


class ToolResultDirectionTests(unittest.TestCase):
    """Test the ToolResultDirection enum values."""

    def test_to_server_value(self):
        self.assertEqual(ToolResultDirection.TO_SERVER.value, 1)

    def test_to_client_value(self):
        self.assertEqual(ToolResultDirection.TO_CLIENT.value, 2)

    def test_to_both_value(self):
        self.assertEqual(ToolResultDirection.TO_BOTH.value, 3)


class ToolTests(unittest.TestCase):
    """Test the Tool wrapper."""

    def test_tool_stores_schema_and_target(self):
        schema = {"name": "test_tool"}
        target = MagicMock()
        tool = Tool(target=target, schema=schema)
        self.assertEqual(tool.schema, schema)
        self.assertEqual(tool.target, target)


class RTToolCallTests(unittest.TestCase):
    """Test RTToolCall tracking object."""

    def test_stores_ids(self):
        tc = RTToolCall("call-123", "prev-456")
        self.assertEqual(tc.tool_call_id, "call-123")
        self.assertEqual(tc.previous_id, "prev-456")


# ═══════════════════════════════════════════════════════════════════════════════
# HMAC TOKEN TESTS (from rtmt.py)
# ═══════════════════════════════════════════════════════════════════════════════

class HMACTokenTests(unittest.TestCase):
    """Test HMAC session token creation and validation."""

    def setUp(self):
        self.secret = b"test-secret-key-1234"

    def test_valid_token_accepted(self):
        token = create_hmac_token(self.secret, expiry_seconds=60)
        self.assertTrue(validate_hmac_token(token, self.secret))

    def test_expired_token_rejected(self):
        token = create_hmac_token(self.secret, expiry_seconds=-1)
        self.assertFalse(validate_hmac_token(token, self.secret))

    def test_wrong_secret_rejected(self):
        token = create_hmac_token(self.secret)
        self.assertFalse(validate_hmac_token(token, b"wrong-secret"))

    def test_empty_token_rejected(self):
        self.assertFalse(validate_hmac_token("", self.secret))

    def test_malformed_token_rejected(self):
        self.assertFalse(validate_hmac_token("not-a-valid-token", self.secret))

    def test_tampered_payload_rejected(self):
        token = create_hmac_token(self.secret)
        parts = token.split(".")
        parts[0] = parts[0][:-1] + "X"
        tampered = ".".join(parts)
        self.assertFalse(validate_hmac_token(tampered, self.secret))


# ═══════════════════════════════════════════════════════════════════════════════
# RTMIDDLETIER INITIALIZATION TESTS
# ═══════════════════════════════════════════════════════════════════════════════

class RTMiddleTierInitTests(unittest.TestCase):
    """Test RTMiddleTier construction and configuration."""

    def _make_rtmt(self, **kwargs):
        from azure.core.credentials import AzureKeyCredential
        cred = AzureKeyCredential("test-key")
        return RTMiddleTier(
            endpoint="https://fake.openai.azure.com",
            deployment="gpt-4o-realtime",
            credentials=cred,
            **kwargs,
        )

    def test_init_with_api_key(self):
        rtmt = self._make_rtmt()
        self.assertEqual(rtmt.key, "test-key")
        self.assertIsNone(rtmt._token_provider)

    def test_init_sets_voice_choice(self):
        rtmt = self._make_rtmt(voice_choice="coral")
        self.assertEqual(rtmt.voice_choice, "coral")

    def test_tools_empty_by_default(self):
        rtmt = self._make_rtmt()
        self.assertEqual(len(rtmt.tools), 0)

    def test_attach_to_app(self):
        rtmt = self._make_rtmt()
        app = web.Application()
        rtmt.attach_to_app(app, "/rt")
        routes = [r.resource.canonical for r in app.router.routes()]
        self.assertIn("/rt", routes)

    def test_system_message_settable(self):
        rtmt = self._make_rtmt()
        rtmt.system_message = "You are a carhop."
        self.assertEqual(rtmt.system_message, "You are a carhop.")


# ═══════════════════════════════════════════════════════════════════════════════
# MESSAGE PROCESSING TESTS
# ═══════════════════════════════════════════════════════════════════════════════

class ProcessMessageToServerTests(unittest.IsolatedAsyncioTestCase):
    """Test server-bound message processing."""

    def _make_rtmt(self):
        from azure.core.credentials import AzureKeyCredential
        cred = AzureKeyCredential("test-key")
        rtmt = RTMiddleTier("https://fake.openai.azure.com", "gpt-4o-realtime", cred)
        rtmt.system_message = "You are a carhop."
        rtmt.temperature = 0.6
        rtmt.max_tokens = 250
        rtmt.voice_choice = "coral"
        return rtmt

    async def test_session_update_injects_server_config(self):
        rtmt = self._make_rtmt()
        rtmt.tools["search"] = Tool(target=MagicMock(), schema={"name": "search"})
        ws = _make_mock_ws()
        order_state_singleton.sessions = {}
        rtmt._sessions.create_session(ws)

        msg = MagicMock()
        msg.data = json.dumps({
            "type": "session.update",
            "session": {"instructions": "client instructions"}
        })
        result = await rtmt._process_message_to_server(msg, ws)
        parsed = json.loads(result)
        session = parsed["session"]
        self.assertEqual(session["instructions"], "You are a carhop.")
        self.assertEqual(session["tool_choice"], "auto")
        self.assertEqual(len(session["tools"]), 1)
        # GA shape: discriminator present, voice nested under audio.output,
        # and `temperature` dropped because GA rejects unknown parameters.
        self.assertEqual(session["type"], "realtime")
        self.assertEqual(session["audio"]["output"]["voice"], "coral")
        self.assertNotIn("temperature", session)
        self.assertNotIn("voice", session)

    async def test_session_update_translates_legacy_audio_keys(self):
        rtmt = self._make_rtmt()
        ws = _make_mock_ws()
        order_state_singleton.sessions = {}
        rtmt._sessions.create_session(ws)

        msg = MagicMock()
        msg.data = json.dumps({
            "type": "session.update",
            "session": {
                "turn_detection": {"type": "server_vad", "threshold": 0.7},
                "input_audio_transcription": {"model": "whisper-1"},
                "input_audio_format": "pcm16",
                "modalities": ["audio", "text"],
            },
        })
        result = await rtmt._process_message_to_server(msg, ws)
        session = json.loads(result)["session"]

        self.assertEqual(session["audio"]["input"]["turn_detection"]["type"], "server_vad")
        self.assertEqual(session["audio"]["input"]["transcription"]["model"], "whisper-1")
        self.assertEqual(session["audio"]["input"]["format"], {"type": "audio/pcm", "rate": 24000})
        self.assertEqual(session["output_modalities"], ["audio", "text"])
        self.assertEqual(session["max_output_tokens"], rtmt.max_tokens)
        # Legacy keys must not survive — GA errors on unknown parameters.
        for legacy in ("turn_detection", "input_audio_transcription",
                       "input_audio_format", "modalities",
                       "max_response_output_tokens", "disable_audio"):
            self.assertNotIn(legacy, session)

    async def test_passthrough_client_audio_not_parsed(self):
        rtmt = self._make_rtmt()
        ws = _make_mock_ws()
        msg = MagicMock()
        msg.data = json.dumps({"type": "input_audio_buffer.append", "audio": "base64data"})
        result = await rtmt._process_message_to_server(msg, ws)
        # Should return data as-is (passthrough)
        self.assertEqual(result, msg.data)


class ProcessMessageToClientTests(unittest.IsolatedAsyncioTestCase):
    """Test client-bound message processing."""

    def _make_rtmt(self):
        from azure.core.credentials import AzureKeyCredential
        cred = AzureKeyCredential("test-key")
        rtmt = RTMiddleTier("https://fake.openai.azure.com", "gpt-4o-realtime", cred)
        return rtmt

    async def test_passthrough_audio_delta_returned_as_is(self):
        rtmt = self._make_rtmt()
        client_ws = _make_mock_ws()
        server_ws = _make_mock_ws()
        tools_pending = {}
        msg = MagicMock()
        msg.data = json.dumps({"type": "response.audio.delta", "delta": "base64audio"})
        result = await rtmt._process_message_to_client(msg, client_ws, server_ws, tools_pending)
        self.assertEqual(result, msg.data)

    async def test_ga_audio_delta_translated_to_legacy(self):
        """GA event names are translated to legacy names for client compatibility."""
        rtmt = self._make_rtmt()
        client_ws = _make_mock_ws()
        server_ws = _make_mock_ws()
        tools_pending = {}
        msg = MagicMock()
        msg.data = json.dumps({"type": "response.output_audio.delta", "delta": "base64audio"})
        result = await rtmt._process_message_to_client(msg, client_ws, server_ws, tools_pending)
        parsed = json.loads(result)
        self.assertEqual(parsed["type"], "response.audio.delta")

    async def test_ga_transcript_delta_translated_to_legacy(self):
        """GA transcript event names are translated to legacy names."""
        rtmt = self._make_rtmt()
        client_ws = _make_mock_ws()
        server_ws = _make_mock_ws()
        tools_pending = {}
        msg = MagicMock()
        msg.data = json.dumps({"type": "response.output_audio_transcript.delta", "delta": "hello"})
        result = await rtmt._process_message_to_client(msg, client_ws, server_ws, tools_pending)
        parsed = json.loads(result)
        self.assertEqual(parsed["type"], "response.audio_transcript.delta")

    async def test_conversation_item_added_handles_function_call(self):
        """GA conversation.item.added event registers tool calls like conversation.item.created."""
        rtmt = self._make_rtmt()
        client_ws = _make_mock_ws()
        server_ws = _make_mock_ws()
        tools_pending = {}
        order_state_singleton.sessions = {}
        rtmt._sessions.create_session(client_ws)
        msg = MagicMock()
        msg.data = json.dumps({
            "type": "conversation.item.added",
            "previous_item_id": "prev-1",
            "item": {
                "type": "function_call",
                "call_id": "call-ga-1",
                "name": "test_tool",
                "arguments": "{}"
            }
        })
        result = await rtmt._process_message_to_client(msg, client_ws, server_ws, tools_pending)
        self.assertIsNone(result)
        self.assertIn("call-ga-1", tools_pending)

    async def test_session_created_strips_instructions(self):
        rtmt = self._make_rtmt()
        rtmt.voice_choice = "coral"
        client_ws = _make_mock_ws()
        server_ws = _make_mock_ws()
        tools_pending = {}
        order_state_singleton.sessions = {}
        rtmt._sessions.create_session(client_ws)

        msg = MagicMock()
        msg.data = json.dumps({
            "type": "session.created",
            "session": {
                "id": "sess-123",
                "instructions": "secret prompt",
                "tools": [{"name": "search"}],
                "voice": "alloy",
                "tool_choice": "auto",
                "max_response_output_tokens": 500,
            }
        })
        result = await rtmt._process_message_to_client(msg, client_ws, server_ws, tools_pending)
        parsed = json.loads(result)
        self.assertEqual(parsed["session"]["instructions"], "")
        self.assertEqual(parsed["session"]["tools"], [])
        self.assertEqual(parsed["session"]["voice"], "coral")
        self.assertEqual(parsed["session"]["audio"]["output"]["voice"], "coral")

    async def test_session_updated_strips_instructions_and_tools(self):
        """swigerb/SonicAIDriveThru#27: session.updated echoes the full session
        object just like session.created, and must be scrubbed identically."""
        rtmt = self._make_rtmt()
        rtmt.voice_choice = "coral"
        client_ws = _make_mock_ws()
        server_ws = _make_mock_ws()
        tools_pending = {}
        order_state_singleton.sessions = {}
        rtmt._sessions.create_session(client_ws)

        msg = MagicMock()
        msg.data = json.dumps({
            "type": "session.updated",
            "session": {
                "id": "sess-123",
                "instructions": "secret prompt",
                "tools": [{"name": "search"}],
                "voice": "alloy",
                "tool_choice": "auto",
                "max_response_output_tokens": 500,
            }
        })
        result = await rtmt._process_message_to_client(msg, client_ws, server_ws, tools_pending)
        parsed = json.loads(result)
        self.assertEqual(parsed["session"]["instructions"], "")
        self.assertEqual(parsed["session"]["tools"], [])
        self.assertEqual(parsed["session"]["voice"], "coral")
        self.assertEqual(parsed["session"]["audio"]["output"]["voice"], "coral")

    async def test_session_updated_with_no_session_object_is_a_noop(self):
        """A malformed/unexpected session.updated with no `session` key must
        not crash -- it's forwarded unchanged rather than scrubbed."""
        rtmt = self._make_rtmt()
        client_ws = _make_mock_ws()
        server_ws = _make_mock_ws()
        tools_pending = {}
        msg = MagicMock()
        msg.data = json.dumps({"type": "session.updated"})
        result = await rtmt._process_message_to_client(msg, client_ws, server_ws, tools_pending)
        self.assertEqual(result, msg.data)

    async def test_session_created_and_updated_strip_ga_only_secret_fields(self):
        """swigerb/SonicAIDriveThru#29: the GA echo carries a duplicate token
        cap under `max_output_tokens` (the original scrub only nulled the
        legacy `max_response_output_tokens`), plus `model` (the internal
        Azure deployment name), `audio.input.transcription.model` (the
        transcription deployment name), `reasoning` and `parallel_tool_calls`
        (reasoning-model tuning knobs) -- none of which the browser needs or
        useRealtime.tsx reads off session.created/session.updated."""
        rtmt = self._make_rtmt()
        client_ws = _make_mock_ws()
        server_ws = _make_mock_ws()
        tools_pending = {}
        order_state_singleton.sessions = {}
        rtmt._sessions.create_session(client_ws)

        raw_session = {
            "id": "sess-123",
            "instructions": "secret prompt",
            "tools": [{"name": "search"}],
            "voice": "alloy",
            "tool_choice": "auto",
            "max_response_output_tokens": 500,
            "max_output_tokens": 500,
            "model": "gpt-realtime-2.1-super-secret-deployment",
            "reasoning": {"effort": "low"},
            "parallel_tool_calls": True,
            "audio": {
                "input": {"transcription": {"model": "gpt-4o-transcribe-secret-deployment"}},
                "output": {"voice": "alloy"},
            },
        }
        for event_type in ("session.created", "session.updated"):
            msg = MagicMock()
            msg.data = json.dumps({"type": event_type, "session": dict(raw_session)})
            result = await rtmt._process_message_to_client(msg, client_ws, server_ws, tools_pending)
            session = json.loads(result)["session"]
            self.assertNotIn("max_output_tokens", session, f"{event_type} leaked the GA max-token cap")
            self.assertNotIn("model", session, f"{event_type} leaked the internal deployment name")
            self.assertNotIn("reasoning", session, f"{event_type} leaked the reasoning tuning knob")
            self.assertNotIn("parallel_tool_calls", session, f"{event_type} leaked parallel_tool_calls")
            self.assertNotIn("model", session["audio"]["input"]["transcription"],
                              f"{event_type} leaked the transcription deployment name")

    async def test_conversation_item_created_drops_server_authored_system_item(self):
        """swigerb/SonicAIDriveThru#29: role="system" conversation items are
        only ever ones the middle tier itself created (session_manager's
        build_rehydration_item / build_nudge_item) and sent straight upstream
        -- the model never originates one. Upstream echoes the item back via
        conversation.item.created/added, which must not reach the browser:
        it carries the resume rehydration text (recent transcript + order
        JSON) or the silent-guest nudge prompt, neither meant for the guest."""
        rtmt = self._make_rtmt()
        client_ws = _make_mock_ws()
        server_ws = _make_mock_ws()
        tools_pending = {}
        for event_type in ("conversation.item.created", "conversation.item.added"):
            msg = MagicMock()
            msg.data = json.dumps({
                "type": event_type,
                "previous_item_id": "prev-1",
                "item": {
                    "type": "message",
                    "role": "system",
                    "content": [{"type": "input_text", "text": "Current order (JSON): {...}"}],
                },
            })
            result = await rtmt._process_message_to_client(msg, client_ws, server_ws, tools_pending)
            self.assertIsNone(result, f"{event_type} with role=system must be dropped from the client relay")

    async def test_conversation_item_created_still_forwards_user_and_assistant_items(self):
        """Only role="system" items (or items with a middle-tier item id) are
        dropped -- role="user"/"assistant" items (real conversation turns)
        must keep reaching the browser unchanged; the frontend's transcript
        UI depends on them."""
        rtmt = self._make_rtmt()
        client_ws = _make_mock_ws()
        server_ws = _make_mock_ws()
        tools_pending = {}
        for role in ("user", "assistant"):
            msg = MagicMock()
            msg.data = json.dumps({
                "type": "conversation.item.created",
                "item": {"type": "message", "role": role, "content": [{"type": "input_text", "text": "hi"}]},
            })
            result = await rtmt._process_message_to_client(msg, client_ws, server_ws, tools_pending)
            self.assertEqual(result, msg.data, f"role={role} conversation item must still be forwarded")

    async def test_conversation_item_created_drops_middle_tier_item_by_id_not_role(self):
        """swigerb/SonicAIDriveThru#29 follow-up (PR #30 review "S1"): the
        greeting is role="user" (not "system") and is middle-tier-authored --
        a role-based drop alone can never catch it, since the model also
        sends real role="user" items. Authorship must be keyed off the
        item id prefix instead."""
        rtmt = self._make_rtmt()
        client_ws = _make_mock_ws()
        server_ws = _make_mock_ws()
        tools_pending = {}
        for event_type in ("conversation.item.created", "conversation.item.added"):
            msg = MagicMock()
            msg.data = json.dumps({
                "type": event_type,
                "item": {
                    "id": f"{MIDDLE_TIER_ITEM_ID_PREFIX}deadbeef0000",
                    "type": "message",
                    "role": "user",
                    "content": [{"type": "input_text", "text": "Say EXACTLY this greeting and NOTHING else: ..."}],
                },
            })
            result = await rtmt._process_message_to_client(msg, client_ws, server_ws, tools_pending)
            self.assertIsNone(result, f"{event_type} with a middle-tier item id must be dropped regardless of role")

    async def test_conversation_item_done_and_retrieved_drop_server_authored_items(self):
        """swigerb/SonicAIDriveThru#29 follow-up (PR #30 review "M1"): GA
        emits conversation.item.done "when the item is finalized" with the
        *full* item -- carrying the exact same leak surface (rehydration
        text, nudge, function_call args, function_call_output/tool results)
        as .created/.added if left unfiltered. conversation.item.retrieved
        (sent in reply to an explicit conversation.item.retrieve) must be
        handled defensively the same way even though nothing in this
        codebase currently issues that request."""
        rtmt = self._make_rtmt()
        client_ws = _make_mock_ws()
        server_ws = _make_mock_ws()
        tools_pending = {}
        items = [
            {"type": "message", "role": "system", "content": [{"type": "input_text", "text": "Current order (JSON): {...}"}]},
            {"id": f"{MIDDLE_TIER_ITEM_ID_PREFIX}abc123", "type": "message", "role": "user", "content": [{"type": "input_text", "text": "Say EXACTLY this greeting..."}]},
            {"type": "function_call", "call_id": "call-done-1", "name": "search", "arguments": '{"q":"combo"}'},
            {"type": "function_call_output", "call_id": "call-done-1", "output": "search hit: secret result"},
        ]
        for event_type in ("conversation.item.done", "conversation.item.retrieved"):
            for item in items:
                msg = MagicMock()
                msg.data = json.dumps({"type": event_type, "item": item})
                result = await rtmt._process_message_to_client(msg, client_ws, server_ws, tools_pending)
                self.assertIsNone(result, f"{event_type} leaked item={item!r}")

    async def test_conversation_item_done_still_forwards_user_and_assistant_items(self):
        """A genuine, model-authored message item (no middle-tier id, no
        system role, not a function_call/function_call_output) must still
        reach the browser on .done -- the transcript UI needs the finalized
        text."""
        rtmt = self._make_rtmt()
        client_ws = _make_mock_ws()
        server_ws = _make_mock_ws()
        tools_pending = {}
        for role in ("user", "assistant"):
            msg = MagicMock()
            msg.data = json.dumps({
                "type": "conversation.item.done",
                "item": {"type": "message", "role": role, "content": [{"type": "input_text", "text": "finalized turn"}]},
            })
            result = await rtmt._process_message_to_client(msg, client_ws, server_ws, tools_pending)
            self.assertEqual(result, msg.data, f"role={role} conversation item must still be forwarded on .done")

    async def test_unknown_message_type_returned_as_data(self):
        """Unknown message types should pass through without crashing."""
        rtmt = self._make_rtmt()
        client_ws = _make_mock_ws()
        server_ws = _make_mock_ws()
        tools_pending = {}
        msg = MagicMock()
        msg.data = json.dumps({"type": "unknown.custom.type", "payload": "test"})
        result = await rtmt._process_message_to_client(msg, client_ws, server_ws, tools_pending)
        # Unknown types should be returned (not None, not crash)
        self.assertIsNotNone(result)

    async def test_function_call_output_item_done_executes_tool(self):
        """Test that response.output_item.done with a function_call triggers tool execution."""
        rtmt = self._make_rtmt()
        client_ws = _make_mock_ws()
        server_ws = _make_mock_ws()
        order_state_singleton.sessions = {}
        rtmt._sessions.create_session(client_ws)

        # Register a mock tool
        mock_tool_target = AsyncMock(return_value=ToolResult("tool result", ToolResultDirection.TO_SERVER))
        rtmt.tools["test_tool"] = Tool(target=mock_tool_target, schema={"name": "test_tool"})

        # Prepare pending tool call
        tools_pending = {"call-1": RTToolCall("call-1", "prev-1")}

        msg = MagicMock()
        msg.data = json.dumps({
            "type": "response.output_item.done",
            "item": {
                "type": "function_call",
                "name": "test_tool",
                "call_id": "call-1",
                "arguments": '{"query": "test"}'
            }
        })
        result = await rtmt._process_message_to_client(msg, client_ws, server_ws, tools_pending)
        self.assertIsNone(result)  # tool responses are not forwarded as-is
        mock_tool_target.assert_called_once()
        server_ws.send_json.assert_called_once()

    async def test_tool_result_to_both_sends_to_client_and_server(self):
        """Test that TO_BOTH sends result to both client and server."""
        rtmt = self._make_rtmt()
        client_ws = _make_mock_ws()
        server_ws = _make_mock_ws()
        order_state_singleton.sessions = {}
        rtmt._sessions.create_session(client_ws)

        mock_tool_target = AsyncMock(return_value=ToolResult(
            "Added 1 Burger", ToolResultDirection.TO_BOTH, client_text='{"items":[]}'
        ))
        rtmt.tools["update_order"] = Tool(target=mock_tool_target, schema={"name": "update_order"})

        tools_pending = {"call-2": RTToolCall("call-2", "prev-2")}
        msg = MagicMock()
        msg.data = json.dumps({
            "type": "response.output_item.done",
            "item": {
                "type": "function_call",
                "name": "update_order",
                "call_id": "call-2",
                "arguments": '{"action":"add","item_name":"Burger","size":"standard","quantity":1,"price":5.99}'
            }
        })
        result = await rtmt._process_message_to_client(msg, client_ws, server_ws, tools_pending)
        self.assertIsNone(result)
        # Both server and client should receive messages
        server_ws.send_json.assert_called_once()
        client_ws.send_json.assert_called_once()
        client_payload = client_ws.send_json.call_args[0][0]
        self.assertEqual(client_payload["type"], "extension.middle_tier_tool_response")
        self.assertEqual(client_payload["tool_result"], '{"items":[]}')

    async def test_error_message_logged_not_crashed(self):
        """OpenAI error messages should be logged, not crash the handler."""
        rtmt = self._make_rtmt()
        client_ws = _make_mock_ws()
        server_ws = _make_mock_ws()
        tools_pending = {}
        msg = MagicMock()
        msg.data = json.dumps({"type": "error", "error": {"message": "something went wrong"}})
        with self.assertLogs("sonic-drive-in", level="ERROR"):
            result = await rtmt._process_message_to_client(msg, client_ws, server_ws, tools_pending)
        self.assertIsNotNone(result)

    async def test_malformed_json_does_not_crash(self):
        """Malformed data that passes regex but fails json.loads should not crash."""
        rtmt = self._make_rtmt()
        client_ws = _make_mock_ws()
        server_ws = _make_mock_ws()
        tools_pending = {}
        msg = MagicMock()
        # Valid regex match but will fail on JSON parse for non-passthrough type
        msg.data = '{"type": "session.created", INVALID JSON'
        with self.assertRaises(json.JSONDecodeError):
            await rtmt._process_message_to_client(msg, client_ws, server_ws, tools_pending)


# ═══════════════════════════════════════════════════════════════════════════════
# ORIGIN MATCHING TESTS (#25)
# ═══════════════════════════════════════════════════════════════════════════════

class OriginMatchesHostTests(unittest.TestCase):
    """Direct unit tests of `_origin_matches_host`, which replaced the buggy
    `origin.endswith(host)` check (#25): a suffix match let
    `https://evil-<host>` through since a lookalike domain the attacker
    controls can still legitimately end with the real host's characters.
    """

    def test_exact_scheme_and_host_matches(self):
        self.assertTrue(_origin_matches_host("https://example.com", "example.com"))

    def test_exact_host_and_non_default_port_matches(self):
        self.assertTrue(_origin_matches_host("https://localhost:8080", "localhost:8080"))

    def test_case_insensitive_match(self):
        self.assertTrue(_origin_matches_host("https://Example.COM", "example.com"))

    def test_lookalike_prefix_suffix_is_rejected(self):
        """The exact bug: a suffix match let a domain the attacker actually
        owns (evil-example.com) through, because it merely ends with the
        legitimate host's characters."""
        self.assertFalse(_origin_matches_host("https://evil-example.com", "example.com"))

    def test_lookalike_prefix_suffix_with_port_is_rejected(self):
        self.assertFalse(_origin_matches_host("https://evil-localhost:8080", "localhost:8080"))

    def test_subdomain_is_rejected(self):
        """A subdomain is a different origin -- must not be silently trusted
        just because it ends with the real host."""
        self.assertFalse(_origin_matches_host("https://attacker.example.com", "example.com"))

    def test_mismatched_port_is_rejected(self):
        self.assertFalse(_origin_matches_host("https://example.com:9999", "example.com:8080"))

    def test_missing_scheme_still_compares_correctly(self):
        # urlsplit treats a schemeless "host:port"-shaped string as
        # scheme=host, path=port unless it starts with "//" -- exercised here
        # to document that a malformed Origin (no browser ever sends one
        # without a scheme) simply fails to match rather than being
        # mis-parsed into an accidental pass.
        self.assertFalse(_origin_matches_host("example.com", "example.com"))

    def test_completely_different_host_is_rejected(self):
        self.assertFalse(_origin_matches_host("https://evil.com", "example.com"))

    def test_empty_host_never_matches(self):
        """swigerb/SonicAIDriveThru#25 follow-up (PR #30 review "S4"): a
        missing/blank Host header must never accidentally validate an
        origin. Without this guard, urlsplit("null").netloc == "" would
        make a bare Origin: null match an empty host."""
        self.assertFalse(_origin_matches_host("https://example.com", ""))
        self.assertFalse(_origin_matches_host("null", ""))
        self.assertFalse(_origin_matches_host("", ""))


class DropFromClientTests(unittest.TestCase):
    """Direct unit tests of `_drop_from_client`, the shared authorship/type
    filter used by every conversation.item.* case in
    `_process_message_to_client` (swigerb/SonicAIDriveThru#29 follow-up,
    PR #30 review "M1"/"S1")."""

    def test_function_call_is_dropped(self):
        self.assertTrue(_drop_from_client({"type": "function_call", "call_id": "c1"}))

    def test_function_call_output_is_dropped(self):
        self.assertTrue(_drop_from_client({"type": "function_call_output", "call_id": "c1"}))

    def test_middle_tier_item_id_is_dropped_regardless_of_role(self):
        self.assertTrue(_drop_from_client({
            "id": f"{MIDDLE_TIER_ITEM_ID_PREFIX}xyz",
            "type": "message",
            "role": "user",
        }))

    def test_role_system_is_dropped_as_legacy_backstop(self):
        self.assertTrue(_drop_from_client({"type": "message", "role": "system"}))

    def test_normal_user_and_assistant_items_are_kept(self):
        self.assertFalse(_drop_from_client({"type": "message", "role": "user", "id": "item-abc"}))
        self.assertFalse(_drop_from_client({"type": "message", "role": "assistant", "id": "item-def"}))

    def test_non_string_id_does_not_crash(self):
        self.assertFalse(_drop_from_client({"type": "message", "role": "user", "id": 12345}))


# ═══════════════════════════════════════════════════════════════════════════════
# WEBSOCKET HANDLER INTEGRATION TESTS
# ═══════════════════════════════════════════════════════════════════════════════

class WebSocketHandlerTests(unittest.IsolatedAsyncioTestCase):
    """Test the _websocket_handler entry point."""

    def _make_rtmt(self):
        from azure.core.credentials import AzureKeyCredential
        cred = AzureKeyCredential("test-key")
        rtmt = RTMiddleTier("https://fake.openai.azure.com", "gpt-4o-realtime", cred)
        return rtmt

    async def test_origin_validation_rejects_foreign_origin(self):
        rtmt = self._make_rtmt()
        request = MagicMock(spec=web.Request)
        request.headers = {"Origin": "https://evil.com", "Host": "localhost:8080"}
        request.query = {}
        result = await rtmt._websocket_handler(request)
        self.assertEqual(result.status, 403)

    async def test_origin_validation_rejects_lookalike_suffix_origin(self):
        """#25: `origin.endswith(host)` used to accept any origin whose
        netloc merely ended with the Host header as a string suffix --
        `https://evil-localhost:8080` passes
        `"evil-localhost:8080".endswith("localhost:8080")` even though it's
        an attacker-controlled domain, not the real host. Must be rejected.
        """
        rtmt = self._make_rtmt()
        request = MagicMock(spec=web.Request)
        request.headers = {"Origin": "https://evil-localhost:8080", "Host": "localhost:8080"}
        request.query = {}
        result = await rtmt._websocket_handler(request)
        self.assertEqual(result.status, 403)

    async def test_origin_validation_accepts_exact_host_match(self):
        """Positive-path companion to the rejection tests above: an Origin
        that exactly matches Host must NOT be rejected by the origin check.
        Forces can_accept_session() to False so the handler takes its next,
        already-covered early-return branch (session limit reached) instead
        of attempting a full WebSocket upgrade against a MagicMock request;
        that branch's own WebSocketResponse is replaced with a stub so it
        doesn't need a real transport either.
        """
        rtmt = self._make_rtmt()
        request = MagicMock(spec=web.Request)
        request.headers = {"Origin": "https://localhost:8080", "Host": "localhost:8080"}
        request.query = {}
        stub_ws = MagicMock()
        stub_ws.prepare = AsyncMock()
        stub_ws.send_json = AsyncMock()
        stub_ws.close = AsyncMock()
        with patch.dict("rtmt._security_cfg", {"allowed_origins": []}), \
             patch.object(rtmt._sessions, "can_accept_session", return_value=False), \
             patch("rtmt.web.WebSocketResponse", return_value=stub_ws):
            result = await rtmt._websocket_handler(request)
        # Session-limit branch returns the prepared WebSocketResponse rather
        # than the plain 403 web.Response the origin check returns -- proves
        # we got past origin validation.
        self.assertIs(result, stub_ws)

    async def test_origin_validation_missing_origin_is_unchanged(self):
        """Documents existing (unchanged by #25) behaviour: a request with no
        Origin header at all is still accepted -- non-browser callers (curl,
        server-to-server, the conformance harness's own health checks)
        legitimately omit it, and #25 only hardens the case where an Origin
        *is* present but doesn't match."""
        rtmt = self._make_rtmt()
        request = MagicMock(spec=web.Request)
        request.headers = {"Host": "localhost:8080"}
        request.query = {}
        stub_ws = MagicMock()
        stub_ws.prepare = AsyncMock()
        stub_ws.send_json = AsyncMock()
        stub_ws.close = AsyncMock()
        with patch.dict("rtmt._security_cfg", {"allowed_origins": []}), \
             patch.object(rtmt._sessions, "can_accept_session", return_value=False), \
             patch("rtmt.web.WebSocketResponse", return_value=stub_ws):
            result = await rtmt._websocket_handler(request)
        self.assertIs(result, stub_ws)

    async def test_token_validation_rejects_bad_token(self):
        rtmt = self._make_rtmt()
        rtmt.app_secret = b"test-secret"
        request = MagicMock(spec=web.Request)
        request.headers = {"Origin": "", "Host": "localhost:8080"}
        request.query = {"token": "bad-token"}
        with patch.dict("rtmt._security_cfg", {"require_session_token": True, "allowed_origins": []}):
            result = await rtmt._websocket_handler(request)
        self.assertEqual(result.status, 401)


# ═══════════════════════════════════════════════════════════════════════════════
# EXTENSION SET VOICE TESTS
# ═══════════════════════════════════════════════════════════════════════════════

class ExtensionSetVoiceTests(unittest.TestCase):
    """The voice picker must reach the GA endpoint in the GA shape.

    These exercise RTMiddleTier.build_voice_update directly rather than
    re-deriving the translation, so removing the _to_ga_session call from the
    production path actually fails the suite.
    """

    def _make_rtmt(self):
        cred = MagicMock()
        cred.get_token.return_value = MagicMock(token="tok", expires_on=9999999999)
        return RTMiddleTier("https://fake.openai.azure.com", "gpt-realtime-2.1", cred)

    def test_build_voice_update_is_ga_shaped(self):
        rtmt = self._make_rtmt()
        session = json.loads(rtmt.build_voice_update("marin"))["session"]
        self.assertEqual(session["type"], "realtime")
        self.assertEqual(session["audio"]["output"]["voice"], "marin")
        # The legacy top-level key gets the whole session.update rejected.
        self.assertNotIn("voice", session)

    def test_build_voice_update_all_supported_voices(self):
        rtmt = self._make_rtmt()
        for v in ["alloy", "ash", "ballad", "coral", "echo",
                  "sage", "shimmer", "verse", "marin", "cedar"]:
            session = json.loads(rtmt.build_voice_update(v))["session"]
            self.assertEqual(session["audio"]["output"]["voice"], v)
            self.assertNotIn("voice", session)


# ═══════════════════════════════════════════════════════════════════════════════
# GPT-REALTIME-2.1 GA SURFACE TESTS
# ═══════════════════════════════════════════════════════════════════════════════

# Built-in voices accepted by session.audio.output.voice on gpt-realtime-2.1 --
# probed live 2026-09-22; the service's own rejection message for fable/onyx/
# nova lists exactly these ten.
GA_REALTIME_VOICES = {"alloy", "ash", "ballad", "coral", "echo",
                      "sage", "shimmer", "verse", "marin", "cedar"}


class GARealtime21SurfaceTests(unittest.TestCase):
    """gpt-realtime-2.1 adds optional reasoning-model session fields; nothing else moved."""

    def _make_rtmt(self):
        rtmt = RTMiddleTier("https://fake.openai.azure.com", "gpt-realtime-2.1",
                            AzureKeyCredential("k"), voice_choice="shimmer")
        rtmt.system_message = "sys"
        return rtmt

    def test_reasoning_model_fields_survive_ga_translation(self):
        session = _to_ga_session({"reasoning": {"effort": "low"}, "parallel_tool_calls": False,
                                  "temperature": 0.6})
        self.assertEqual(session["reasoning"], {"effort": "low"})
        self.assertIs(session["parallel_tool_calls"], False)
        self.assertNotIn("temperature", session)

    def test_reasoning_fields_are_not_sent_by_default(self):
        # A session.update the model rejects drops the tools with it, so the
        # default payload must stay valid on non-reasoning models (1.5) too.
        rtmt = self._make_rtmt()
        payloads = [json.loads(rtmt.build_bootstrap_session_update())["session"],
                    rtmt._build_session({}),
                    json.loads(rtmt.build_voice_update("marin"))["session"]]
        for session in payloads:
            self.assertNotIn("reasoning", session)
            self.assertNotIn("parallel_tool_calls", session)

    def test_voice_picker_offers_exactly_the_ga_voices(self):
        voices = (Path(__file__).resolve().parents[2] / "frontend" / "src" / "lib"
                  / "voices.ts").read_text(encoding="utf-8")
        offered = re.findall(r'\{ value: "([a-z]+)"', voices)
        self.assertEqual(set(offered), GA_REALTIME_VOICES)
        self.assertEqual(len(offered), len(GA_REALTIME_VOICES), "duplicate voice in the picker")

    def test_default_voice_is_a_ga_voice(self):
        from app import get_config
        self.assertIn(get_config()["model"]["default_voice"], GA_REALTIME_VOICES)

    def test_frontend_and_backend_default_voice_agree(self):
        from app import get_config
        voices = (Path(__file__).resolve().parents[2] / "frontend" / "src" / "lib"
                  / "voices.ts").read_text(encoding="utf-8")
        frontend_default = re.search(r'DEFAULT_VOICE = "([a-z]+)"', voices).group(1)
        self.assertEqual(frontend_default, "marin")
        self.assertEqual(get_config()["model"]["default_voice"], frontend_default)


if __name__ == "__main__":
    unittest.main()
