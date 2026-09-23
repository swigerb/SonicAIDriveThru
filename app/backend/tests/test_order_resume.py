"""Order resume after a transport drop (plan: order-resume, Brian's decisions).

Lifecycle rules under test:
  * a transport close detaches the session and holds the order for the grace
    period (resume.grace_seconds, 120s);
  * an idle close (4000) ends the session immediately and permanently;
  * the 5-minute idle clock keeps running while detached, so the hold is
    min(grace, remaining idle budget);
  * detached sessions are LRU-capped and don't count against the concurrency cap;
  * only guest activity (not the constant mic stream) moves the idle clock.

The end-to-end tests drive the real middle tier through aiohttp TestServer
against the fake GA realtime server from test_session_bootstrap.
"""

import asyncio
import json
import sys
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock, patch

sys.path.append(str(Path(__file__).resolve().parents[1]))
sys.path.append(str(Path(__file__).resolve().parent))

from aiohttp import WSMsgType, web
from test_session_bootstrap import (
    BROWSER_SESSION_UPDATE,
    MIC_FRAME,
    FakeGARealtime,
    _RealtimeHarness,
)

import session_manager as session_manager_module
from order_state import order_state_singleton
from session_manager import IDLE_CLOSE_CODE, SessionManager

IDLE = 300


class FakeClock:
    def __init__(self, t: float = 1000.0):
        self.t = t

    def __call__(self) -> float:
        return self.t

    def advance(self, seconds: float) -> None:
        self.t += seconds


def _ws():
    ws = MagicMock()
    ws.close = AsyncMock()
    return ws


class FakeGAPerConnection(FakeGARealtime):
    """FakeGARealtime that also records each upstream connection separately and
    lets a test push events down a live upstream (speech_started, transcripts)."""

    def __init__(self):
        super().__init__()
        self.connections: list[list[dict]] = []
        self.upstreams: list[web.WebSocketResponse] = []
        # Hold back session.updated until the test releases it.
        self.withhold_session_updated = False
        self.held_session_updated: list[tuple[web.WebSocketResponse, dict]] = []

    async def handler(self, request: web.Request) -> web.WebSocketResponse:
        ws = web.WebSocketResponse()
        await ws.prepare(request)
        log: list[dict] = []
        self.connections.append(log)
        self.upstreams.append(ws)
        await ws.send_json({"type": "session.created", "session": {"type": "realtime", **self._snapshot()}})
        async for msg in ws:
            event = json.loads(msg.data)
            self.received.append(event)
            log.append(event)
            kind = event.get("type")
            if kind == "session.update":
                if self.withhold_session_updated:
                    self.held_session_updated.append((ws, event))
                    continue
                await self._session_update(ws, event["session"], event.get("event_id"))
            elif kind == "input_audio_buffer.append" and not self.vad_fired:
                self.vad_fired = True
                await self._respond(ws)
            elif kind == "response.create":
                await self._respond(ws)
        return ws

    async def release_session_updated(self) -> None:
        self.withhold_session_updated = False
        held, self.held_session_updated = self.held_session_updated, []
        for ws, event in held:
            await self._session_update(ws, event["session"], event.get("event_id"))


class _ResumeHarness(_RealtimeHarness):

    fake_class = FakeGAPerConnection

    async def asyncSetUp(self):
        await super().asyncSetUp()
        self.clock = FakeClock()
        self.sm: SessionManager = self.rtmt._sessions
        self.sm._clock = self.clock

    async def _connect(self):
        browser = await self.client.ws_connect("/realtime")
        await self._until(lambda: len(self.fake.upstreams) >= 1 and self.sm.active_session_count >= 1)
        return browser

    def _only_session(self) -> str:
        return next(iter(self.sm._session_map.values()))


class GraceHoldLifecycleTests(unittest.TestCase):
    """SessionManager rules with an injected clock (no real sleeps)."""

    def setUp(self):
        self.clock = FakeClock()
        self.sm = SessionManager(clock=self.clock)
        self.sm.grace_seconds = 120
        self.sm.max_detached = 20
        self._idle = patch.object(session_manager_module, "_IDLE_TIMEOUT_SECONDS", IDLE)
        self._idle.start()

    def tearDown(self):
        self._idle.stop()
        for sid in list(order_state_singleton.sessions):
            self.sm.end_session(sid, "test teardown")

    def test_transport_close_keeps_order_for_grace_then_deletes(self):
        ws = _ws()
        sid = self.sm.create_session(ws)
        self.sm.detach_session(ws, sid)
        self.assertIn(sid, order_state_singleton.sessions)
        self.assertTrue(self.sm.is_detached(sid))
        self.assertEqual(self.sm.active_session_count, 0)

        self.clock.advance(119)
        self.sm.sweep_detached()
        self.assertIn(sid, order_state_singleton.sessions, "order dropped before the grace period ended")

        self.clock.advance(2)
        self.sm.sweep_detached()
        self.assertNotIn(sid, order_state_singleton.sessions, "order held past the grace period")
        self.assertFalse(self.sm.is_detached(sid))

    def test_grace_is_capped_by_remaining_idle_budget(self):
        ws = _ws()
        sid = self.sm.create_session(ws)          # guest activity at t0
        self.clock.advance(250)                   # 250s of silence, then the socket drops
        self.sm.detach_session(ws, sid)
        self.assertEqual(self.sm.detached_expires_at(sid) - self.clock(), 50,
                         "hold must be min(120s grace, 50s left of the 5-minute idle budget)")

        self.clock.advance(49)
        self.sm.sweep_detached()
        self.assertIn(sid, order_state_singleton.sessions)
        self.clock.advance(2)                     # 301s since the guest's last activity
        self.sm.sweep_detached()
        self.assertNotIn(sid, order_state_singleton.sessions,
                         "a disconnect extended the 5-minute idle limit")

    def test_drop_after_idle_budget_is_spent_ends_immediately(self):
        ws = _ws()
        sid = self.sm.create_session(ws)
        self.clock.advance(IDLE + 1)
        self.sm.detach_session(ws, sid)
        self.assertNotIn(sid, order_state_singleton.sessions)
        self.assertFalse(self.sm.is_detached(sid))

    def test_idle_close_deletes_immediately_and_is_not_detached(self):
        ws = _ws()
        sid = self.sm.create_session(ws)
        self.clock.advance(IDLE + 1)
        asyncio.run(self.sm.close_idle_sessions())
        ws.close.assert_awaited_once()
        self.assertEqual(ws.close.await_args.kwargs["code"], IDLE_CLOSE_CODE)
        self.assertNotIn(sid, order_state_singleton.sessions)
        # The socket's own close handling runs afterwards; it must not resurrect a hold.
        self.sm.detach_session(ws, sid)
        self.assertFalse(self.sm.is_detached(sid))
        self.assertNotIn(sid, order_state_singleton.sessions)

    def test_lru_cap_evicts_the_oldest_detached_session(self):
        self.sm.max_detached = 2
        sids = []
        for _ in range(3):
            ws = _ws()
            sid = self.sm.create_session(ws)
            self.clock.advance(1)
            self.sm.detach_session(ws, sid)
            sids.append(sid)
        self.assertEqual(self.sm.detached_session_count, 2)
        self.assertNotIn(sids[0], order_state_singleton.sessions, "oldest held session was not evicted")
        self.assertIn(sids[1], order_state_singleton.sessions)
        self.assertIn(sids[2], order_state_singleton.sessions)

    def test_concurrency_cap_ignores_detached_sessions(self):
        with patch.object(session_manager_module, "_MAX_CONCURRENT_SESSIONS", 1):
            ws = _ws()
            sid = self.sm.create_session(ws)
            self.assertFalse(self.sm.can_accept_session())
            self.sm.detach_session(ws, sid)
            self.assertTrue(self.sm.is_detached(sid))
            self.assertTrue(self.sm.can_accept_session(), "a grace-held session blocked a new guest")

    def test_resume_disabled_ends_on_transport_close(self):
        self.sm.resume_enabled = False
        ws = _ws()
        sid = self.sm.create_session(ws)
        self.sm.detach_session(ws, sid)
        self.assertNotIn(sid, order_state_singleton.sessions)

    def test_detach_of_a_socket_that_no_longer_owns_the_session_is_a_noop(self):
        ws = _ws()
        sid = self.sm.create_session(ws)
        other = _ws()
        self.sm.detach_session(other, sid)
        self.assertEqual(self.sm.get_session_id(ws), sid)
        self.assertFalse(self.sm.is_detached(sid))

    def test_shipped_resume_config(self):
        cfg = session_manager_module._resume_cfg
        self.assertEqual(cfg["grace_seconds"], 120)
        self.assertEqual(cfg["nudge_after_seconds"], 30)
        self.assertTrue(cfg["enabled"])
        self.assertNotIn("resume_after_idle", cfg)
        self.assertEqual(session_manager_module._security_cfg["idle_timeout_seconds"], 300)


class GraceHoldEndToEndTests(_ResumeHarness):

    async def asyncSetUp(self):
        await super().asyncSetUp()
        self._idle = patch.object(session_manager_module, "_IDLE_TIMEOUT_SECONDS", IDLE)
        self._idle.start()

    async def asyncTearDown(self):
        self._idle.stop()
        await super().asyncTearDown()

    async def test_browser_drop_keeps_order_for_grace_then_deletes(self):
        browser = await self._connect()
        sid = self._only_session()
        await browser.close()
        await self._until(lambda: self.sm.is_detached(sid))
        self.assertIn(sid, order_state_singleton.sessions)

        self.clock.advance(121)
        await self.sm.close_idle_sessions()
        self.assertNotIn(sid, order_state_singleton.sessions)

    async def test_idle_close_over_the_socket_is_permanent(self):
        browser = await self._connect()
        sid = self._only_session()
        self.clock.advance(IDLE + 1)
        await self.sm.close_idle_sessions()
        msg = await asyncio.wait_for(browser.receive(), 5)
        while msg.type is not WSMsgType.CLOSE:
            msg = await asyncio.wait_for(browser.receive(), 5)
        self.assertEqual(msg.data, IDLE_CLOSE_CODE)
        await self._until(lambda: self.sm.active_session_count == 0)
        await asyncio.sleep(0.05)
        self.assertFalse(self.sm.is_detached(sid))
        self.assertNotIn(sid, order_state_singleton.sessions)

    async def test_new_guest_admitted_while_another_is_grace_held(self):
        with patch.object(session_manager_module, "_MAX_CONCURRENT_SESSIONS", 1):
            first = await self._connect()
            sid = self._only_session()
            await first.close()
            await self._until(lambda: self.sm.is_detached(sid))

            second = await self.client.ws_connect("/realtime")
            events = await self._browser_events(second, duration=0.5)
            self.assertNotIn("error", [e.get("type") for e in events], "rejected as busy")
            self.assertEqual(self.sm.active_session_count, 1)
            await second.close()

    async def test_mic_stream_is_not_guest_activity_but_speech_is(self):
        browser = await self._connect()
        sid = self._only_session()
        start = self.sm._last_activity[sid]

        self.clock.advance(100)
        await browser.send_json(MIC_FRAME)
        await self._response_done(browser)
        self.assertEqual(self.sm._last_activity[sid], start, "silent mic audio reset the idle clock")

        self.clock.advance(100)
        await self.fake.upstreams[0].send_json({"type": "input_audio_buffer.speech_started"})
        await self._until(lambda: self.sm._last_activity[sid] == start + 200)

        self.clock.advance(10)
        await self.fake.upstreams[0].send_json(
            {"type": "conversation.item.input_audio_transcription.completed", "item_id": "i1", "transcript": "a burger"})
        await self._until(lambda: self.sm._last_activity[sid] == start + 210)

        self.clock.advance(10)
        await browser.send_json(BROWSER_SESSION_UPDATE)
        await self._until(lambda: self.sm._last_activity[sid] == start + 220)
        await browser.close()


if __name__ == "__main__":
    unittest.main()
