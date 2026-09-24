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
            elif kind == "conversation.item.create":
                # Real GA acknowledges every conversation.item.create with a
                # conversation.item.created echo carrying the same item back
                # down the same socket -- including our own rehydration/nudge
                # system items (see RehydrationAndNudgeTests). Mirror that so
                # tests can prove the middle tier suppresses it before the
                # browser side (swigerb/SonicAIDriveThru#29).
                await ws.send_json({
                    "type": "conversation.item.created",
                    "previous_item_id": event.get("previous_item_id"),
                    "item": event["item"],
                })
                # GA also emits conversation.item.done "when the item is
                # finalized", carrying the full item a second time on a
                # separate event type (swigerb/SonicAIDriveThru#29 follow-up,
                # PR #30 review "M1") -- Rick's review proved the existing
                # suppression missed this event entirely. Mirror it too so
                # the Python suite can catch a regression the same way the
                # conformance harness does (see M2).
                await ws.send_json({
                    "type": "conversation.item.done",
                    "previous_item_id": event.get("previous_item_id"),
                    "item": event["item"],
                })
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


class TranscriptBufferTests(unittest.TestCase):

    def setUp(self):
        self.sm = SessionManager(clock=FakeClock())
        self.sid = self.sm.create_session(_ws())

    def tearDown(self):
        self.sm.end_session(self.sid)

    def test_ring_buffer_keeps_the_last_n_turns(self):
        self.sm.history_turns = 6
        for i in range(10):
            self.sm.record_turn(self.sid, "guest" if i % 2 else "carhop", f"turn {i}")
        self.assertEqual([t for _, t in self.sm.recent_turns(self.sid)], [f"turn {i}" for i in range(4, 10)])

    def test_history_is_char_capped_keeping_the_newest(self):
        self.sm.history_chars = 50
        for i in range(5):
            self.sm.record_turn(self.sid, "guest", f"{i}" * 30)
        turns = self.sm.recent_turns(self.sid)
        self.assertLessEqual(sum(len(t) for _, t in turns), 50)
        self.assertEqual(turns[-1][1], "4" * 30, "newest turn must survive the cap")

    def test_rehydration_item_is_one_system_message_with_the_order(self):
        order_state_singleton.handle_order_update(self.sid, "add", "Cheeseburger", "", 1, 4.49)
        self.sm.record_turn(self.sid, "guest", "a cheeseburger please")
        self.sm.record_turn(self.sid, "carhop", "One cheeseburger, anything else?")
        item = json.loads(self.sm.build_rehydration_item(self.sid))
        self.assertEqual(item["type"], "conversation.item.create")
        self.assertEqual(item["item"]["role"], "system")
        text = item["item"]["content"][0]["text"]
        self.assertIn(order_state_singleton.get_order_summary_json(self.sid), text)
        self.assertIn("Guest: a cheeseburger please", text)
        self.assertIn("Carhop: One cheeseburger, anything else?", text)
        self.assertIn("Do NOT greet", text)

    def test_ended_session_forgets_its_transcript(self):
        self.sm.record_turn(self.sid, "guest", "hello")
        self.sm.end_session(self.sid)
        self.assertEqual(self.sm.recent_turns(self.sid), [])


NUDGE_MARKER = "quiet since their connection came back"


class RehydrationAndNudgeTests(_ResumeHarness):

    async def asyncSetUp(self):
        await super().asyncSetUp()
        self._idle = patch.object(session_manager_module, "_IDLE_TIMEOUT_SECONDS", IDLE)
        self._idle.start()
        self.sm.first_frame_timeout_seconds = 0.3
        self.sm.nudge_after_seconds = 0

    async def asyncTearDown(self):
        self._idle.stop()
        await super().asyncTearDown()

    async def _converse_then_drop(self):
        browser, meta, sid = await self._start_fresh()          # greeting: carhop says "hi"
        await self.fake.upstreams[-1].send_json({
            "type": "conversation.item.input_audio_transcription.completed",
            "item_id": "i1", "transcript": "I'd like a cheeseburger"})
        await self._until(lambda: any(r == "guest" for r, _ in self.sm.recent_turns(sid)))
        order_state_singleton.handle_order_update(sid, "add", "Cheeseburger", "", 1, 4.49)
        await self._drop(browser, sid)
        return meta, sid

    async def _resume_ok(self, resume_id):
        browser = await self._resume(resume_id)
        await self._until_event(browser, "extension.session_resumed")
        return browser, self.fake.connections[-1]

    @staticmethod
    def _system_texts(upstream):
        return [e["item"]["content"][0]["text"] for e in upstream
                if e.get("type") == "conversation.item.create" and e["item"].get("role") == "system"]

    def _nudges(self, upstream):
        return [t for t in self._system_texts(upstream) if NUDGE_MARKER in t]

    def _greeting_present(self, upstream):
        """Whether a greeting item (role="user", the exact configured greeting
        text) was sent upstream. Compares text, not the whole dict/exact id:
        build_greeting_msg (PR #30 review "G1"/item 1) stamps a brand-new
        middle-tier item id on every call, so two greetings (or a greeting
        compared against a fresh self.sm.build_greeting_msg() read) never
        compare equal by id even when they are, in every way that matters,
        "the same greeting" repeated."""
        greeting_text = json.loads(self.sm.build_greeting_msg())["item"]["content"][0]["text"]
        return any(e.get("type") == "conversation.item.create" and e["item"].get("role") == "user"
                   and e["item"]["content"][0]["text"] == greeting_text for e in upstream)

    async def test_upstream_gets_bootstrap_then_rehydration_and_no_greeting(self):
        meta, sid = await self._converse_then_drop()
        order_json = order_state_singleton.get_order_summary_json(sid)
        browser, upstream = await self._resume_ok(meta["resumeId"])
        await browser.send_json(BROWSER_SESSION_UPDATE)          # the browser restarting its session
        await self._until(lambda: sum(e["type"] == "session.update" for e in upstream) >= 2)
        await browser.send_json(MIC_FRAME)                        # guest speaks -> VAD response
        await self._response_done(browser)

        types = [e["type"] for e in upstream]
        self.assertEqual(types[0], "session.update", "bootstrap session.update must be first")
        self.assertEqual([t["name"] for t in upstream[0]["session"]["tools"]], ["search", "update_order", "get_order", "reset_order"])
        self.assertEqual(upstream[1]["type"], "conversation.item.create")
        self.assertEqual(upstream[1]["item"]["role"], "system", "second upstream frame must be the rehydration item")
        text = upstream[1]["item"]["content"][0]["text"]
        self.assertIn(order_json, text)
        self.assertIn("Guest: I'd like a cheeseburger", text)
        self.assertIn("Carhop: hi", text)
        self.assertEqual(len(self._system_texts(upstream)), 1, "exactly one rehydration item")
        self.assertNotIn("response.create", types, "the carhop spoke unprompted after a resume")
        self.assertFalse(self._greeting_present(upstream), "greeting repeated on resume")
        self.assertLess(types.index("conversation.item.create"), types.index("input_audio_buffer.append"))
        await browser.close()

    async def test_browser_never_receives_the_rehydration_system_item(self):
        """swigerb/SonicAIDriveThru#29: upstream (per FakeGAPerConnection, which
        now mirrors real GA's ack behaviour) echoes the rehydration item back
        via conversation.item.created *and* conversation.item.done (GA emits
        both -- swigerb/SonicAIDriveThru#29 follow-up, PR #30 review "M1"/"M2")
        on the same socket -- the middle tier must swallow both frames
        rather than relay them, since they carry the recent transcript and
        order JSON, not something the guest should see on their own screen.

        Rick's PR #30 review proved that without the .done handling, this
        test passed anyway (the leak was on the .done event this test didn't
        check for) -- .done is asserted here specifically so a regression on
        either event type fails it."""
        meta, sid = await self._converse_then_drop()
        browser, upstream = await self._resume_ok(meta["resumeId"])
        # Drain everything the browser receives while the rehydration item
        # round-trips through the (now echoing) fake upstream.
        seen = await self._browser_events(browser, duration=0.5)
        self.assertTrue(
            any(e.get("type") == "conversation.item.create" and e["item"].get("role") == "system" for e in upstream),
            "precondition: the fake upstream must have echoed the rehydration item for this test to mean anything",
        )
        leaked = [e for e in seen
                  if e.get("type") in ("conversation.item.created", "conversation.item.added", "conversation.item.done")
                  and e.get("item", {}).get("role") == "system"]
        self.assertEqual(leaked, [], "the browser must never see a server-authored system conversation item")
        await browser.close()

    async def test_resume_before_the_conversation_started_keeps_the_normal_greeting(self):
        browser = await self.client.ws_connect("/realtime")
        meta = await self._until_event(browser, "extension.session_metadata")   # never started talking
        sid = self._sid_for_token(meta["sessionToken"])
        await self._drop(browser, sid)
        again, upstream = await self._resume_ok(meta["resumeId"])
        await again.send_json(BROWSER_SESSION_UPDATE)
        await self._response_done(again)
        self.assertTrue(self._greeting_present(upstream))
        self.assertEqual(self._system_texts(upstream), [])
        await again.close()

    async def test_nudge_fires_once_after_silence_and_only_after_session_updated(self):
        meta, sid = await self._converse_then_drop()
        self.sm.nudge_after_seconds = 0.2
        self.fake.withhold_session_updated = True
        browser, upstream = await self._resume_ok(meta["resumeId"])
        activity = self.sm._last_activity[sid]
        self.clock.advance(5)

        await asyncio.sleep(0.5)
        self.assertEqual(self._nudges(upstream), [], "nudged before session.updated confirmed voice/tools")
        self.assertNotIn("response.create", [e["type"] for e in upstream])

        await self.fake.release_session_updated()
        await self._until(lambda: "response.create" in [e["type"] for e in upstream])
        await self._response_done(browser)
        await asyncio.sleep(0.4)
        types = [e["type"] for e in upstream]
        self.assertEqual(len(self._nudges(upstream)), 1, "nudge must fire exactly once")
        self.assertEqual(types.count("response.create"), 1)
        nudge_index = next(i for i, e in enumerate(upstream)
                           if e.get("type") == "conversation.item.create" and NUDGE_MARKER in json.dumps(e))
        self.assertLess(nudge_index, types.index("response.create"))
        self.assertEqual(self.sm._last_activity[sid], activity, "the nudge counted as guest activity")
        await browser.close()

    async def _assert_nudge_cancelled_by(self, guest_action):
        meta, sid = await self._converse_then_drop()
        self.sm.nudge_after_seconds = 0.3
        browser, upstream = await self._resume_ok(meta["resumeId"])
        await guest_action(browser)
        await asyncio.sleep(0.7)
        self.assertEqual(self._nudges(upstream), [], "nudge fired although the guest spoke")
        await browser.close()

    async def test_nudge_cancelled_by_guest_speech(self):
        async def speak(_browser):
            await self.fake.upstreams[-1].send_json({"type": "input_audio_buffer.speech_started"})
        await self._assert_nudge_cancelled_by(speak)

    async def test_nudge_cancelled_by_guest_transcript(self):
        async def transcript(_browser):
            await self.fake.upstreams[-1].send_json({
                "type": "conversation.item.input_audio_transcription.completed", "item_id": "i2", "transcript": "and a drink"})
        await self._assert_nudge_cancelled_by(transcript)

    async def test_nudge_cancelled_by_guest_initiated_response(self):
        async def respond(browser):
            await browser.send_json({"type": "response.create"})
        await self._assert_nudge_cancelled_by(respond)

    async def test_nudge_disabled_with_zero(self):
        meta, sid = await self._converse_then_drop()
        self.sm.nudge_after_seconds = 0
        browser, upstream = await self._resume_ok(meta["resumeId"])
        await asyncio.sleep(0.4)
        self.assertEqual(self._nudges(upstream), [])
        await browser.close()


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
