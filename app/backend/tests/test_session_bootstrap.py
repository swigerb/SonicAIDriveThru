"""Regression tests for the $0.00 Carhop Ticket incident (2026-09-22).

Production session 26e3f21f: the browser's socket was auto-reconnected while
the mic was live. The fresh upstream realtime session ran on service defaults
(no tools, generic instructions, server VAD auto-responding) and the model spoke
before the browser's session.update arrived. Once assistant audio exists, GA
rejects any session.update whose voice differs from the current one with
`cannot_update_voice` -- and it rejects the whole event, so tools/tool_choice/
instructions were never registered and every turn completed with no tool calls.

These tests drive the real middle tier end to end against a fake GA realtime
server that enforces the two service behaviours involved (verified live against
gpt-realtime-1.5 on 2026-09-22):
  * server VAD auto-creates a response as soon as mic audio arrives;
  * a session.update carrying a *different* voice after assistant audio is
    rejected wholesale with `cannot_update_voice` (same voice / no voice is OK).
"""

import asyncio
import json
import logging
import os
import sys
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

sys.path.append(str(Path(__file__).resolve().parents[1]))

from aiohttp import WSMsgType, web
from aiohttp.test_utils import TestClient, TestServer
from azure.core.credentials import AzureKeyCredential

import rtmt as rtmt_module
from rtmt import RTMiddleTier, Tool

SYSTEM_PROMPT = "You are a Sonic Drive-In carhop."
TOOL_NAMES = ["search", "update_order", "get_order", "reset_order"]

# Exactly what app/frontend/src/hooks/useRealtime.tsx startSession() sends.
BROWSER_SESSION_UPDATE = {
    "type": "session.update",
    "session": {
        "turn_detection": {"type": "server_vad", "threshold": 0.7, "prefix_padding_ms": 300, "silence_duration_ms": 500},
        "input_audio_transcription": {"model": "whisper-1"},
    },
}
MIC_FRAME = {"type": "input_audio_buffer.append", "audio": "AAAA"}


class FakeGARealtime:
    """Minimal stand-in for /openai/v1/realtime with the GA rules that matter here."""

    def __init__(self):
        self.session = {"tools": [], "tool_choice": "auto", "instructions": "default assistant", "voice": "alloy"}
        self.assistant_audio = False
        self.vad_fired = False
        self.received: list[dict] = []
        self.errors: list[dict] = []
        self.response_sessions: list[dict] = []
        # GA rejects a session.update wholesale if ANY field is unsupported.
        # reject_keys: top-level GA session keys that get an update rejected.
        # echo_event_id=False mimics gpt-realtime-1.5 rejecting `reasoning`
        # (no error.event_id, no param).
        self.reject_keys: set[str] = set()
        self.reject_every_update = False
        self.echo_event_id = True

    def app(self) -> web.Application:
        app = web.Application()
        app.router.add_get("/openai/v1/realtime", self.handler)
        return app

    def _snapshot(self) -> dict:
        return {**self.session, "tools": [t.get("name") for t in self.session["tools"]]}

    async def handler(self, request: web.Request) -> web.WebSocketResponse:
        ws = web.WebSocketResponse()
        await ws.prepare(request)
        await ws.send_json({"type": "session.created", "session": {"type": "realtime", **self._snapshot()}})
        async for msg in ws:
            event = json.loads(msg.data)
            self.received.append(event)
            kind = event.get("type")
            if kind == "session.update":
                await self._session_update(ws, event["session"], event.get("event_id"))
            elif kind == "input_audio_buffer.append" and not self.vad_fired:
                self.vad_fired = True  # server VAD: speech detected -> auto response
                await self._respond(ws)
            elif kind == "response.create":
                await self._respond(ws)
            elif kind == "conversation.item.delete":
                # Unrelated client event rejected, with its event_id echoed.
                await self._error(ws, "item_not_found", "item_id", event.get("event_id"), "No such item.")
            elif kind == "response.cancel":
                # Unrelated rejection: no active response to cancel (see
                # tests/conformance/README.md's "response_cancel_not_active" contract).
                await self._error(ws, "response_cancel_not_active", "response_id", event.get("event_id"),
                                  "No active response to cancel.")
        return ws

    async def _error(self, ws, code, param, event_id, text) -> None:
        error = {"type": "error", "event_id": "event_srv", "error": {
            "type": "invalid_request_error", "code": code, "message": text, "param": param,
            "event_id": event_id}}
        self.errors.append(error)
        await ws.send_json(error)

    async def _session_update(self, ws, session: dict, event_id: str | None = None) -> None:
        rejected = sorted(k for k in session if k in self.reject_keys)
        if self.reject_every_update or rejected:
            key = rejected[0] if rejected else "instructions"
            await self._error(ws, "invalid_value",
                              f"session.{key}" if self.echo_event_id else None,
                              event_id if self.echo_event_id else None,
                              "Unsupported option for this model.")
            return
        voice = (session.get("audio") or {}).get("output", {}).get("voice")
        if voice is not None and self.assistant_audio and voice != self.session["voice"]:
            error = {"type": "error", "error": {
                "type": "invalid_request_error", "code": "cannot_update_voice",
                "message": "Cannot update a conversation's voice if assistant audio is present."}}
            self.errors.append(error)
            await ws.send_json(error)
            return
        for key in ("tools", "tool_choice", "instructions"):
            if key in session:
                self.session[key] = session[key]
        if voice is not None:
            self.session["voice"] = voice
        await ws.send_json({"type": "session.updated", "session": {"type": "realtime", **self._snapshot()}})

    async def _respond(self, ws) -> None:
        self.response_sessions.append(self._snapshot())
        await ws.send_json({"type": "response.created", "response": {"id": "resp"}})
        await ws.send_json({"type": "response.output_audio.delta", "delta": "AAAA"})
        self.assistant_audio = True
        await ws.send_json({"type": "response.output_audio.done"})
        await ws.send_json({"type": "response.done", "response": {"id": "resp", "output": [
            {"type": "message", "content": [{"type": "audio", "transcript": "hi"}]}]}})


class _RealtimeHarness(unittest.IsolatedAsyncioTestCase):
    """Real middle tier <-> FakeGARealtime, driven through a fake browser socket."""

    fake_class = FakeGARealtime

    async def asyncSetUp(self):
        self.fake = self.fake_class()
        self.fake_server = TestServer(self.fake.app())
        await self.fake_server.start_server()

        self.rtmt = RTMiddleTier(
            endpoint=str(self.fake_server.make_url("")),
            deployment="gpt-realtime-test",
            credentials=AzureKeyCredential("test-key"),
            voice_choice="shimmer",
        )
        self.rtmt.system_message = SYSTEM_PROMPT
        self.rtmt.max_tokens = 4096
        # Matches the shipped config.yaml default (never unset in a real
        # deployment) -- see input_audio_transcription server-ownership.
        self.rtmt.transcription_model = "whisper-1"
        for name in TOOL_NAMES:
            self.rtmt.tools[name] = Tool(target=MagicMock(), schema={"type": "function", "name": name})

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

    async def _response_done(self, browser, timeout=5.0):
        async def recv():
            while True:
                msg = await browser.receive()
                if json.loads(msg.data).get("type") == "response.done":
                    return
        await asyncio.wait_for(recv(), timeout)

    def _session_updates(self):
        return [e for e in self.fake.received if e["type"] == "session.update"]

    def _fallbacks(self):
        return [e for e in self._session_updates() if str(e.get("event_id", "")).startswith("sonic_fallback")]

    async def _browser_events(self, browser, duration=0.3):
        events = []
        try:
            while True:
                msg = await asyncio.wait_for(browser.receive(), duration)
                if msg.type != WSMsgType.TEXT:
                    return events
                events.append(json.loads(msg.data))
        except TimeoutError:
            return events

    def _only_session(self) -> str:
        """The session_id of the one connection currently attached. Only
        meaningful right after connecting a single guest, before any other
        guest has connected (see the voice-persistence tests, which capture
        this immediately after each guest connects)."""
        return next(iter(self.rtmt._sessions._session_map.values()))

class SessionBootstrapTests(_RealtimeHarness):

    async def test_reconnected_socket_with_live_mic_still_registers_tools(self):
        """The production failure: mic audio reaches the upstream before the
        browser's session.update. The model must still have our tools."""
        browser = await self.client.ws_connect("/realtime")
        await browser.send_json(MIC_FRAME)
        await self._response_done(browser)          # VAD auto-response
        await browser.send_json(BROWSER_SESSION_UPDATE)
        await self._response_done(browser)          # greeting

        first = self.fake.response_sessions[0]
        self.assertEqual(first["tools"], TOOL_NAMES,
                         "the model answered the guest with no tools registered")
        self.assertEqual(first["tool_choice"], "auto")
        self.assertEqual(first["instructions"], SYSTEM_PROMPT)
        self.assertEqual(self.fake.errors, [], "a session.update was rejected")
        self.assertEqual(self.fake.session["tools"], [{"type": "function", "name": n} for n in TOOL_NAMES])
        self.assertEqual(self.fake.session["tool_choice"], "auto")
        await browser.close()

    async def test_first_upstream_frame_is_the_server_session_config(self):
        browser = await self.client.ws_connect("/realtime")
        await browser.send_json(MIC_FRAME)
        await self._until(lambda: len(self.fake.received) >= 2)

        first = self.fake.received[0]
        self.assertEqual(first["type"], "session.update")
        session = first["session"]
        self.assertEqual([t["name"] for t in session["tools"]], TOOL_NAMES)
        self.assertEqual(session["tool_choice"], "auto")
        self.assertEqual(session["instructions"], SYSTEM_PROMPT)
        self.assertEqual(session["audio"]["output"]["voice"], "shimmer")
        self.assertEqual(session["audio"]["input"]["turn_detection"], BROWSER_SESSION_UPDATE["session"]["turn_detection"])
        self.assertEqual(session["audio"]["input"]["transcription"], {"model": "whisper-1"})
        self.assertEqual(session["type"], "realtime")
        await browser.close()

    async def test_session_update_after_assistant_audio_is_not_rejected_for_voice(self):
        """#43 fix (PR #49 review round 6, "S1"): the voice a guest picks is
        stored on their OWN session in self._sessions, never on RTMiddleTier
        -- so even if another tab/guest's pick lands in session storage under
        a DIFFERENT session_id mid-call, THIS connection's own local `voice`
        is unaffected, and a re-sent session.update (mic re-toggle) still
        must not carry a new voice."""
        browser = await self.client.ws_connect("/realtime")
        await browser.send_json(BROWSER_SESSION_UPDATE)
        await self._response_done(browser)          # greeting -> assistant audio present
        self.rtmt._sessions.set_voice("another-guests-session", "coral")   # simulates another tab/guest picking a voice
        await browser.send_json(BROWSER_SESSION_UPDATE)
        await self._until(lambda: len(self._session_updates()) >= 3)
        await asyncio.sleep(0.1)

        self.assertEqual(self.fake.errors, [])
        last = self._session_updates()[-1]["session"]
        self.assertNotIn("voice", (last.get("audio") or {}).get("output", {}))
        self.assertEqual([t["name"] for t in last["tools"]], TOOL_NAMES)
        await browser.close()

    async def test_voice_picker_after_assistant_audio_is_deferred(self):
        browser = await self.client.ws_connect("/realtime")
        await self._until(lambda: self.rtmt._sessions.active_session_count >= 1)
        sid = self._only_session()
        await browser.send_json(BROWSER_SESSION_UPDATE)
        await self._response_done(browser)
        before = len(self._session_updates())
        await browser.send_json({"type": "extension.set_voice", "voice": "coral"})
        await asyncio.sleep(0.2)

        self.assertEqual(len(self._session_updates()), before, "voice change was sent and would be rejected")
        self.assertEqual(self.fake.errors, [])
        # #43 fix (round 6): self.voice_choice (config default) is never
        # mutated; the pick lands in this session's own entry in
        # self._sessions, never on RTMiddleTier itself.
        self.assertEqual(self.rtmt._sessions.get_voice(sid), "coral")
        await browser.close()

    async def test_voice_picker_before_assistant_audio_is_applied(self):
        browser = await self.client.ws_connect("/realtime")
        await self._until(lambda: len(self._session_updates()) >= 1)
        await browser.send_json({"type": "extension.set_voice", "voice": "coral"})
        await self._until(lambda: len(self._session_updates()) >= 2)
        await asyncio.sleep(0.1)

        self.assertEqual(self.fake.session["voice"], "coral")
        self.assertEqual(self.fake.errors, [])
        await browser.close()

    async def test_unknown_voice_is_rejected_not_forwarded(self):
        """PR #49 review round 5, "M1": extension.set_voice trusted ANY
        non-empty string and forwarded it upstream immediately (unlocked
        path) -- a forged voice name reached the real GA endpoint, and (via
        the then process-wide self.voice_choice) every later guest's
        bootstrap too. Only a value from the server's own voice allow-list
        may ever be forwarded or adopted."""
        browser = await self.client.ws_connect("/realtime")
        await self._until(lambda: len(self._session_updates()) >= 1)
        before = len(self._session_updates())
        await browser.send_json({"type": "extension.set_voice", "voice": "rick_probe_voice"})
        await asyncio.sleep(0.2)

        self.assertEqual(len(self._session_updates()), before,
                          "an unknown voice must never be forwarded upstream")
        self.assertNotEqual(self.fake.session.get("voice"), "rick_probe_voice")
        await browser.close()

    async def test_two_concurrent_guests_voice_picks_do_not_leak_into_an_already_open_connection(self):
        """#43 fix (PR #49 review round 6, "S1"): the process-wide
        `self.voice_choice` mutation used to mean guest A picking a voice
        also changed guest B's in-flight conversation, even though B was
        already connected and B's socket has nothing to do with A's pick.
        Mirrors the C# conformance scenario of the same name."""
        guest_a = await self.client.ws_connect("/realtime")
        await self._until(lambda: self.rtmt._sessions.active_session_count >= 1)
        sid_a = self._only_session()
        await guest_a.send_json(BROWSER_SESSION_UPDATE)
        await self._response_done(guest_a)   # A's greeting -> A's voice is locked

        guest_b = await self.client.ws_connect("/realtime")
        await guest_b.send_json(BROWSER_SESSION_UPDATE)
        await self._response_done(guest_b)   # B's greeting -> B's voice is locked

        # Guest A picks a voice mid-conversation (locked, so it's deferred to
        # A's own next conversation) -- this must have NO effect on guest B,
        # who is still active right now.
        watermark = len(self._session_updates())
        await guest_a.send_json({"type": "extension.set_voice", "voice": "coral"})
        await asyncio.sleep(0.2)

        self.assertEqual(len(self._session_updates()), watermark,
                          "guest A's deferred voice pick must not produce any upstream session.update at all")
        self.assertEqual(self.rtmt._sessions.get_voice(sid_a), "coral")

        # Guest B's own connection is unaffected: a re-sent session.update
        # (mic re-toggle) on B must still omit voice (still locked on B's
        # ORIGINAL voice), never pick up A's pending pick.
        await guest_b.send_json(BROWSER_SESSION_UPDATE)
        await self._until(lambda: len(self._session_updates()) > watermark)
        latest = self._session_updates()[-1]["session"]
        self.assertNotIn("voice", (latest.get("audio") or {}).get("output", {}),
                          "guest B's re-sent session.update must not carry guest A's pending voice pick")

        await guest_a.close()
        await guest_b.close()

    async def test_voice_picked_after_lock_does_not_carry_to_a_brand_new_unrelated_connection(self):
        """#43 fix (PR #49 review round 6, "S1"): the round-5 fix left
        `self._voice_override` as a process-wide sticky default for every
        future NEW connection, so guest A's pick still became guest B's, C's,
        ... default -- exactly Rick's S1 finding ("Guest A can still change
        every guest's voice"). A brand-new, UNRELATED connection (no
        resume_id presented, so it has nothing to do with the first guest's
        session) must always bootstrap with the server's config default,
        never another guest's pick."""
        first = await self.client.ws_connect("/realtime")
        await first.send_json(BROWSER_SESSION_UPDATE)
        await self._response_done(first)   # greeting -> voice locked
        await first.send_json({"type": "extension.set_voice", "voice": "verse"})
        await asyncio.sleep(0.2)
        await first.close()

        watermark = len(self._session_updates())
        second = await self.client.ws_connect("/realtime")
        await self._until(lambda: len(self._session_updates()) > watermark)
        bootstrap = [e for e in self._session_updates() if str(e.get("event_id", "")).startswith("sonic_bootstrap")][-1]
        self.assertEqual(bootstrap["session"]["audio"]["output"]["voice"], "shimmer",
                          "a brand-new, unrelated connection must get the server default, never another guest's pick")
        await second.close()

    async def test_bootstrap_does_not_trigger_an_unprompted_greeting(self):
        """Greeting belongs to the browser's session.update (mic pressed), not to
        the bootstrap session.updated that arrives as soon as the page connects."""
        browser = await self.client.ws_connect("/realtime")
        await self._until(lambda: len(self._session_updates()) >= 1)
        await asyncio.sleep(0.3)
        self.assertFalse(any(e["type"] == "conversation.item.create" for e in self.fake.received))
        self.assertEqual(self.fake.response_sessions, [])

        await browser.send_json(BROWSER_SESSION_UPDATE)
        await self._response_done(browser)
        self.assertEqual(sum(e["type"] == "conversation.item.create" for e in self.fake.received), 1)
        self.assertEqual(self.fake.response_sessions[0]["tools"], TOOL_NAMES)
        await browser.close()


class ClientLogControlGateTests(_RealtimeHarness):
    """swigerb/SonicAIDriveThru#53: a browser must never be able to flip
    process-wide log verbosity (`extension.set_verbose_logging`) or open a
    process-wide log file (`extension.set_log_to_file`) in production --
    both mutate the shared `sonic-verbose` logger, so one guest's request
    would silently change every OTHER connection's logging on the same
    worker process (and file logging additionally risks writing guest audio
    transcripts to disk and filling it).

    Gated the same way #31's G1 `response.create` gate is (see
    `test_rtmt.ResponseCreateGateTests` /
    `rtmt._client_log_control_allowed`): `conformance_hooks.hooks_enabled_now()`
    (live-rechecked every call, never the frozen import-time constant -- see
    that function's own docstring for why) OR an explicit
    `security.allow_client_log_control` config/env opt-in.

    `app/backend/tests/conftest.py` sets `CONFORMANCE_TEST_HOOKS=1` for the
    whole pytest process, so the "dropped" test below forces hooks off LIVE
    for its own duration to reproduce production's actual shape (mirroring
    the G1 test's identical `patch.dict(os.environ, ...)` technique)."""

    async def asyncSetUp(self):
        await super().asyncSetUp()
        # Snapshot the shared vlogger's state so a test that legitimately
        # enables it (hooks on / config flag on) can't leak into any other
        # test in the process.
        self._vlogger_level = rtmt_module.vlogger.level
        self._vlogger_handlers = list(rtmt_module.vlogger.handlers)

    async def asyncTearDown(self):
        for h in list(rtmt_module.vlogger.handlers):
            if h not in self._vlogger_handlers:
                rtmt_module.vlogger.removeHandler(h)
                try:
                    h.close()
                except Exception:
                    pass
        rtmt_module.vlogger.setLevel(self._vlogger_level)
        await super().asyncTearDown()

    async def test_both_extensions_dropped_with_warning_when_hooks_and_flag_are_off(self):
        with patch.dict(os.environ, {"CONFORMANCE_TEST_HOOKS": ""}), \
                patch.object(rtmt_module, "_security_cfg", {}):
            browser = await self.client.ws_connect("/realtime")
            await self._until(lambda: self.rtmt._sessions.active_session_count >= 1)

            with self.assertLogs("sonic-drive-in", level="WARNING") as logs:
                await browser.send_json({"type": "extension.set_verbose_logging", "enabled": True})
                await asyncio.sleep(0.1)
            self.assertTrue(any("set_verbose_logging" in m for m in logs.output))
            self.assertEqual(rtmt_module.vlogger.level, self._vlogger_level,
                              "verbose logging must not be enabled while gated off")
            self.assertEqual(list(rtmt_module.vlogger.handlers), self._vlogger_handlers)

            with self.assertLogs("sonic-drive-in", level="WARNING") as logs2:
                await browser.send_json({"type": "extension.set_log_to_file", "enabled": True})
                await asyncio.sleep(0.1)
            self.assertTrue(any("set_log_to_file" in m for m in logs2.output))
            self.assertEqual(list(rtmt_module.vlogger.handlers), self._vlogger_handlers,
                              "no file handler must be attached while gated off")
            # Liveness: the socket must still be open and processing frames
            # afterwards -- a dropped extension frame must never close it.
            await browser.send_json({"type": "input_audio_buffer.clear"})
            await asyncio.sleep(0.05)
            self.assertFalse(browser.closed)
            await browser.close()

    async def test_both_extensions_still_work_when_conformance_hooks_are_enabled(self):
        # conftest.py already sets CONFORMANCE_TEST_HOOKS=1 for this whole
        # process -- no patch needed, this is the existing/legacy behaviour.
        with patch.object(rtmt_module, "_create_verbose_file_handler", return_value=MagicMock()):
            browser = await self.client.ws_connect("/realtime")
            await self._until(lambda: self.rtmt._sessions.active_session_count >= 1)

            await browser.send_json({"type": "extension.set_verbose_logging", "enabled": True})
            await self._until(lambda: rtmt_module.vlogger.level == logging.DEBUG)

            await browser.send_json({"type": "extension.set_log_to_file", "enabled": True})
            await self._until(lambda: len(rtmt_module.vlogger.handlers) > len(self._vlogger_handlers))

            await browser.send_json({"type": "extension.set_log_to_file", "enabled": False})
            await browser.send_json({"type": "extension.set_verbose_logging", "enabled": False})
            await asyncio.sleep(0.05)
            await browser.close()

    async def test_verbose_logging_still_works_via_explicit_config_flag_with_hooks_off(self):
        with patch.dict(os.environ, {"CONFORMANCE_TEST_HOOKS": ""}), \
                patch.object(rtmt_module, "_security_cfg", {"allow_client_log_control": True}):
            browser = await self.client.ws_connect("/realtime")
            await self._until(lambda: self.rtmt._sessions.active_session_count >= 1)

            await browser.send_json({"type": "extension.set_verbose_logging", "enabled": True})
            await self._until(lambda: rtmt_module.vlogger.level == logging.DEBUG)

            await browser.send_json({"type": "extension.set_verbose_logging", "enabled": False})
            await asyncio.sleep(0.05)
            await browser.close()


class SessionUpdatedScrubTests(_RealtimeHarness):
    """swigerb/SonicAIDriveThru#27: the middle tier scrubbed `instructions` and
    `tools` from `session.created` before relaying it to the browser, but had
    no case for `session.updated` in `_process_message_to_client`'s match
    statement -- and `session.updated` is also not in
    `_PASSTHROUGH_SERVER_TYPES`, so it fell through unmodified. GA fires
    `session.updated` after every accepted session.update (starting with our
    own bootstrap one), echoing the full session object back, so every
    browser connection received the real system prompt and tool schemas.

    swigerb/SonicAIDriveThru#45 replaced the original deny-list scrub with a
    minimal allow-listed copy (`RTMiddleTier._client_session_echo`): the
    browser-bound `session` object now contains only `id`, `object`, and
    `audio.output.voice` -- `instructions`/`tools` (and everything else) are
    absent entirely rather than nulled out, so these tests assert absence,
    not emptiness.
    """

    async def test_bootstrap_session_updated_reaching_the_browser_has_no_instructions_or_tools(self):
        browser = await self.client.ws_connect("/realtime")
        # The bootstrap session.update (sent before any browser frame) is
        # acknowledged by the fake with a session.updated that carries the
        # real instructions/tools -- exactly what the real GA service does.
        events = await self._browser_events(browser, duration=1.0)
        updates = [e for e in events if e["type"] == "session.updated"]
        self.assertTrue(updates, "no session.updated reached the browser")

        # Sanity check: the *upstream* fake really did receive our real
        # prompt and tool schemas, so this test would fail for the right
        # reason if the scrub were ever removed.
        self.assertEqual(self.fake.session["instructions"], SYSTEM_PROMPT)
        self.assertEqual([t.get("name") for t in self.fake.session["tools"]], TOOL_NAMES)

        for event in updates:
            session = event["session"]
            self.assertEqual(set(session), {"id", "object", "audio"},
                              "session.updated must relay only the allow-listed session keys")
            self.assertNotIn("instructions", session,
                              "session.updated leaked the system prompt to the browser")
            self.assertNotIn("tools", session,
                              "session.updated leaked tool schemas to the browser")
        await browser.close()

    async def test_session_updated_for_the_browsers_own_update_is_also_scrubbed(self):
        browser = await self.client.ws_connect("/realtime")
        await self._browser_events(browser, duration=0.3)     # drain the bootstrap ack

        await browser.send_json(BROWSER_SESSION_UPDATE)
        events = await self._browser_events(browser, duration=1.0)
        updates = [e for e in events if e["type"] == "session.updated"]
        self.assertTrue(updates, "expected a session.updated for the browser's own session.update too")

        for event in updates:
            session = event["session"]
            self.assertEqual(set(session), {"id", "object", "audio"},
                              "session.updated must relay only the allow-listed session keys")
            self.assertNotIn("instructions", session,
                              "session.updated leaked the system prompt to the browser")
            self.assertNotIn("tools", session,
                              "session.updated leaked tool schemas to the browser")
        await browser.close()


class SessionUpdateFallbackTests(_RealtimeHarness):
    """A rejected session.update must never silently cost us the tools.

    GA drops the WHOLE session.update when any one field is unsupported (a
    locked voice, an undeployed transcription model, `reasoning` on 1.5...).
    The middle tier correlates the `error` back to its update and immediately
    resends a minimal one -- instructions + tools only -- exactly once.
    """

    async def _until_browser(self, browser, event_type, timeout=5.0):
        async def recv():
            while True:
                msg = await browser.receive()
                if json.loads(msg.data).get("type") == event_type:
                    return
        await asyncio.wait_for(recv(), timeout)

    async def test_every_session_update_carries_an_event_id(self):
        browser = await self.client.ws_connect("/realtime")
        await self._until_browser(browser, "session.updated")
        await browser.send_json({"type": "extension.set_voice", "voice": "coral"})
        await browser.send_json(BROWSER_SESSION_UPDATE)
        await self._until(lambda: len(self._session_updates()) >= 3)

        ids = [e.get("event_id") for e in self._session_updates()]
        self.assertTrue(all(ids), ids)
        self.assertEqual(len(set(ids)), len(ids))
        await browser.close()

    async def test_rejected_bootstrap_triggers_exactly_one_minimal_fallback_that_registers_tools(self):
        self.fake.reject_keys = {"audio"}      # e.g. an undeployable transcription/voice setting
        browser = await self.client.ws_connect("/realtime")
        await self._until(lambda: len(self._fallbacks()) >= 1)
        events = await self._browser_events(browser)

        bootstrap = self._session_updates()[0]
        self.assertEqual([e["error"]["event_id"] for e in self.fake.errors], [bootstrap["event_id"]])
        fallbacks = self._fallbacks()
        self.assertEqual(len(fallbacks), 1)
        self.assertEqual(set(fallbacks[0]["session"]), {"type", "instructions", "tools", "tool_choice"})
        self.assertEqual(self.fake.session["tools"], [{"type": "function", "name": n} for n in TOOL_NAMES])
        self.assertEqual(self.fake.session["tool_choice"], "auto")
        self.assertEqual(self.fake.session["instructions"], SYSTEM_PROMPT)
        self.assertNotIn("error", [e["type"] for e in events], "a recovered rejection reached the browser")

        await browser.send_json(MIC_FRAME)            # the guest speaks: the model must have its tools
        await self._response_done(browser)
        self.assertEqual(self.fake.response_sessions[0]["tools"], TOOL_NAMES)
        await browser.close()

    async def test_rejection_without_event_id_is_recovered_and_reasoning_turned_off(self):
        """gpt-realtime-1.5 rejects `reasoning` with no error.event_id and no param."""
        self.rtmt.reasoning_effort = "low"
        self.fake.reject_keys = {"reasoning"}
        self.fake.echo_event_id = False
        browser = await self.client.ws_connect("/realtime")
        await self._until(lambda: len(self._fallbacks()) >= 1)
        events = await self._browser_events(browser)

        self.assertIn("reasoning", self._session_updates()[0]["session"])
        self.assertEqual(len(self._fallbacks()), 1)
        self.assertNotIn("reasoning", self._fallbacks()[0]["session"])
        self.assertEqual(self.fake.session["tools"], [{"type": "function", "name": n} for n in TOOL_NAMES])
        self.assertNotIn("error", [e["type"] for e in events])
        self.assertTrue(self.rtmt._reasoning_rejected)
        self.assertFalse(self.rtmt.reasoning_enabled())

        await browser.send_json(BROWSER_SESSION_UPDATE)   # later updates no longer carry `reasoning`
        await self._response_done(browser)
        self.assertNotIn("reasoning", self._session_updates()[-1]["session"])
        self.assertEqual(len(self.fake.errors), 1)
        await browser.close()

    async def test_forced_reasoning_on_a_non_reasoning_deployment_still_registers_tools(self):
        """Operator sets reasoning_model=true on a 1.5 deployment: 1.5 rejects it (no event_id,
        no param), the fallback still registers the tools, and later updates drop `reasoning`."""
        self.rtmt.deployment = "gpt-realtime-1.5"
        self.rtmt.reasoning_model = True
        self.rtmt.reasoning_effort = "minimal"
        self.fake.reject_keys = {"reasoning"}
        self.fake.echo_event_id = False
        browser = await self.client.ws_connect("/realtime")
        await self._until(lambda: len(self._fallbacks()) >= 1)
        events = await self._browser_events(browser)

        self.assertEqual(self._session_updates()[0]["session"]["reasoning"], {"effort": "minimal"})
        self.assertEqual(len(self._fallbacks()), 1)
        self.assertEqual(self.fake.session["tools"], [{"type": "function", "name": n} for n in TOOL_NAMES])
        self.assertNotIn("error", [e["type"] for e in events])
        await browser.send_json(BROWSER_SESSION_UPDATE)
        await self._response_done(browser)
        self.assertNotIn("reasoning", self._session_updates()[-1]["session"])
        self.assertEqual(self.fake.response_sessions[0]["tools"], TOOL_NAMES)
        await browser.close()

    async def _assert_rejected_fallback_does_not_loop(self):
        self.fake.reject_every_update = True
        browser = await self.client.ws_connect("/realtime")
        await self._until(lambda: len(self._fallbacks()) >= 1)
        events = await self._browser_events(browser, duration=0.5)

        self.assertEqual(len(self._session_updates()), 2, "bootstrap + exactly one fallback")
        self.assertEqual(len(self._fallbacks()), 1)
        errors = [e for e in events if e["type"] == "error"]
        self.assertEqual(len(errors), 1, "the fallback's own rejection must reach the browser, once")
        if self.fake.echo_event_id:
            self.assertEqual(errors[0]["error"]["event_id"], self._fallbacks()[0]["event_id"])

        # A new original still gets its own (single) fallback -- and no more.
        await browser.send_json(BROWSER_SESSION_UPDATE)
        await self._until(lambda: len(self._session_updates()) >= 4)
        events = await self._browser_events(browser, duration=0.5)
        self.assertEqual(len(self._session_updates()), 4)
        self.assertEqual(len(self._fallbacks()), 2)
        self.assertEqual(sum(e["type"] == "error" for e in events), 1)
        await browser.close()

    async def test_rejected_fallback_does_not_loop(self):
        await self._assert_rejected_fallback_does_not_loop()

    async def test_rejected_fallback_without_event_id_does_not_loop(self):
        self.fake.echo_event_id = False
        await self._assert_rejected_fallback_does_not_loop()

    async def test_unrelated_errors_do_not_trigger_fallback(self):
        browser = await self.client.ws_connect("/realtime")
        await self._until_browser(browser, "session.updated")     # nothing of ours in flight now
        # swigerb/SonicAIDriveThru#31: conversation.item.delete is no longer
        # forwarded upstream at all -- it isn't in the browser->upstream
        # allow-list (the real frontend never sends it), so it can no longer
        # serve as an "unrelated error" vehicle here. PR #49 review round 2,
        # "S1" also removed input_audio_buffer.commit from the allow-list (the
        # real frontend never sends it either), so two response.cancel frames
        # (still allow-listed) stand in instead, each rejected by the fake
        # with "no active response to cancel" since nothing is streaming.
        await browser.send_json({"type": "response.cancel"})
        await browser.send_json({"type": "response.cancel"})
        await self._until(lambda: len(self.fake.errors) >= 2)
        events = await self._browser_events(browser)

        self.assertEqual(self._fallbacks(), [])
        self.assertEqual(len(self._session_updates()), 1)
        self.assertEqual(sorted(e["error"]["code"] for e in events if e["type"] == "error"),
                         ["response_cancel_not_active", "response_cancel_not_active"])
        await browser.close()


class SessionUpdateGuardTests(unittest.TestCase):

    def _error(self, event_id=None, param=None, type_="invalid_request_error"):
        return {"type": "error", "error": {"type": type_, "code": "invalid_value", "param": param,
                                           "event_id": event_id}}

    def test_track_adds_event_id_and_keeps_an_existing_one(self):
        from rtmt import _SessionUpdateGuard
        guard = _SessionUpdateGuard()
        added = json.loads(guard.track(json.dumps({"type": "session.update", "session": {}})))
        self.assertTrue(added["event_id"].startswith("sonic_su_"))
        kept = json.loads(guard.track(json.dumps({"type": "session.update", "event_id": "mine", "session": {}})))
        self.assertEqual(kept["event_id"], "mine")

    def test_correlation_rules(self):
        from rtmt import _SessionUpdateGuard
        guard = _SessionUpdateGuard()
        self.assertIsNone(guard.correlate(self._error()), "nothing in flight")
        guard.stamp({"type": "session.update", "event_id": "su1", "session": {}})
        self.assertIsNone(guard.correlate(self._error(event_id="client_evt")), "explicitly someone else's")
        self.assertIsNone(guard.correlate(self._error(param="item_id")), "not a session field")
        self.assertIsNone(guard.correlate(self._error(type_="server_error")), "not a validation error")
        self.assertEqual(guard.correlate(self._error(param="session.reasoning")), "su1")

        guard.stamp({"type": "session.update", "event_id": "su2", "session": {}})
        guard.on_session_updated()
        self.assertIsNone(guard.correlate(self._error()), "su2 was acknowledged")
        self.assertEqual(guard.correlate(self._error(event_id="su2")), "su2", "echoed id always correlates")

    def test_one_fallback_per_original(self):
        from rtmt import _SessionUpdateGuard
        guard = _SessionUpdateGuard()
        self.assertTrue(guard.claim_fallback("su1"))
        self.assertFalse(guard.claim_fallback("su1"))
        self.assertTrue(guard.claim_fallback("su2"))


class ReasoningAndTranscriptionConfigTests(unittest.TestCase):

    def _rtmt(self, deployment="gpt-realtime-2.1", **attrs):
        rtmt = RTMiddleTier("https://fake.openai.azure.com", deployment, AzureKeyCredential("k"), voice_choice="marin")
        rtmt.system_message = SYSTEM_PROMPT
        rtmt.tools["update_order"] = Tool(target=MagicMock(), schema={"type": "function", "name": "update_order"})
        for key, value in attrs.items():
            setattr(rtmt, key, value)
        return rtmt

    def _bootstrap(self, rtmt):
        return json.loads(rtmt.build_bootstrap_session_update())["session"]

    def test_reasoning_not_sent_when_unconfigured(self):
        session = self._bootstrap(self._rtmt())
        self.assertNotIn("reasoning", session)
        self.assertNotIn("parallel_tool_calls", session)

    def test_reasoning_sent_on_a_reasoning_deployment(self):
        session = self._bootstrap(self._rtmt(reasoning_effort="low", parallel_tool_calls=False))
        self.assertEqual(session["reasoning"], {"effort": "low"})
        self.assertIs(session["parallel_tool_calls"], False)
        self.assertEqual(self._bootstrap(self._rtmt(reasoning_effort="none"))["reasoning"], {"effort": "none"})

    def test_rollback_to_1_5_never_sends_reasoning(self):
        """1.5 rejects `reasoning` (any effort) and parallel_tool_calls=true -- with the tools."""
        for deployment in ("gpt-realtime-1.5", "gpt-realtime", "gpt-realtime-2025-08-28", "gpt-realtime-mini",
                           "gpt-4o-realtime-preview"):
            with self.subTest(deployment=deployment):
                rtmt = self._rtmt(deployment, reasoning_effort="high", parallel_tool_calls=True)
                session = self._bootstrap(rtmt)
                self.assertNotIn("reasoning", session)
                self.assertNotIn("parallel_tool_calls", session)
                self.assertEqual(session["tools"][0]["name"], "update_order")
                self.assertFalse(rtmt.reasoning_enabled())

    def test_deployment_name_check(self):
        from rtmt import deployment_supports_reasoning
        for name in ("gpt-realtime-2", "gpt-realtime-2.1", "GPT-Realtime-2.1", "my-custom-carhop", "", None):
            self.assertTrue(deployment_supports_reasoning(name), name)
        for name in ("gpt-realtime-1.5", "gpt-realtime-1", "gpt-realtime", "gpt-realtime-2025-08-28",
                     "gpt-realtime-mini-2025-10-06", "gpt-4o-realtime-preview"):
            self.assertFalse(deployment_supports_reasoning(name), name)

    def test_data_zone_deployment_name_is_a_reasoning_deployment(self):
        """The DataZoneStandard deployment (`gpt-realtime-2.1-dz`, selected with
        `azd env set AZURE_OPENAI_REALTIME_DEPLOYMENT gpt-realtime-2.1-dz`) is the
        same 2.1 model: `auto` must still send `reasoning`, while a data-zone 1.5
        rollback must not."""
        from rtmt import configure_realtime_model, deployment_supports_reasoning
        for name in ("gpt-realtime-2.1-dz", "GPT-Realtime-2.1-DZ", "gpt-realtime-2-dz"):
            self.assertTrue(deployment_supports_reasoning(name), name)
        for name in ("gpt-realtime-1.5-dz", "gpt-realtime-mini-dz"):
            self.assertFalse(deployment_supports_reasoning(name), name)
        rtmt = configure_realtime_model(
            self._rtmt("gpt-realtime-2.1-dz"),
            {"reasoning_effort": "low", "parallel_tool_calls": False, "reasoning_model": "auto"}, environ={})
        session = self._bootstrap(rtmt)
        self.assertTrue(rtmt.reasoning_enabled())
        self.assertEqual(session["reasoning"], {"effort": "low"})
        self.assertIs(session["parallel_tool_calls"], False)
        self.assertEqual(session["tools"][0]["name"], "update_order")

    def test_client_cannot_inject_reasoning(self):
        rtmt = self._rtmt("gpt-realtime-1.5")
        session = rtmt._build_session({"reasoning": {"effort": "high"}, "parallel_tool_calls": True})
        self.assertNotIn("reasoning", session)
        self.assertNotIn("parallel_tool_calls", session)

    def test_runtime_rejection_stops_reasoning(self):
        rtmt = self._rtmt(reasoning_effort="low")
        rtmt._reasoning_rejected = True
        self.assertNotIn("reasoning", self._bootstrap(rtmt))
        rtmt.reasoning_model = True                     # a live rejection beats the explicit switch
        self.assertNotIn("reasoning", self._bootstrap(rtmt))

    def test_explicit_reasoning_model_switch_beats_the_name_check(self):
        from rtmt import configure_realtime_model
        cases = [
            # (deployment, config reasoning_model, env switch, reasoning sent?)
            ("gpt-realtime-2.1", "auto", None, True),
            ("gpt-realtime-1.5", "auto", None, False),
            ("gpt-realtime-2.1", False, None, False),          # YAML `false`
            ("gpt-realtime-2.1", "auto", "false", False),
            ("carhop-prod", "auto", None, True),               # unknown name: assumed reasoning (fallback guards it)
            ("carhop-prod", "auto", "false", False),
            ("gpt-4o-carhop", "auto", None, False),
            ("gpt-4o-carhop", "false", "true", True),         # env wins over config
            ("gpt-realtime-1.5", True, "", True),              # empty env = use config
            ("gpt-realtime-1.5", "bogus", None, False),        # unknown value = auto
        ]
        for deployment, cfg_switch, env_switch, sent in cases:
            with self.subTest(deployment=deployment, cfg=cfg_switch, env=env_switch):
                env = {} if env_switch is None else {"AZURE_OPENAI_REALTIME_REASONING_MODEL": env_switch}
                rtmt = configure_realtime_model(
                    self._rtmt(deployment), {"reasoning_effort": "low", "parallel_tool_calls": False,
                                             "reasoning_model": cfg_switch}, environ=env)
                session = self._bootstrap(rtmt)
                self.assertEqual("reasoning" in session, sent)
                self.assertEqual("parallel_tool_calls" in session, sent)
                self.assertEqual(rtmt.reasoning_enabled(), sent)
                self.assertEqual(session["tools"][0]["name"], "update_order")

    def test_effort_off_or_empty_never_sends_reasoning_even_when_forced(self):
        from rtmt import configure_realtime_model
        for effort_cfg, effort_env in (("", None), ("off", None), ("low", "off"), (None, None)):
            with self.subTest(cfg=effort_cfg, env=effort_env):
                env = {"AZURE_OPENAI_REALTIME_REASONING_MODEL": "true"}
                if effort_env is not None:
                    env["AZURE_OPENAI_REALTIME_REASONING_EFFORT"] = effort_env
                rtmt = configure_realtime_model(self._rtmt(), {"reasoning_effort": effort_cfg}, environ=env)
                self.assertNotIn("reasoning", self._bootstrap(rtmt))
                self.assertFalse(rtmt.reasoning_enabled())

    def test_fallback_is_minimal(self):
        rtmt = self._rtmt(reasoning_effort="low", parallel_tool_calls=True, transcription_model="whisper-1")
        payload = json.loads(rtmt.build_fallback_session_update())
        self.assertTrue(payload["event_id"].startswith("sonic_fallback_"))
        self.assertEqual(set(payload["session"]), {"type", "instructions", "tools", "tool_choice"})
        self.assertEqual(payload["session"]["tool_choice"], "auto")

    def test_configure_realtime_model(self):
        from rtmt import configure_realtime_model
        cases = [
            # (config, env, expected effort, expected transcription model)
            ({}, {}, None, "whisper-1"),
            ({"reasoning_effort": "low", "transcription_model": "gpt-4o-transcribe"}, {}, "low", "gpt-4o-transcribe"),
            ({"reasoning_effort": "low"}, {"AZURE_OPENAI_REALTIME_REASONING_EFFORT": "medium"}, "medium", "whisper-1"),
            ({"reasoning_effort": "low"}, {"AZURE_OPENAI_REALTIME_REASONING_EFFORT": "off"}, None, "whisper-1"),
            ({"reasoning_effort": "low"}, {"AZURE_OPENAI_REALTIME_REASONING_EFFORT": ""}, "low", "whisper-1"),
            ({"reasoning_effort": ""}, {}, None, "whisper-1"),
            ({"reasoning_effort": False}, {}, None, "whisper-1"),      # YAML `off` parses to False
            ({"reasoning_effort": "turbo"}, {}, None, "whisper-1"),
            ({"transcription_model": "whisper-1"},
             {"AZURE_OPENAI_REALTIME_TRANSCRIPTION_MODEL": "my-transcribe-deployment"}, None, "my-transcribe-deployment"),
        ]
        for cfg, env, effort, transcription in cases:
            with self.subTest(cfg=cfg, env=env):
                rtmt = configure_realtime_model(self._rtmt(), cfg, environ=env)
                self.assertEqual(rtmt.reasoning_effort, effort)
                self.assertEqual(rtmt.transcription_model, transcription)
                session = self._bootstrap(rtmt)
                self.assertEqual(session["audio"]["input"]["transcription"], {"model": transcription})
                self.assertEqual(session.get("reasoning"), None if effort is None else {"effort": effort})

    def test_shipped_config_is_rollback_safe_and_uses_whisper(self):
        import yaml

        from rtmt import configure_realtime_model
        cfg = yaml.safe_load((Path(__file__).resolve().parents[1] / "config.yaml").read_text(encoding="utf-8"))
        model_cfg = cfg["model"]
        self.assertEqual(model_cfg["default_voice"], "marin")
        self.assertEqual(model_cfg["transcription_model"], "whisper-1")
        self.assertEqual(model_cfg["reasoning_model"], "auto")
        for deployment in ("gpt-realtime-2.1", "gpt-realtime-1.5"):
            rtmt = configure_realtime_model(self._rtmt(deployment), model_cfg, environ={})
            session = self._bootstrap(rtmt)
            if deployment == "gpt-realtime-1.5":
                self.assertNotIn("reasoning", session)
                self.assertNotIn("parallel_tool_calls", session)


class VoiceConfigValidationTests(unittest.TestCase):
    """swigerb/SonicAIDriveThru#57 FU2: `configure_realtime_model` must fail
    LOUDLY at startup on a misconfigured voice allow-list, rather than
    silently shipping a config that rejects every guest's voice pick."""

    def _rtmt(self, voice_choice="marin"):
        rtmt = RTMiddleTier("https://fake.openai.azure.com", "gpt-realtime-2.1", AzureKeyCredential("k"),
                             voice_choice=voice_choice)
        rtmt.system_message = SYSTEM_PROMPT
        return rtmt

    def test_a_bare_string_allowed_voices_is_rejected(self):
        """A bare string is iterable character-by-character in Python --
        `frozenset(str(v) for v in "marin")` would silently become
        {"m", "a", "r", "i", "n"} instead of the single voice "marin". Pick a
        default voice that IS one of those characters ("m") so a naive
        char-set fallback would pass the (separate) membership check too --
        only the isinstance guard itself can catch this."""
        from rtmt import configure_realtime_model
        with self.assertRaises(ValueError):
            configure_realtime_model(self._rtmt(voice_choice="m"), {"allowed_voices": "marin"}, environ={})

    def test_a_dict_allowed_voices_is_rejected(self):
        from rtmt import configure_realtime_model
        with self.assertRaises(ValueError):
            configure_realtime_model(self._rtmt(), {"allowed_voices": {"marin": True}}, environ={})

    def test_a_list_allowed_voices_is_accepted(self):
        from rtmt import configure_realtime_model
        rtmt = configure_realtime_model(self._rtmt(), {"allowed_voices": ["marin", "cedar"]}, environ={})
        self.assertEqual(rtmt.allowed_voices, frozenset({"marin", "cedar"}))

    def test_empty_or_omitted_allowed_voices_falls_back_to_the_default_ten(self):
        from rtmt import _DEFAULT_ALLOWED_VOICES, configure_realtime_model
        for cfg in ({}, {"allowed_voices": []}, {"allowed_voices": None}):
            with self.subTest(cfg=cfg):
                rtmt = configure_realtime_model(self._rtmt(), cfg, environ={})
                self.assertEqual(rtmt.allowed_voices, _DEFAULT_ALLOWED_VOICES)

    def test_default_voice_not_in_allow_list_fails_at_startup(self):
        from rtmt import configure_realtime_model
        with self.assertRaises(ValueError):
            configure_realtime_model(self._rtmt(voice_choice="marin"),
                                      {"allowed_voices": ["cedar", "shimmer"]}, environ={})

    def test_default_voice_in_allow_list_succeeds(self):
        from rtmt import configure_realtime_model
        rtmt = configure_realtime_model(self._rtmt(voice_choice="cedar"),
                                         {"allowed_voices": ["cedar", "shimmer"]}, environ={})
        self.assertEqual(rtmt.voice_choice, "cedar")

    def test_no_default_voice_configured_skips_the_membership_check(self):
        """`voice_choice=None` means "send no voice at all" (see `_VOICE_UNSET`)
        -- not a voice pick that could ever be invalid."""
        from rtmt import configure_realtime_model
        rtmt = configure_realtime_model(self._rtmt(voice_choice=None),
                                         {"allowed_voices": ["cedar"]}, environ={})
        self.assertIsNone(rtmt.voice_choice)

    def test_shipped_config_default_voice_is_in_the_default_allow_list(self):
        import yaml

        from rtmt import _DEFAULT_ALLOWED_VOICES, configure_realtime_model
        cfg = yaml.safe_load((Path(__file__).resolve().parents[1] / "config.yaml").read_text(encoding="utf-8"))
        model_cfg = cfg["model"]
        self.assertNotIn("allowed_voices", model_cfg, "shipped config leaves this commented out/default")
        rtmt = configure_realtime_model(self._rtmt(voice_choice=model_cfg["default_voice"]), model_cfg, environ={})
        self.assertIn(rtmt.voice_choice, _DEFAULT_ALLOWED_VOICES)


class BuildSessionTests(unittest.TestCase):

    def _rtmt(self):
        rtmt = RTMiddleTier("https://fake.openai.azure.com", "gpt-realtime-test",
                            AzureKeyCredential("k"), voice_choice="shimmer")
        rtmt.system_message = SYSTEM_PROMPT
        rtmt.tools["update_order"] = Tool(target=MagicMock(), schema={"type": "function", "name": "update_order"})
        return rtmt

    def test_voice_locked_omits_voice_but_keeps_tools(self):
        session = self._rtmt()._build_session({}, voice_locked=True)
        self.assertNotIn("audio", session)
        self.assertEqual(session["tool_choice"], "auto")
        self.assertEqual(session["tools"][0]["name"], "update_order")
        self.assertEqual(session["instructions"], SYSTEM_PROMPT)

    def test_voice_locked_keeps_input_audio_settings(self):
        session = self._rtmt()._build_session(dict(BROWSER_SESSION_UPDATE["session"]), voice_locked=True)
        self.assertNotIn("output", session["audio"])
        self.assertIn("turn_detection", session["audio"]["input"])

    def test_unlocked_sets_voice(self):
        session = self._rtmt()._build_session({})
        self.assertEqual(session["audio"]["output"]["voice"], "shimmer")

    def test_bootstrap_payload_is_ga_shaped(self):
        payload = json.loads(self._rtmt().build_bootstrap_session_update())
        self.assertEqual(payload["type"], "session.update")
        session = payload["session"]
        self.assertEqual(session["type"], "realtime")
        for legacy in ("voice", "turn_detection", "input_audio_transcription", "temperature",
                       "max_response_output_tokens", "modalities"):
            self.assertNotIn(legacy, session)


if __name__ == "__main__":
    unittest.main()
