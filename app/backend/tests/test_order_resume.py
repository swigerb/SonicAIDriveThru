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
import logging
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

import rtmt as rtmt_module
import session_manager as session_manager_module
from order_state import order_state_singleton
from session_manager import IDLE_CLOSE_CODE, SessionManager, resume_id_fingerprint

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

    async def _until_event(self, browser, event_type, timeout=5.0, seen=None):
        """Read browser frames until one of `event_type`; frames read on the way go to `seen`."""
        async def recv():
            while True:
                msg = await browser.receive()
                if msg.type is not WSMsgType.TEXT:
                    raise AssertionError(f"socket closed while waiting for {event_type}: {msg.type} {msg.data}")
                event = json.loads(msg.data)
                if seen is not None:
                    seen.append(event)
                if event.get("type") == event_type:
                    return event
        return await asyncio.wait_for(recv(), timeout)

    async def _until_close(self, browser, timeout=5.0):
        async def recv():
            while True:
                msg = await browser.receive()
                if msg.type in (WSMsgType.CLOSE, WSMsgType.CLOSED, WSMsgType.CLOSING):
                    return msg
        return await asyncio.wait_for(recv(), timeout)

    async def _start_fresh(self):
        """A guest's first visit: connect, start talking, get the session metadata."""
        browser = await self.client.ws_connect("/realtime")
        await browser.send_json(BROWSER_SESSION_UPDATE)
        meta = await self._until_event(browser, "extension.session_metadata")
        await self._response_done(browser)      # greeting
        sid = self._sid_for_token(meta["sessionToken"])
        return browser, meta, sid

    def _sid_for_token(self, session_token: str) -> str:
        return next(s for s, v in order_state_singleton.sessions.items() if v["session_token"] == session_token)

    async def _drop(self, browser, sid):
        await browser.close()
        await self._until(lambda: self.sm.is_detached(sid))

    async def _resume(self, resume_id):
        browser = await self.client.ws_connect("/realtime")
        await browser.send_json({"type": "extension.resume", "resume_id": resume_id})
        return browser


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

    def test_resume_steals_from_a_still_attached_socket(self):
        stale = _ws()
        sid = self.sm.create_session(stale)
        resume_id = self.sm.issue_resume_id(sid)
        fresh = _ws()
        provisional = self.sm.create_session(fresh)
        outcome = self.sm.resume(fresh, resume_id)
        self.assertTrue(outcome.accepted)
        self.assertIs(outcome.stale_ws, stale)
        self.assertIsNone(self.sm.get_session_id(stale), "stale socket still mapped to the session")
        self.assertEqual(self.sm.get_session_id(fresh), sid)
        self.assertEqual(self.sm.active_session_count, 1)
        self.assertNotIn(provisional, order_state_singleton.sessions)

    def test_resume_of_an_idle_expired_session_is_rejected_before_the_sweep(self):
        stale = _ws()
        sid = self.sm.create_session(stale)
        resume_id = self.sm.issue_resume_id(sid)
        self.clock.advance(IDLE + 1)              # idle loop hasn't run yet
        outcome = self.sm.resume(_ws(), resume_id)
        self.assertFalse(outcome.accepted)
        self.assertEqual(outcome.reason, "expired")
        self.assertNotIn(sid, order_state_singleton.sessions)

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


class ResumeProtocolTests(_ResumeHarness):
    """extension.resume / session_resumed / resume_rejected over real sockets."""

    async def asyncSetUp(self):
        await super().asyncSetUp()
        self._idle = patch.object(session_manager_module, "_IDLE_TIMEOUT_SECONDS", IDLE)
        self._idle.start()
        self.sm.first_frame_timeout_seconds = 0.3

    async def asyncTearDown(self):
        self._idle.stop()
        await super().asyncTearDown()

    async def test_metadata_carries_a_256_bit_resume_id_once(self):
        browser, meta, sid = await self._start_fresh()
        self.assertIsInstance(meta.get("resumeId"), str)
        self.assertGreaterEqual(len(meta["resumeId"]), 43, "resume id is not 256-bit token_urlsafe(32)")
        more = await self._browser_events(browser, duration=0.4)
        types = [e["type"] for e in more]
        self.assertNotIn("extension.session_metadata", types)
        self.assertNotIn("extension.session_resumed", types)
        await browser.close()

    async def test_metadata_waits_for_the_first_frame_decision(self):
        self.sm.first_frame_timeout_seconds = 0.6
        browser = await self.client.ws_connect("/realtime")
        early = await self._browser_events(browser, duration=0.3)
        self.assertNotIn("extension.session_metadata", [e["type"] for e in early],
                         "metadata sent before the socket could present a resume id")
        meta = await self._until_event(browser, "extension.session_metadata", timeout=3)
        self.assertIn("resumeId", meta)
        await browser.close()

    async def test_valid_resume_reattaches_same_session_and_order(self):
        browser, meta, sid = await self._start_fresh()
        order_state_singleton.handle_order_update(sid, "add", "Cheeseburger", "", 2, 4.49)
        expected_order = json.loads(order_state_singleton.get_order_summary_json(sid))
        await self._drop(browser, sid)

        again = await self._resume(meta["resumeId"])
        seen: list[dict] = []
        resumed = await self._until_event(again, "extension.session_resumed", seen=seen)
        self.assertEqual(resumed["order_summary"], expected_order)
        self.assertEqual(resumed["order_summary"]["items"][0]["quantity"], 2)
        self.assertEqual(resumed["session_token"], meta["sessionToken"])
        self.assertEqual(resumed["round_trip_index"], order_state_singleton.sessions[sid]["round_trip_index"])
        self.assertIn("round_trip_token", resumed)
        self.assertNotEqual(resumed["resume_id"], meta["resumeId"], "resume id was not rotated")

        self.assertEqual(set(self.sm._session_map.values()), {sid}, "socket not re-attached to the held session")
        self.assertFalse(self.sm.is_detached(sid))
        self.assertEqual(list(self.sm._attached), [sid], "provisional session was not discarded")
        later = await self._browser_events(again, duration=0.5)
        self.assertNotIn("extension.session_metadata", [e["type"] for e in seen + later],
                         "a resumed socket must not also announce a fresh session")
        await again.close()

    async def test_wrong_id_is_rejected_and_starts_fresh(self):
        browser, meta, sid = await self._start_fresh()
        await self._drop(browser, sid)
        again = await self._resume("x" * 43)
        rejected = await self._until_event(again, "extension.resume_rejected")
        self.assertEqual(rejected["reason"], "unknown")
        fresh = await self._until_event(again, "extension.session_metadata")
        self.assertNotEqual(fresh["sessionToken"], meta["sessionToken"])
        self.assertNotEqual(fresh["resumeId"], meta["resumeId"])
        self.assertTrue(self.sm.is_detached(sid), "a wrong id disturbed the held session")
        await again.close()

    async def test_malformed_id_is_rejected(self):
        again = await self._resume(12345)
        rejected = await self._until_event(again, "extension.resume_rejected")
        self.assertEqual(rejected["reason"], "malformed")
        await self._until_event(again, "extension.session_metadata")
        await again.close()

    async def test_reused_id_is_rejected_but_the_rotated_one_works(self):
        browser, meta, sid = await self._start_fresh()
        await self._drop(browser, sid)
        second = await self._resume(meta["resumeId"])
        resumed = await self._until_event(second, "extension.session_resumed")
        await self._drop(second, sid)

        third = await self._resume(meta["resumeId"])
        rejected = await self._until_event(third, "extension.resume_rejected")
        self.assertEqual(rejected["reason"], "unknown", "a resume id was accepted twice")
        await third.close()

        fourth = await self._resume(resumed["resume_id"])
        again = await self._until_event(fourth, "extension.session_resumed")
        self.assertEqual(again["session_token"], meta["sessionToken"])
        await fourth.close()

    async def test_expired_grace_is_rejected(self):
        browser, meta, sid = await self._start_fresh()
        await self._drop(browser, sid)
        self.clock.advance(121)                       # sweep hasn't run yet
        again = await self._resume(meta["resumeId"])
        rejected = await self._until_event(again, "extension.resume_rejected")
        self.assertEqual(rejected["reason"], "expired")
        await self._until_event(again, "extension.session_metadata")
        self.assertNotIn(sid, order_state_singleton.sessions)
        await again.close()

    async def test_idle_closed_session_can_never_be_resumed(self):
        browser, meta, sid = await self._start_fresh()
        self.clock.advance(IDLE + 1)
        await self.sm.close_idle_sessions()
        closed = await self._until_close(browser)
        self.assertEqual(closed.data, IDLE_CLOSE_CODE)
        self.assertNotIn(sid, order_state_singleton.sessions, "idle close kept the order")

        again = await self._resume(meta["resumeId"])
        rejected = await self._until_event(again, "extension.resume_rejected")
        self.assertIn(rejected["reason"], ("unknown", "expired"))
        fresh = await self._until_event(again, "extension.session_metadata")
        self.assertNotEqual(fresh["sessionToken"], meta["sessionToken"])
        await again.close()

    async def test_resume_is_only_honoured_as_the_first_frame(self):
        browser, meta, sid = await self._start_fresh()
        await self._drop(browser, sid)

        other = await self.client.ws_connect("/realtime")
        await other.send_json(BROWSER_SESSION_UPDATE)
        own = await self._until_event(other, "extension.session_metadata")
        await other.send_json({"type": "extension.resume", "resume_id": meta["resumeId"]})
        seen: list[dict] = []
        rejected = await self._until_event(other, "extension.resume_rejected", seen=seen)
        self.assertEqual(rejected["reason"], "not_first_frame")
        self.assertNotIn("extension.session_resumed", [e["type"] for e in seen])
        self.assertTrue(self.sm.is_detached(sid), "a late resume frame stole the held session")
        # The browser drops its stored id on rejection, so its own session is re-announced.
        reannounced = await self._until_event(other, "extension.session_metadata")
        self.assertEqual(reannounced["sessionToken"], own["sessionToken"])
        self.assertNotEqual(reannounced["resumeId"], own["resumeId"])
        self.assertNotIn("extension.resume", [e["type"] for e in self.fake.received],
                         "extension.resume leaked to the model")
        await other.close()

    async def test_resume_frames_never_reach_the_model(self):
        browser, meta, sid = await self._start_fresh()
        await self._drop(browser, sid)
        other = await self.client.ws_connect("/realtime")
        await other.send_json(BROWSER_SESSION_UPDATE)
        await self._until_event(other, "extension.session_metadata")
        await other.send_json({"type": "extension.resume", "resume_id": meta["resumeId"]})
        await self._until_event(other, "extension.resume_rejected")
        # A later frame on the same socket reaching upstream proves the resume frame was handled first.
        upstream = self.fake.connections[-1]
        before = sum(e["type"] == "session.update" for e in upstream)
        await other.send_json(BROWSER_SESSION_UPDATE)
        await self._until(lambda: sum(e["type"] == "session.update" for e in upstream) > before)
        self.assertNotIn("extension.resume", [e["type"] for e in self.fake.received])
        await other.close()

    async def test_rejection_after_upstream_is_ready_still_announces_the_fresh_session(self):
        browser = await self.client.ws_connect("/realtime")
        await self._until(lambda: len(self.fake.upstreams) >= 1)
        await asyncio.sleep(0.1)                   # session.created already relayed
        await browser.send_json({"type": "extension.resume", "resume_id": "z" * 43})
        await self._until_event(browser, "extension.resume_rejected")
        meta = await self._until_event(browser, "extension.session_metadata", timeout=2)
        self.assertIn("resumeId", meta)
        await browser.close()

    async def test_resume_after_the_first_frame_timeout_is_rejected(self):
        browser, meta, sid = await self._start_fresh()
        await self._drop(browser, sid)
        late = await self.client.ws_connect("/realtime")
        await self._until_event(late, "extension.session_metadata")      # timeout decided "fresh"
        await late.send_json({"type": "extension.resume", "resume_id": meta["resumeId"]})
        rejected = await self._until_event(late, "extension.resume_rejected")
        self.assertEqual(rejected["reason"], "not_first_frame")
        self.assertTrue(self.sm.is_detached(sid))
        await late.close()

    async def test_stale_attached_socket_is_closed_with_4002(self):
        stale, meta, sid = await self._start_fresh()        # never closed: half-open
        fresh = await self._resume(meta["resumeId"])
        resumed = await self._until_event(fresh, "extension.session_resumed")
        self.assertEqual(resumed["session_token"], meta["sessionToken"])

        closed = await self._until_close(stale)
        self.assertEqual(closed.data, 4002)
        self.assertEqual(closed.extra, "superseded")
        await asyncio.sleep(0.1)                            # stale handler's finally runs
        self.assertFalse(self.sm.is_detached(sid), "superseded socket detached the resumed session")
        self.assertIn(sid, order_state_singleton.sessions)
        self.assertEqual(set(self.sm._session_map.values()), {sid})
        await fresh.close()

    async def test_end_session_deletes_order_and_closes_1000(self):
        browser, meta, sid = await self._start_fresh()
        await browser.send_json({"type": "extension.end_session"})
        closed = await self._until_close(browser)
        self.assertEqual(closed.data, 1000)
        self.assertEqual(closed.extra, "session_ended")
        await asyncio.sleep(0.05)
        self.assertNotIn(sid, order_state_singleton.sessions)
        self.assertFalse(self.sm.is_detached(sid))

    async def test_resume_disabled_rejects(self):
        self.sm.resume_enabled = False
        browser = await self.client.ws_connect("/realtime")
        await browser.send_json(BROWSER_SESSION_UPDATE)
        meta = await self._until_event(browser, "extension.session_metadata")
        self.assertNotIn("resumeId", meta)
        await browser.close()
        again = await self._resume("y" * 43)
        rejected = await self._until_event(again, "extension.resume_rejected")
        self.assertEqual(rejected["reason"], "disabled")
        await again.close()


class _Capture(logging.Handler):
    def __init__(self):
        super().__init__(level=logging.DEBUG)
        self.lines: list[str] = []

    def emit(self, record):
        self.lines.append(record.getMessage())


class ResumeIdNeverLoggedTests(_ResumeHarness):

    async def test_resume_id_never_appears_in_logs(self):
        capture = _Capture()
        names = ["", "sonic-drive-in", "sonic-verbose", "aiohttp", "aiohttp.access", "aiohttp.server", "aiohttp.web"]
        saved = {n: logging.getLogger(n).level for n in names}
        for n in names:
            logging.getLogger(n).setLevel(logging.DEBUG)
        logging.getLogger().addHandler(capture)
        try:
            with patch.object(session_manager_module, "_IDLE_TIMEOUT_SECONDS", IDLE), \
                    patch.object(rtmt_module, "_VERBOSE_GLOBAL", True):
                browser, meta, sid = await self._start_fresh()
                await self._drop(browser, sid)
                again = await self._resume(meta["resumeId"])
                resumed = await self._until_event(again, "extension.session_resumed")
                await self._drop(again, sid)
                reused = await self._resume(meta["resumeId"])
                await self._until_event(reused, "extension.resume_rejected")
                await reused.send_json({"type": "extension.resume", "resume_id": resumed["resume_id"]})
                await self._until_event(reused, "extension.resume_rejected")
                await reused.close()
        finally:
            logging.getLogger().removeHandler(capture)
            for n, level in saved.items():
                logging.getLogger(n).setLevel(level)

        text = "\n".join(capture.lines)
        self.assertTrue(capture.lines, "nothing captured; the test is not looking at the logs")
        for secret in (meta["resumeId"], resumed["resume_id"]):
            self.assertNotIn(secret, text, "a raw resume id was logged")
        self.assertIn(resume_id_fingerprint(meta["resumeId"]), text, "resume events should log the sha256 prefix")


if __name__ == "__main__":
    unittest.main()
