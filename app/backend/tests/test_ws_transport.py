"""Browser websocket transport regressions (2026-09-22 incident, session 09ccb306).

The browser socket was auto-reconnected after our own idle close and then sat
~40s with only aiohttp heartbeats on it, so the first frame Chromium sent was a
PONG. aiohttp 3.14.2/3.14.3 then rejects the first permessage-deflate data frame
("Received frame with non-zero reserved bits", aio-libs/aiohttp#13274) and kills
the socket. These tests drive the real middle tier with the same client framing.
"""

import asyncio
import json
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

sys.path.append(str(Path(__file__).resolve().parents[1]))
sys.path.append(str(Path(__file__).resolve().parent))

import aiohttp
from aiohttp import WSMsgType, web
from aiohttp.test_utils import TestClient, TestServer
from azure.core.credentials import AzureKeyCredential
from test_session_bootstrap import BROWSER_SESSION_UPDATE, FakeGARealtime

import audio_pipeline
import rtmt as rtmt_module
import session_manager as session_manager_module
from order_state import order_state_singleton
from rtmt import RTMiddleTier


class BrowserSocketTransportTests(unittest.IsolatedAsyncioTestCase):

    async def asyncSetUp(self):
        self.fake = FakeGARealtime()
        self.fake_server = TestServer(self.fake.app())
        await self.fake_server.start_server()
        self.rtmt = RTMiddleTier(
            endpoint=str(self.fake_server.make_url("")),
            deployment="gpt-realtime-test",
            credentials=AzureKeyCredential("test-key"),
            voice_choice="shimmer",
        )
        self.rtmt.system_message = "sys"
        app = web.Application()
        self.rtmt.attach_to_app(app, "/realtime")
        self.client = TestClient(TestServer(app))
        await self.client.start_server()

    async def asyncTearDown(self):
        await self.client.close()
        await self.fake_server.close()

    async def _until(self, predicate, timeout=5.0):
        async def poll():
            while not predicate():
                await asyncio.sleep(0.01)
        await asyncio.wait_for(poll(), timeout)

    def _session_updates(self):
        # [0] is the server bootstrap; anything after it came from the browser.
        return [e for e in self.fake.received if e["type"] == "session.update"]

    async def test_handshake_does_not_negotiate_permessage_deflate(self):
        """Chromium always offers permessage-deflate; the server must decline it."""
        browser = await self.client.ws_connect("/realtime", compress=15)
        self.assertEqual(browser.compress, 0, "server accepted permessage-deflate")
        self.assertNotIn("Sec-WebSocket-Extensions", browser._response.headers)
        await browser.close()

    async def test_first_data_frame_after_heartbeat_pong_is_accepted(self):
        """Exact production framing: server PING -> browser PONG -> browser data frame."""
        with patch.object(rtmt_module, "_WS_HEARTBEAT_SEC", 0.2):
            browser = await self.client.ws_connect("/realtime", compress=15, autoping=False)
            ping = await asyncio.wait_for(browser.receive(), 5)
            while ping.type is not WSMsgType.PING:  # skip upstream traffic relayed to us
                ping = await asyncio.wait_for(browser.receive(), 5)
            await browser.pong(ping.data)
            await browser.send_str(json.dumps(BROWSER_SESSION_UPDATE))

            async def read_until_close():
                while True:
                    msg = await browser.receive()
                    if msg.type is WSMsgType.PING:
                        await browser.pong(msg.data)
                    elif msg.type in (WSMsgType.CLOSE, WSMsgType.CLOSED, WSMsgType.ERROR):
                        return msg
            reader = asyncio.create_task(read_until_close())
            await self._until(lambda: len(self._session_updates()) >= 2 or reader.done())
            if reader.done():
                close = reader.result()
                self.fail(f"server killed the socket: {close.data} {close.extra}")
            reader.cancel()
            await browser.close()

    async def test_idle_close_uses_application_close_code(self):
        """The browser keys 'do not auto-reconnect' off this exact code/reason."""
        browser = await self.client.ws_connect("/realtime")
        await self._until(lambda: self.rtmt._sessions.active_session_count == 1)
        session_id = next(iter(self.rtmt._sessions._session_map.values()))

        with patch.object(session_manager_module, "_IDLE_TIMEOUT_SECONDS", -1):
            closer = asyncio.create_task(self.rtmt._sessions.close_idle_sessions())
            msg = await asyncio.wait_for(browser.receive(), 5)
            while msg.type is not WSMsgType.CLOSE:
                msg = await asyncio.wait_for(browser.receive(), 5)
            await browser.close()
            await asyncio.wait_for(closer, 5)

        self.assertEqual(msg.data, 4000)
        self.assertEqual(msg.extra, "idle_timeout")
        self.assertNotIn(session_id, order_state_singleton.sessions)


class BargeInFilterTests(unittest.IsolatedAsyncioTestCase):
    """PR #49 review round 5, "F1": `echo.on_barge_in` used to fire on the raw
    `_MARKER_RESPONSE_CANCEL in msg.data` substring check, evaluated on the
    browser's raw bytes *before* the frame was filtered/parsed -- so a frame
    merely *containing* the substring "response.cancel" somewhere (e.g. buried
    in an unrelated field of a session.update), without actually being that
    type, could still disable echo suppression. It's now keyed on the
    validated `sent_type` this coroutine already computes after
    `_process_message_to_server` has run -- the same seam review round 2's F1
    used for the idle-reset/nudge-cancel/greeting triggers -- so a dropped or
    unrelated frame triggers nothing.
    """

    async def asyncSetUp(self):
        self.fake = FakeGARealtime()
        self.fake_server = TestServer(self.fake.app())
        await self.fake_server.start_server()
        self.rtmt = RTMiddleTier(
            endpoint=str(self.fake_server.make_url("")),
            deployment="gpt-realtime-test",
            credentials=AzureKeyCredential("test-key"),
            voice_choice="shimmer",
        )
        self.rtmt.system_message = "sys"
        app = web.Application()
        self.rtmt.attach_to_app(app, "/realtime")
        self.client = TestClient(TestServer(app))
        await self.client.start_server()

    async def asyncTearDown(self):
        await self.client.close()
        await self.fake_server.close()

    async def _until(self, predicate, timeout=5.0):
        async def poll():
            while not predicate():
                await asyncio.sleep(0.01)
        await asyncio.wait_for(poll(), timeout)

    def _received_types(self):
        return [e.get("type") for e in self.fake.received]

    async def test_a_forged_substring_buried_in_an_unrelated_frame_does_not_trigger_barge_in(self):
        with patch.object(audio_pipeline.EchoSuppressor, "on_barge_in") as mock_barge_in:
            browser = await self.client.ws_connect("/realtime")
            await self._until(lambda: len(self.fake.received) >= 1)  # bootstrap landed

            # NOT a response.cancel -- a session.update whose *value* happens to
            # contain the substring "response.cancel". The old substring check
            # (`_MARKER_RESPONSE_CANCEL in msg.data`) would still have matched
            # this, since it never parsed the frame first.
            forged = (
                '{"type":"session.update","session":{"turn_detection":'
                '{"type":"server_vad"}},"note":"response.cancel"}'
            )
            await browser.send_str(forged)
            await self._until(lambda: self._received_types().count("session.update") >= 2)

            mock_barge_in.assert_not_called()

            # Sanity: a genuine response.cancel still triggers it.
            await browser.send_str(json.dumps({"type": "response.cancel"}))
            await self._until(lambda: "response.cancel" in self._received_types())
            mock_barge_in.assert_called_once()

            await browser.close()


class CompressionConfigTests(unittest.TestCase):

    def test_browser_socket_compression_defaults_off(self):
        self.assertIs(rtmt_module._WS_COMPRESS, False)

    def test_upstream_socket_does_not_offer_deflate(self):
        rtmt = RTMiddleTier("https://fake.openai.azure.com", "d", AzureKeyCredential("k"))
        captured = {}

        class _Stop(Exception):
            pass

        def fake_ws_connect(self_, *args, **kwargs):
            captured.update(kwargs)
            raise _Stop

        with patch.object(aiohttp.ClientSession, "ws_connect", fake_ws_connect):
            with self.assertRaises(_Stop):
                asyncio.run(rtmt._forward_messages(SimpleNamespace(headers={})))
        self.assertEqual(captured.get("compress"), 0)


if __name__ == "__main__":
    unittest.main()
