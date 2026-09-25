"""Rate-limit recovery (round 3, R1): a rate-limited model response must not
leave the guest listening to silence.

Unit tests cover detection, the retry-hint parsing/clamping and the settings.
The end-to-end tests drive the real middle tier (aiohttp TestServer) against a
fake GA realtime server that fails the next N responses with
`rate_limit_exceeded`. The retry delay goes through an injected sleep, so no
test really waits for it.
"""

import asyncio
import json
import logging
import re
import sys
import unittest
import wave
from array import array
from pathlib import Path
from unittest.mock import AsyncMock, patch

from azure.core.credentials import AzureKeyCredential

sys.path.append(str(Path(__file__).resolve().parents[1]))
sys.path.append(str(Path(__file__).resolve().parent))

from test_order_resume import NUDGE_MARKER, FakeGAPerConnection, _ResumeHarness

import session_manager as session_manager_module
from rate_limit import (
    FIRST_RETRY_BOUNDS,
    SECOND_RETRY_BOUNDS,
    RateLimitRecovery,
    RateLimitSettings,
    is_rate_limit_error,
    parse_retry_hint,
    rate_limit_error_of_error_event,
    rate_limit_error_of_response_done,
    retry_delay,
)
from rtmt import RTMiddleTier, ToolResult, ToolResultDirection, _SessionUpdateGuard

RATE_LIMIT_ERROR = {"type": "rate_limit_error", "code": "rate_limit_exceeded",
                    "message": "Rate limit reached for gpt-realtime-2.1."}


def failed_done(error=None, response_id="resp_failed"):
    return {"type": "response.done", "response": {
        "id": response_id, "status": "failed", "output": [],
        "status_details": {"type": "failed", "error": error or RATE_LIMIT_ERROR}}}


class DetectionTests(unittest.TestCase):

    def test_rate_limit_is_recognised_by_code_or_type(self):
        self.assertTrue(is_rate_limit_error({"code": "rate_limit_exceeded"}))
        self.assertTrue(is_rate_limit_error({"type": "rate_limit_error", "code": None}))
        self.assertTrue(is_rate_limit_error({"type": "tokens", "code": "RATE_LIMIT_EXCEEDED"}))
        for other in ({"code": "server_error", "type": "server_error"}, {"code": None}, {}, None, "rate_limit"):
            self.assertFalse(is_rate_limit_error(other), other)

    def test_response_done_needs_failed_status_and_a_rate_limit_error(self):
        self.assertEqual(rate_limit_error_of_response_done(failed_done()), RATE_LIMIT_ERROR)
        completed = failed_done()
        completed["response"]["status"] = "completed"
        self.assertIsNone(rate_limit_error_of_response_done(completed))
        self.assertIsNone(rate_limit_error_of_response_done(failed_done({"type": "server_error", "code": "server_error"})))
        self.assertIsNone(rate_limit_error_of_response_done({"type": "response.done", "response": {"status": "failed"}}))
        self.assertIsNone(rate_limit_error_of_response_done({"type": "response.done"}))

    def test_error_event(self):
        self.assertEqual(rate_limit_error_of_error_event({"type": "error", "error": RATE_LIMIT_ERROR}), RATE_LIMIT_ERROR)
        self.assertIsNone(rate_limit_error_of_error_event({"type": "error", "error": {"code": "item_not_found"}}))


class RetryHintTests(unittest.TestCase):

    def test_parses_seconds_and_milliseconds(self):
        cases = {
            "Rate limit reached. Please try again in 3s.": 3.0,
            "Please try again in 1.234 seconds.": 1.234,
            "try again in 250ms": 0.25,
            "Try again in 20 sec": 20.0,
            "Please retry after 7 seconds.": 7.0,
            "retry after 1500 milliseconds": 1.5,
        }
        for text, expected in cases.items():
            with self.subTest(text):
                self.assertAlmostEqual(parse_retry_hint(text), expected)

    def test_no_hint(self):
        for text in ("Rate limit reached.", "", None, 42, "try again later", "wait 3s"):
            self.assertIsNone(parse_retry_hint(text), text)

    def test_clamping(self):
        self.assertEqual(retry_delay(None, 1.5, FIRST_RETRY_BOUNDS), 1.5)
        self.assertEqual(retry_delay(0.1, 1.5, FIRST_RETRY_BOUNDS), 0.5)
        self.assertEqual(retry_delay(3.0, 1.5, FIRST_RETRY_BOUNDS), 3.0)
        self.assertEqual(retry_delay(20.0, 1.5, FIRST_RETRY_BOUNDS), 5.0)
        self.assertEqual(retry_delay(None, 4.0, SECOND_RETRY_BOUNDS), 4.0)
        self.assertEqual(retry_delay(0.25, 4.0, SECOND_RETRY_BOUNDS), 2.0)
        self.assertEqual(retry_delay(6.0, 4.0, SECOND_RETRY_BOUNDS), 6.0)
        self.assertEqual(retry_delay(60.0, 4.0, SECOND_RETRY_BOUNDS), 8.0)


class SettingsTests(unittest.TestCase):

    def test_shipped_config(self):
        from config_loader import get_config
        s = RateLimitSettings.from_config(get_config(), {})
        self.assertEqual(s, RateLimitSettings(enabled=True, retry_delay_seconds=1.5,
                                              second_retry_delay_seconds=4.0, max_retries=2))

    def test_env_overrides_enabled(self):
        cfg = {"resilience": {"rate_limit": {"enabled": True, "retry_delay_seconds": 2, "max_retries": 1}}}
        self.assertFalse(RateLimitSettings.from_config(cfg, {"RATE_LIMIT_RECOVERY_ENABLED": "false"}).enabled)
        off = {"resilience": {"rate_limit": {"enabled": False}}}
        self.assertTrue(RateLimitSettings.from_config(off, {"RATE_LIMIT_RECOVERY_ENABLED": "true"}).enabled)
        self.assertFalse(RateLimitSettings.from_config(off, {"RATE_LIMIT_RECOVERY_ENABLED": ""}).enabled)
        custom = RateLimitSettings.from_config(cfg, {})
        self.assertEqual((custom.retry_delay_seconds, custom.max_retries), (2.0, 1))

    def test_middle_tier_reads_the_env_override(self):
        with patch.dict("os.environ", {"RATE_LIMIT_RECOVERY_ENABLED": "false"}):
            rtmt = RTMiddleTier("https://x", "gpt-realtime-2.1", AzureKeyCredential("k"))
        self.assertFalse(rtmt.rate_limit_settings.enabled)


class GuardTests(unittest.TestCase):

    def test_uncorrelated_rate_limit_error_is_not_blamed_on_an_in_flight_session_update(self):
        guard = _SessionUpdateGuard()
        guard.track(json.dumps({"type": "session.update", "session": {}}))
        rate_limited = {"type": "error", "error": {**RATE_LIMIT_ERROR, "type": "invalid_request_error"}}
        self.assertIsNone(guard.correlate(rate_limited))
        # ...while an ordinary uncorrelated invalid_request_error still is.
        self.assertIsNotNone(guard.correlate({"type": "error", "error": {"type": "invalid_request_error"}}))

    def test_rate_limit_error_echoing_our_event_id_is_still_correlated(self):
        guard = _SessionUpdateGuard()
        guard.track(json.dumps({"type": "session.update", "event_id": "su_1", "session": {}}))
        self.assertEqual(guard.correlate({"type": "error", "error": {**RATE_LIMIT_ERROR, "event_id": "su_1"}}), "su_1")

    def test_stamp_does_not_crash_on_a_non_string_event_id_dict(self):
        # #31 S2 (PR #49 review round 5): a browser-forged {"event_id": {"a": 1}} on
        # session.update used to reach guard.stamp() unchecked, where it's used as a
        # dict key ("self._sent[event_id] = ...") -- unhashable, so it killed the socket
        # with a raw TypeError instead of being dropped.
        guard = _SessionUpdateGuard()
        message = {"type": "session.update", "event_id": {"a": 1}, "session": {}}
        stamped = guard.stamp(message)
        self.assertIsInstance(stamped["event_id"], str)
        self.assertTrue(stamped["event_id"])

    def test_stamp_does_not_crash_on_a_non_string_event_id_list(self):
        guard = _SessionUpdateGuard()
        message = {"type": "session.update", "event_id": ["a"], "session": {}}
        stamped = guard.stamp(message)
        self.assertIsInstance(stamped["event_id"], str)
        self.assertTrue(stamped["event_id"])

    def test_stamp_ignores_an_empty_string_event_id_and_generates_its_own(self):
        guard = _SessionUpdateGuard()
        message = {"type": "session.update", "event_id": "", "session": {}}
        stamped = guard.stamp(message)
        self.assertIsInstance(stamped["event_id"], str)
        self.assertTrue(stamped["event_id"])


class RecoveryUnitTests(unittest.IsolatedAsyncioTestCase):

    async def test_a_retry_never_fires_while_a_response_is_running(self):
        sent, notices, sleep = [], [], GatedSleep()

        async def upstream(frame):
            sent.append(frame)

        async def client(payload):
            notices.append(payload)

        recovery = RateLimitRecovery(RateLimitSettings(), upstream, client, sleep=sleep)
        self.assertTrue(await recovery.on_error({"type": "error", "error": RATE_LIMIT_ERROR}))
        self.assertTrue(recovery.pending)
        # A response got going without its response.created reaching us yet.
        recovery.response_in_flight = True
        sleep.open()
        await asyncio.sleep(0.05)
        self.assertEqual(sent, [])
        self.assertFalse(recovery.busy)
        self.assertEqual(notices, [])


class GatedSleep:
    """Stands in for asyncio.sleep: records each delay, returns once opened."""

    def __init__(self):
        self.delays: list[float] = []
        self._gate = asyncio.Event()

    def open(self):
        self._gate.set()

    async def __call__(self, delay):
        self.delays.append(delay)
        await self._gate.wait()


class RateLimitedGA(FakeGAPerConnection):
    """Fails the next `fail_next` responses with rate_limit_exceeded."""

    def __init__(self):
        super().__init__()
        self.fail_next = 0
        self.fail_error = dict(RATE_LIMIT_ERROR)
        self.failures_sent = 0
        # Answer response.create with a function call to update_order.
        self.tool_call_next = False
        # Answer response.create with response.created only (a response in flight).
        self.hold_responses = False
        # Ignore response.create entirely (the service has not answered yet).
        self.silent = False
        self.rate_limit_session_updates = False

    async def _session_update(self, ws, session, event_id=None):
        if self.rate_limit_session_updates and event_id and not str(event_id).startswith("sonic_fallback"):
            await self._error(ws, "rate_limit_exceeded", None, event_id, "Please try again in 2s.")
            return
        await super()._session_update(ws, session, event_id)

    async def _respond(self, ws):
        if self.silent:
            return
        if self.hold_responses:
            await ws.send_json({"type": "response.created", "response": {"id": "resp_held"}})
            return
        if self.tool_call_next:
            self.tool_call_next = False
            item = {"type": "function_call", "call_id": "call_1", "name": "update_order",
                    "arguments": json.dumps({"action": "add", "item_name": "Tots", "size": "Large",
                                             "quantity": 1, "price": 2.99})}
            await ws.send_json({"type": "response.created", "response": {"id": "resp_tool"}})
            await ws.send_json({"type": "response.output_item.added", "item": item})
            await ws.send_json({"type": "conversation.item.created", "previous_item_id": "p", "item": item})
            await ws.send_json({"type": "response.output_item.done", "item": item})
            await ws.send_json({"type": "response.done", "response": {"id": "resp_tool", "status": "completed",
                                                                      "output": [item]}})
            return
        if self.fail_next > 0:
            self.fail_next -= 1
            self.failures_sent += 1
            rid = f"resp_rl_{self.failures_sent}"
            await ws.send_json({"type": "response.created", "response": {"id": rid, "status": "in_progress"}})
            await ws.send_json(failed_done(dict(self.fail_error), rid))
            return
        await super()._respond(ws)


class _RateLimitHarness(_ResumeHarness):

    fake_class = RateLimitedGA

    async def asyncSetUp(self):
        await super().asyncSetUp()
        self._idle = patch.object(session_manager_module, "_IDLE_TIMEOUT_SECONDS", 300)
        self._idle.start()
        self.sm.first_frame_timeout_seconds = 0.3
        self.sm.nudge_after_seconds = 0
        self.sleep = GatedSleep()
        self.rtmt._rate_limit_sleep = self.sleep

    async def asyncTearDown(self):
        self._idle.stop()
        await super().asyncTearDown()

    def upstream(self):
        return self.fake.connections[-1]

    def creates(self):
        return sum(e.get("type") == "response.create" for e in self.upstream())

    async def fresh(self):
        browser, meta, sid = await self._start_fresh()      # greeting: one response.create
        self.assertEqual(self.creates(), 1)
        return browser, meta, sid

    async def guest_turn(self, browser):
        # Echo suppression drops mic frames right after the greeting, so the
        # guest's turn is started the way the browser can: response.create.
        await browser.send_json({"type": "response.create"})
        await self._until(lambda: self.creates() >= 2)

    async def rate_limited_events(self, browser, duration=0.3):
        return [e for e in await self._browser_events(browser, duration) if e.get("type") == "extension.rate_limited"]


class RecoveryLadderTests(_RateLimitHarness):

    async def test_one_silent_retry_after_the_delay(self):
        browser, _, _ = await self.fresh()
        self.fake.fail_next = 1
        await self.guest_turn(browser)                       # the guest's turn is rate-limited
        await self._until(lambda: len(self.sleep.delays) == 1)
        await asyncio.sleep(0.1)
        self.assertEqual(self.sleep.delays, [1.5])
        self.assertEqual(self.creates(), 2, "retried before the delay elapsed")

        self.sleep.open()
        await self._until(lambda: self.creates() == 3)
        seen = []
        await self._until_event(browser, "response.done", seen=seen)
        await asyncio.sleep(0.2)
        seen += await self._browser_events(browser, 0.3)
        self.assertEqual(self.creates(), 3, "exactly one retry")
        self.assertEqual([e for e in seen if e["type"] == "extension.rate_limited"], [], "retry 1 must be silent")
        done = [e for e in seen if e["type"] == "response.done"]
        self.assertEqual(len(done), 1, "the failed response.done leaked to the browser")
        self.assertNotEqual(done[0]["response"].get("status"), "failed")
        await browser.close()

    async def test_second_failure_plays_apology_then_retries_after_4s(self):
        browser, _, _ = await self.fresh()
        self.fake.fail_next = 2
        self.sleep.open()
        await self.guest_turn(browser)
        seen = []
        notice = await self._until_event(browser, "extension.rate_limited", seen=seen)
        self.assertEqual(notice, {"type": "extension.rate_limited", "attempt": 1})
        await self._until_event(browser, "response.done", seen=seen)
        self.assertEqual(self.sleep.delays, [1.5, 4.0])
        self.assertEqual(self.creates(), 4)                  # greeting, guest turn, two retries
        self.assertEqual(await self.rate_limited_events(browser), [])
        await browser.close()

    async def test_third_failure_is_final_and_stops_then_the_next_turn_is_normal(self):
        browser, _, sid = await self.fresh()
        self.fake.fail_next = 3
        self.sleep.open()
        await self.guest_turn(browser)
        seen = []
        await self._until_event(browser, "extension.rate_limited", seen=seen)
        final = await self._until_event(browser, "extension.rate_limited", seen=seen)
        self.assertEqual(final, {"type": "extension.rate_limited", "attempt": 2, "final": True})
        await asyncio.sleep(0.3)
        self.assertEqual(self.creates(), 4, "no retry after the final failure")
        self.assertEqual(self.sleep.delays, [1.5, 4.0])
        self.assertFalse(browser.closed, "the session must stay alive")
        self.assertIn(sid, self.sm._last_activity)

        # The guest says it again: that turn gets its own, fresh ladder.
        await self.fake.upstreams[-1].send_json({"type": "input_audio_buffer.speech_started"})
        self.fake.fail_next = 1
        await browser.send_json({"type": "response.create"})
        await self._until_event(browser, "response.done")
        self.assertEqual(self.sleep.delays, [1.5, 4.0, 1.5], "the next turn must start with a silent retry")
        self.assertEqual(self.creates(), 6)
        await browser.close()

    async def test_service_hint_sets_the_delays_within_the_clamps(self):
        browser, _, _ = await self.fresh()
        self.fake.fail_next = 2
        self.fake.fail_error = {**RATE_LIMIT_ERROR, "message": "Rate limit reached. Please try again in 20s."}
        self.sleep.open()
        await self.guest_turn(browser)
        await self._until_event(browser, "response.done")
        self.assertEqual(self.sleep.delays, [5.0, 8.0])
        await browser.close()

    async def test_rate_limited_error_event_is_retried_too(self):
        browser, _, _ = await self.fresh()
        self.sleep.open()
        await self.fake.upstreams[-1].send_json({"type": "error", "error": {
            **RATE_LIMIT_ERROR, "message": "Please try again in 250ms."}})
        await self._until(lambda: self.creates() == 2)
        self.assertEqual(self.sleep.delays, [0.5])
        seen = await self._browser_events(browser, 0.3)
        self.assertNotIn("error", [e["type"] for e in seen], "a handled rate-limit error reached the browser")
        await browser.close()

    async def test_speech_started_cancels_a_pending_retry(self):
        browser, _, _ = await self.fresh()
        self.fake.fail_next = 1
        await self.guest_turn(browser)
        await self._until(lambda: len(self.sleep.delays) == 1)
        with self.assertLogs("sonic-drive-in", level="INFO") as logs:
            await self.fake.upstreams[-1].send_json({"type": "input_audio_buffer.speech_started"})
            await asyncio.sleep(0.2)
        self.assertTrue(any("retry cancelled: guest started speaking" in line for line in logs.output))
        self.sleep.open()
        await asyncio.sleep(0.3)
        self.assertEqual(self.creates(), 2, "a stale retry was stacked on the guest's new turn")
        await browser.close()

    async def test_a_new_response_cancels_a_pending_retry(self):
        browser, _, _ = await self.fresh()
        self.fake.fail_next = 1
        await self.guest_turn(browser)
        await self._until(lambda: len(self.sleep.delays) == 1)
        up = self.fake.upstreams[-1]
        await up.send_json({"type": "response.created", "response": {"id": "resp_vad"}})
        await up.send_json({"type": "response.done", "response": {"id": "resp_vad", "status": "completed", "output": []}})
        await asyncio.sleep(0.2)
        self.sleep.open()
        await asyncio.sleep(0.3)
        self.assertEqual(self.creates(), 2)
        await browser.close()

    async def test_a_browser_response_create_replaces_a_pending_retry(self):
        browser, _, _ = await self.fresh()
        self.fake.fail_next = 1
        await self.guest_turn(browser)
        await self._until(lambda: len(self.sleep.delays) == 1)
        self.fake.silent = True             # its response.created has not arrived yet...
        await browser.send_json({"type": "response.create"})
        await self._until(lambda: self.creates() == 3)
        await asyncio.sleep(0.1)
        self.sleep.open()                   # ...when the retry delay runs out
        await asyncio.sleep(0.3)
        self.assertEqual(self.creates(), 3, "retry stacked on top of the browser's own response.create")
        await browser.close()

    async def test_non_rate_limit_failure_is_untouched(self):
        browser, _, _ = await self.fresh()
        self.fake.fail_next = 1
        self.fake.fail_error = {"type": "server_error", "code": "server_error", "message": "try again in 1s"}
        self.sleep.open()
        await self.guest_turn(browser)
        seen = []
        done = await self._until_event(browser, "response.done", seen=seen)
        self.assertEqual(done["response"]["status"], "failed", "a non-rate-limit failure must reach the browser as before")
        seen += await self._browser_events(browser, 0.3)
        self.assertEqual(self.sleep.delays, [])
        self.assertEqual(self.creates(), 2)
        self.assertNotIn("extension.rate_limited", [e["type"] for e in seen])
        # ...and so does an unrelated error event.
        await self.fake.upstreams[-1].send_json({"type": "error", "error": {"type": "server_error", "code": "boom"}})
        await self._until_event(browser, "error")
        await browser.close()

    async def test_disabled_recovery_passes_the_failure_through(self):
        self.rtmt.rate_limit_settings = RateLimitSettings(enabled=False)
        browser, _, _ = await self.fresh()
        self.fake.fail_next = 1
        self.sleep.open()
        await self.guest_turn(browser)
        done = await self._until_event(browser, "response.done")
        self.assertEqual(done["response"]["status"], "failed")
        await asyncio.sleep(0.2)
        self.assertEqual((self.sleep.delays, self.creates()), ([], 2))
        await browser.close()

    async def test_session_update_correlated_rate_limit_error_goes_to_the_fallback(self):
        self.fake.rate_limit_session_updates = True
        self.sleep.open()
        browser = await self._connect()
        await self._until(lambda: len(self._fallbacks()) == 1)
        await asyncio.sleep(0.3)
        self.assertEqual(self.sleep.delays, [], "a session.update rejection was treated as a rate-limited response")
        self.assertEqual(self.creates(), 0)
        await browser.close()

    async def test_tool_follow_up_is_retried_without_re_running_the_tool(self):
        target = AsyncMock(return_value=ToolResult('{"items": []}', ToolResultDirection.TO_BOTH))
        self.rtmt.tools["update_order"].target = target
        browser, _, _ = await self.fresh()
        self.fake.tool_call_next = True     # the model calls update_order...
        self.fake.fail_next = 1             # ...and the follow-up response is rate-limited
        self.sleep.open()
        await browser.send_json({"type": "response.create"})
        await self._until(lambda: self.creates() == 4)      # greeting, guest turn, follow-up, retry
        await asyncio.sleep(0.4)
        self.assertEqual(self.sleep.delays, [1.5])
        self.assertEqual(target.await_count, 1, "the retry re-ran the tool")
        outputs = [e for e in self.upstream() if e.get("type") == "conversation.item.create"
                   and e["item"].get("type") == "function_call_output"]
        self.assertEqual(len(outputs), 1, "one function_call_output per tool call")
        self.assertEqual(self.creates(), 4)
        await browser.close()


class ResumeInteractionTests(_RateLimitHarness):
    """R1 composed with order resume: the retry is not guest activity, the resume
    nudge and a retry never stack two responses, and a detach drops the retry."""

    async def _converse_then_drop(self):
        browser, meta, sid = await self._start_fresh()
        await self.fake.upstreams[-1].send_json({
            "type": "conversation.item.input_audio_transcription.completed",
            "item_id": "i1", "transcript": "I'd like a cheeseburger"})
        await self._until(lambda: any(r == "guest" for r, _ in self.sm.recent_turns(sid)))
        await self._drop(browser, sid)
        return meta, sid

    async def _resume_ok(self, resume_id):
        browser = await self._resume(resume_id)
        await self._until_event(browser, "extension.session_resumed")
        return browser

    def nudges(self):
        return [e for e in self.upstream() if e.get("type") == "conversation.item.create" and NUDGE_MARKER in json.dumps(e)]

    async def _push_failure(self, rid="resp_unprompted"):
        up = self.fake.upstreams[-1]
        await up.send_json({"type": "response.created", "response": {"id": rid}})
        await up.send_json(failed_done(response_id=rid))

    async def test_a_retry_is_not_guest_activity(self):
        meta, sid = await self._converse_then_drop()
        browser = await self._resume_ok(meta["resumeId"])
        activity = self.sm._last_activity[sid]
        self.clock.advance(5)
        self.sleep.open()
        await self._push_failure()
        await self._until(lambda: self.creates() == 1)
        await self._until_event(browser, "response.done")
        self.assertEqual(self.sm._last_activity[sid], activity, "the rate-limit retry counted as guest activity")
        await browser.close()

    async def test_the_nudge_does_not_fire_on_top_of_a_pending_retry(self):
        meta, _ = await self._converse_then_drop()
        self.sm.nudge_after_seconds = 0.3
        browser = await self._resume_ok(meta["resumeId"])
        await self._push_failure()
        await self._until(lambda: len(self.sleep.delays) == 1)
        await asyncio.sleep(0.6)                            # the nudge timer runs out meanwhile
        self.assertEqual(self.nudges(), [], "nudged while a rate-limit retry was pending")
        self.assertEqual(self.creates(), 0)
        self.sleep.open()
        await self._until_event(browser, "response.done")
        await asyncio.sleep(0.3)
        self.assertEqual(self.creates(), 1, "exactly one response: the retry")
        self.assertEqual(self.nudges(), [])
        await browser.close()

    async def test_a_retry_does_not_fire_on_top_of_the_nudge_response(self):
        meta, _ = await self._converse_then_drop()
        self.sm.nudge_after_seconds = 0.2
        self.fake.hold_responses = True                     # the nudge's response stays in flight
        browser = await self._resume_ok(meta["resumeId"])
        await self._until(lambda: self.creates() == 1)
        await self._until(lambda: len(self.nudges()) == 1)
        await asyncio.sleep(0.1)
        self.sleep.open()
        await self.fake.upstreams[-1].send_json({"type": "error", "error": RATE_LIMIT_ERROR})
        await asyncio.sleep(0.4)
        self.assertEqual(self.sleep.delays, [], "a retry was scheduled on top of the running nudge response")
        self.assertEqual(self.creates(), 1)
        await browser.close()

    async def test_a_pending_retry_is_cancelled_when_the_socket_detaches(self):
        browser, _, sid = await self.fresh()
        self.fake.fail_next = 1
        await self.guest_turn(browser)
        await self._until(lambda: len(self.sleep.delays) == 1)
        upstream = self.upstream()
        with self.assertLogs("sonic-drive-in", level="INFO") as logs:
            await self._drop(browser, sid)
            await asyncio.sleep(0.1)
        self.assertTrue(any("retry cancelled: socket closed" in line for line in logs.output), logs.output)
        self.sleep.open()
        await asyncio.sleep(0.3)
        self.assertEqual(sum(e.get("type") == "response.create" for e in upstream), 2)


class ApologyClipTests(unittest.TestCase):
    """The pre-recorded clips the browser plays on extension.rate_limited attempt 1."""

    REPO = Path(__file__).resolve().parents[3]

    @classmethod
    def setUpClass(cls):
        sys.path.append(str(cls.REPO / "scripts"))
        import generate_apology_clips
        cls.gen = generate_apology_clips

    def test_one_clip_per_ui_language(self):
        locales = sorted(p.name for p in (self.REPO / "app" / "frontend" / "src" / "locales").iterdir()
                         if (p / "translation.json").is_file())
        apology_ts = (self.REPO / "app" / "frontend" / "src" / "lib" / "apology.ts").read_text(encoding="utf-8")
        frontend = re.search(r"APOLOGY_LANGUAGES = \[([^\]]*)\]", apology_ts).group(1)
        self.assertEqual(sorted(re.findall(r'"(\w+)"', frontend)), locales)
        self.assertEqual(sorted(self.gen.APOLOGY_PHRASES), locales)

    def test_clips_are_short_24khz_mono_pcm16_speech(self):
        for lang in self.gen.APOLOGY_PHRASES:
            with self.subTest(lang):
                path = self.gen.clip_path(lang)
                self.assertEqual(path.parent, self.REPO / "app" / "frontend" / "public" / "audio")
                with wave.open(str(path), "rb") as wav:
                    self.assertEqual((wav.getnchannels(), wav.getsampwidth(), wav.getframerate()), (1, 2, 24_000))
                    seconds = wav.getnframes() / wav.getframerate()
                    samples = array("h", wav.readframes(wav.getnframes()))
                self.assertGreater(seconds, 0.8)
                self.assertLess(seconds, 4.5)  # the browser gives up on the clip after 5 s
                self.assertGreater(max(abs(x) for x in samples), 2000)  # not silence

    def test_trim_silence_keeps_speech_and_a_little_padding(self):
        pad = 24_000 * 60 // 1000
        speech = array("h", [0] * 5000 + [4000, -4000] * 100 + [0] * 5000).tobytes()
        trimmed = array("h", self.gen.trim_silence(speech))
        self.assertEqual(len(trimmed), 200 + 2 * pad)
        self.assertEqual(trimmed[pad], 4000)
        self.assertEqual(self.gen.trim_silence(bytes(4000)), bytes(4000))

    def test_write_wav_round_trips(self):
        out = self.REPO / "app" / "backend" / "tests" / "_apology_tmp.wav"
        self.addCleanup(out.unlink, missing_ok=True)
        pcm = array("h", [1, -2, 3]).tobytes()
        self.gen.write_wav(out, pcm)
        with wave.open(str(out), "rb") as wav:
            self.assertEqual((wav.getnchannels(), wav.getsampwidth(), wav.getframerate()), (1, 2, 24_000))
            self.assertEqual(wav.readframes(3), pcm)


if __name__ == "__main__":
    logging.basicConfig(level=logging.INFO)
    unittest.main()
