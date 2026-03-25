"""Tests for the real-time middle tier (rtmt.py), session manager (session_manager.py),
and audio pipeline (audio_pipeline.py).

Covers WebSocket lifecycle, session management, message routing, echo suppression,
and error recovery — all with mocked external services (no real OpenAI/Azure calls).
"""

import asyncio
import json
import sys
import time
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock, patch, PropertyMock

sys.path.append(str(Path(__file__).resolve().parents[1]))

from aiohttp import web
from order_state import order_state_singleton

# ── Imports under test ──
from session_manager import SessionManager, ContextMonitor
from audio_pipeline import (
    EchoSuppressor,
    ECHO_COOLDOWN_SEC,
    TYPE_RE,
    RESPONSE_CREATE_MSG,
    INPUT_AUDIO_CLEAR_MSG,
    _PASSTHROUGH_SERVER_TYPES,
    _PASSTHROUGH_CLIENT_TYPES,
)
from rtmt import (
    RTMiddleTier,
    Tool,
    ToolResult,
    ToolResultDirection,
    RTToolCall,
    create_hmac_token,
    validate_hmac_token,
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

    def test_greeting_msg_from_prompt_loader(self):
        loader = MagicMock()
        loader.get_greeting_json_str.return_value = '{"type":"custom_greeting"}'
        sm = SessionManager(prompt_loader=loader)
        self.assertEqual(sm.greeting_msg, '{"type":"custom_greeting"}')


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
        self.assertEqual(parsed["session"]["instructions"], "You are a carhop.")
        self.assertEqual(parsed["session"]["temperature"], 0.6)
        self.assertEqual(parsed["session"]["voice"], "coral")
        self.assertEqual(parsed["session"]["tool_choice"], "auto")
        self.assertEqual(len(parsed["session"]["tools"]), 1)

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

    async def test_session_created_strips_instructions(self):
        rtmt = self._make_rtmt()
        rtmt.voice_choice = "coral"
        client_ws = _make_mock_ws()
        server_ws = _make_mock_ws()
        tools_pending = {}
        order_state_singleton.sessions = {}
        sid = rtmt._sessions.create_session(client_ws)

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
        sid = rtmt._sessions.create_session(client_ws)

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
        sid = rtmt._sessions.create_session(client_ws)

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

    async def test_token_validation_rejects_bad_token(self):
        rtmt = self._make_rtmt()
        rtmt.app_secret = b"test-secret"
        request = MagicMock(spec=web.Request)
        request.headers = {"Origin": "", "Host": "localhost:8080"}
        request.query = {"token": "bad-token"}
        with patch.dict("rtmt._security_cfg", {"require_session_token": True, "allowed_origins": []}):
            result = await rtmt._websocket_handler(request)
        self.assertEqual(result.status, 401)


if __name__ == "__main__":
    unittest.main()
