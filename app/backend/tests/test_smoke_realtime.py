"""scripts/smoke_realtime.py: the test audio is the phrase read aloud, the
transcript must match it word for word, the token comes from the resource's
tenant, and the postdeploy hook never fails `azd up`."""

import asyncio
import base64
import json
import os
import subprocess
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import yaml
from aiohttp import web
from aiohttp.test_utils import TestServer

REPO = Path(__file__).resolve().parents[3]
sys.path.append(str(REPO / "app" / "backend"))
sys.path.append(str(REPO / "scripts"))

import smoke_realtime  # noqa: E402

CLEAN_ENV = {
    "AZURE_OPENAI_REALTIME_VOICE_CHOICE": "",
    "AZURE_OPENAI_REALTIME_REASONING_EFFORT": "",
    "AZURE_OPENAI_REALTIME_REASONING_MODEL": "",
    "AZURE_OPENAI_REALTIME_TRANSCRIPTION_MODEL": "",
    "AZURE_OPENAI_EASTUS2_API_KEY": "",
}

PHRASE = smoke_realtime.TRANSCRIPTION_PHRASE
# What gpt-realtime-2.1 produced when handed the phrase as a user turn: it took
# the order instead of reading it. The old check passed this with a note.
ANSWERED = "Sure, I can't place the order for you, but a large cherry limeade and medium tots sounds tasty!"


class EchoingRealtime:
    """Fake /openai/v1/realtime: applies session.updates and echoes the session
    like GA does, "speaks" on response.create, and on input_audio_buffer.commit
    reports `self.transcript` as the guest's transcribed speech."""

    def __init__(self):
        self.reject_keys: set[str] = set()
        self.drop_tools = False
        self.transcript = PHRASE
        self.received: list[dict] = []
        self.auth: list[str | None] = []

    def app(self):
        app = web.Application()
        app.router.add_get("/openai/v1/realtime", self.handler)
        return app

    async def handler(self, request):
        self.auth.append(request.headers.get("api-key"))
        ws = web.WebSocketResponse()
        await ws.prepare(request)
        session: dict = {"type": "realtime", "tools": [], "tool_choice": "auto", "instructions": ""}
        async for msg in ws:
            event = json.loads(msg.data)
            self.received.append(event)
            kind = event["type"]
            if kind == "response.create":
                await ws.send_json({"type": "response.output_audio.delta",
                                    "delta": base64.b64encode(b"\x01\x00" * 2400).decode()})
                await ws.send_json({"type": "response.done"})
            elif kind == "input_audio_buffer.commit":
                await ws.send_json({"type": "conversation.item.input_audio_transcription.completed",
                                    "transcript": self.transcript})
            elif kind == "session.update":
                bad = sorted(k for k in event["session"] if k in self.reject_keys)
                if bad:
                    await ws.send_json({"type": "error", "error": {
                        "type": "invalid_request_error", "code": "invalid_value", "param": f"session.{bad[0]}",
                        "event_id": event.get("event_id"), "message": "Unsupported option."}})
                    continue
                session.update(event["session"])
                echoed = dict(session)
                if self.drop_tools:
                    echoed["tools"] = []
                await ws.send_json({"type": "session.updated", "session": echoed})
        return ws


class TranscriptMatchTests(unittest.TestCase):
    """A transcript passes only if it is the phrase, word for word."""

    def test_verbatim_and_formatting_variants_match(self):
        for transcript in (
            PHRASE,
            "hi can i get a large cherry limeade and a medium tots please",
            "  Hi, can I get a large cherry limeade, and a medium tots, please.  ",
            "Hi, can I get a large cherry lime-ade and a medium tots, please?",
            "Hi, can I get a large cherry lime aid and a medium tots, please?",
            # Two slips at once (about 0.95): still the phrase, so still a pass.
            "Hi, can I get a large cherry limeade and a medium tater tots, please?",
            "Hey can I get a large cherry lime aid and a medium tot please",
        ):
            with self.subTest(transcript):
                self.assertTrue(smoke_realtime.transcript_matches(PHRASE, transcript))

    def test_answers_and_paraphrases_do_not_match(self):
        for transcript in (
            ANSWERED,
            "Sure! One large cherry limeade and a medium tots coming right up.",
            "Can I get a cherry limeade and tots?",
            "Hi, can I get a large cherry limeade and a medium tots, please? Anything else for you today?",
            "",
            "   ",
        ):
            with self.subTest(transcript):
                self.assertFalse(smoke_realtime.transcript_matches(PHRASE, transcript))

    def test_normalisation(self):
        self.assertEqual(smoke_realtime.normalise_transcript("  Hi,   THERE!\n"), "hi there")
        self.assertEqual(smoke_realtime.normalise_transcript("申し訳ありません、少々お待ちください。"),
                         "申し訳ありません 少々お待ちください")
        self.assertEqual(smoke_realtime.transcript_similarity(PHRASE, PHRASE.upper()), 1.0)

    def test_threshold_is_between_a_misspelling_and_a_paraphrase(self):
        misspelt = smoke_realtime.transcript_similarity(PHRASE, PHRASE.replace("limeade", "lime aid"))
        dropped = smoke_realtime.transcript_similarity(PHRASE, "Can I get a cherry limeade and tots?")
        self.assertGreaterEqual(misspelt, smoke_realtime.TRANSCRIPT_MATCH_THRESHOLD)
        self.assertLess(dropped, smoke_realtime.TRANSCRIPT_MATCH_THRESHOLD)


class RealtimeUrlTests(unittest.TestCase):

    def test_azure_is_wss_and_a_local_fake_is_ws(self):
        self.assertEqual(smoke_realtime.realtime_url("https://r.openai.azure.com/", "gpt-realtime-2.1"),
                         "wss://r.openai.azure.com/openai/v1/realtime?model=gpt-realtime-2.1")
        self.assertEqual(smoke_realtime.realtime_url("http://127.0.0.1:8080", "d"),
                         "ws://127.0.0.1:8080/openai/v1/realtime?model=d")


class CheckSessionTests(unittest.TestCase):

    def _good(self):
        return {"tools": [{"name": n} for n in smoke_realtime.EXPECTED_TOOLS], "tool_choice": "auto",
                "instructions": "x", "reasoning": {"effort": "low"}, "audio": {"output": {"voice": "marin"}}}

    def test_pass(self):
        self.assertEqual(smoke_realtime.check_session("b", self._good(), self._good(), None, {"effort": "low"}), [])

    def test_each_failure_mode_is_reported(self):
        cases = {
            "rejected": (self._good(), None, {"error": {"code": "invalid_value", "param": "session.reasoning"}}),
            "tools": (self._good(), {**self._good(), "tools": [{"name": "search"}]}, None),
            "tool_choice": (self._good(), {**self._good(), "tool_choice": "none"}, None),
            "instructions": (self._good(), {**self._good(), "instructions": ""}, None),
            "reasoning": (self._good(), {**self._good(), "reasoning": {"effort": "none"}}, None),
            "voice": (self._good(), {**self._good(), "audio": {"output": {"voice": "alloy"}}}, None),
        }
        for name, (sent, echoed, error) in cases.items():
            with self.subTest(name):
                failures = smoke_realtime.check_session("b", sent, echoed, error, {"effort": "low"})
                self.assertEqual(len(failures), 1, failures)


class _FakeServerCase(unittest.IsolatedAsyncioTestCase):

    async def asyncSetUp(self):
        self.fake = EchoingRealtime()
        self.server = TestServer(self.fake.app())
        await self.server.start_server()
        self.endpoint = str(self.server.make_url("/"))
        env = patch.dict(os.environ, CLEAN_ENV)
        env.start()
        self.addCleanup(env.stop)

    async def asyncTearDown(self):
        await self.server.close()


class SynthesizeTests(_FakeServerCase):
    """The test audio must be the phrase read aloud, not the model's reply to it."""

    async def test_phrase_is_sent_as_response_instructions_not_a_user_turn(self):
        url = smoke_realtime.realtime_url(self.endpoint, "gpt-realtime-2.1")
        pcm = await smoke_realtime._synthesize(url, {}, PHRASE, 5)

        self.assertEqual(pcm, b"\x01\x00" * 2400)
        kinds = [e["type"] for e in self.fake.received]
        self.assertEqual(kinds, ["session.update", "response.create"])
        create = self.fake.received[1]
        self.assertIn(f'"{PHRASE}"', create["response"]["instructions"])
        self.assertIn("word for word", create["response"]["instructions"])
        session = self.fake.received[0]["session"]
        self.assertIsNone(session["audio"]["input"]["turn_detection"])
        self.assertEqual(session["audio"]["output"]["voice"], "alloy")
        self.assertNotIn(PHRASE, session["instructions"])

    async def test_voice_is_configurable(self):
        url = smoke_realtime.realtime_url(self.endpoint, "gpt-realtime-2.1")
        await smoke_realtime._synthesize(url, {}, "Hello.", 5, voice="marin")
        self.assertEqual(self.fake.received[0]["session"]["audio"]["output"]["voice"], "marin")


class CheckTranscriptionTests(_FakeServerCase):
    """check_transcription fails when the transcript is not the phrase."""

    async def _check(self):
        rtmt = smoke_realtime.build_middle_tier(self.endpoint, "gpt-realtime-2.1", environ=dict(CLEAN_ENV))
        url = smoke_realtime.realtime_url(self.endpoint, "gpt-realtime-2.1")
        return await smoke_realtime.check_transcription(rtmt, url, {}, 5)

    async def test_verbatim_transcript_passes(self):
        failures, report = await self._check()
        self.assertEqual(failures, [])
        self.assertIn("matches the test phrase", report[0])
        commits = [e for e in self.fake.received if e["type"] == "input_audio_buffer.commit"]
        self.assertEqual(len(commits), 1)

    async def test_answered_transcript_fails(self):
        self.fake.transcript = ANSWERED
        failures, report = await self._check()
        self.assertEqual(report, [])
        self.assertEqual(len(failures), 1)
        self.assertIn("does not match the test phrase", failures[0])

    async def test_empty_transcript_fails(self):
        self.fake.transcript = ""
        failures, _ = await self._check()
        self.assertEqual(len(failures), 1)
        self.assertIn("empty transcript", failures[0])


class LiveShapeTests(_FakeServerCase):
    """End to end against a fake GA endpoint (no Azure)."""

    async def _run(self, skip_transcription=True):
        return await smoke_realtime.run(self.endpoint, "gpt-realtime-2.1", voice=None, timeout=5,
                                        skip_transcription=skip_transcription, headers={"api-key": "k"})

    async def test_passes_when_the_session_is_accepted(self):
        self.assertEqual(await self._run(), 0)
        updates = [e for e in self.fake.received if e["type"] == "session.update"]
        self.assertEqual(len(updates), 3)
        self.assertEqual(self.fake.auth, ["k"])

    async def test_fails_when_reasoning_is_rejected(self):
        self.fake.reject_keys = {"reasoning"}
        self.assertEqual(await self._run(), 1)

    async def test_fails_when_tools_do_not_register(self):
        self.fake.drop_tools = True
        self.assertEqual(await self._run(), 1)

    async def test_with_transcription_passes_on_a_verbatim_transcript(self):
        self.assertEqual(await self._run(skip_transcription=False), 0)

    async def test_with_transcription_fails_on_an_answered_transcript(self):
        self.fake.transcript = ANSWERED
        self.assertEqual(await self._run(skip_transcription=False), 1)

    async def test_unreachable_endpoint_is_could_not_run(self):
        await self.server.close()
        with self.assertRaises(smoke_realtime.SmokeError):
            await self._run()


def _isolated_main(argv, env, azd):
    """Run main() with only `env` set for the names it reads, `azd` as the azd
    env, and a fake run(); return (exit code, run kwargs, env seen by run)."""
    names = ["AZURE_TENANT_ID", "AZURE_SUBSCRIPTION_ID", "AZURE_OPENAI_EASTUS2_ENDPOINT",
             "AZURE_OPENAI_REALTIME_DEPLOYMENT", "AZURE_OPENAI_EASTUS2_API_KEY", *CLEAN_ENV]
    seen = {}

    async def fake_run(*_a, **kwargs):
        seen["kwargs"] = kwargs
        seen["env"] = {n: os.environ.get(n) for n in CLEAN_ENV}
        return 0
    saved = {n: os.environ.pop(n, None) for n in names}
    try:
        os.environ.update(env)
        with patch.object(smoke_realtime, "_azd_env_values", return_value=azd), \
                patch.object(smoke_realtime, "run", fake_run):
            code = smoke_realtime.main(argv)
    finally:
        for n in names:
            os.environ.pop(n, None)
            if saved[n] is not None:
                os.environ[n] = saved[n]
    return code, seen.get("kwargs"), seen.get("env")


class MainTests(unittest.TestCase):

    def test_missing_settings_exit_2(self):
        code, _, _ = _isolated_main([], {}, {})
        self.assertEqual(code, 2)

    def test_smoke_error_exit_2(self):
        async def boom(*_a, **_k):
            raise smoke_realtime.SmokeError("no token")
        with patch.object(smoke_realtime, "run", boom), \
                patch.object(smoke_realtime, "_azd_env_values", return_value={}):
            self.assertEqual(smoke_realtime.main(["--endpoint", "https://x", "--deployment", "d"]), 2)

    def test_azd_values_fill_unset_app_settings(self):
        azd = {"AZURE_OPENAI_EASTUS2_ENDPOINT": "https://x", "AZURE_OPENAI_REALTIME_DEPLOYMENT": "gpt-realtime-2.1",
               "AZURE_OPENAI_REALTIME_REASONING_EFFORT": "medium", "AZURE_OPENAI_REALTIME_REASONING_MODEL": "false",
               "AZURE_OPENAI_REALTIME_TRANSCRIPTION_MODEL": "whisper-1", "AZURE_OPENAI_REALTIME_VOICE_CHOICE": "cedar"}
        code, _, env = _isolated_main([], {}, azd)
        self.assertEqual(code, 0)
        app_settings = [n for n in CLEAN_ENV if n != "AZURE_OPENAI_EASTUS2_API_KEY"]
        self.assertEqual(env, {**{n: azd[n] for n in app_settings}, "AZURE_OPENAI_EASTUS2_API_KEY": None})


class TenantTests(unittest.TestCase):
    """The token must come from the resource's tenant, not the active `az` or `azd` default."""

    AZD = {"AZURE_OPENAI_EASTUS2_ENDPOINT": "https://x", "AZURE_OPENAI_REALTIME_DEPLOYMENT": "d",
           "AZURE_TENANT_ID": "azd-tenant", "AZURE_SUBSCRIPTION_ID": "azd-sub"}
    EXPLICIT = ["--endpoint", "https://x", "--deployment", "d"]

    def _main_identity(self, argv, env, azd):
        code, kwargs, _ = _isolated_main(argv, env, azd)
        self.assertEqual(code, 0)
        return kwargs.get("tenant_id"), kwargs.get("subscription_id")

    def test_azd_env_identity_is_used(self):
        self.assertEqual(self._main_identity([], {}, self.AZD), ("azd-tenant", "azd-sub"))

    def test_azd_env_identity_is_used_even_with_explicit_endpoint_and_deployment(self):
        self.assertEqual(self._main_identity(self.EXPLICIT, {}, self.AZD), ("azd-tenant", "azd-sub"))

    def test_env_then_cli_override_azd(self):
        env = {"AZURE_TENANT_ID": "env-tenant", "AZURE_SUBSCRIPTION_ID": "env-sub"}
        self.assertEqual(self._main_identity([], env, self.AZD), ("env-tenant", "env-sub"))
        self.assertEqual(self._main_identity(["--tenant", "cli-tenant", "--subscription", "cli-sub"], env, self.AZD),
                         ("cli-tenant", "cli-sub"))

    def test_each_value_falls_back_to_azd_independently(self):
        self.assertEqual(self._main_identity(["--tenant", "cli-tenant"], {}, self.AZD), ("cli-tenant", "azd-sub"))
        self.assertEqual(self._main_identity([], {"AZURE_SUBSCRIPTION_ID": "env-sub"}, self.AZD),
                         ("azd-tenant", "env-sub"))

    def test_nothing_anywhere_is_none(self):
        self.assertEqual(self._main_identity(self.EXPLICIT, {}, {}), (None, None))

    def test_api_key_skips_identity(self):
        self.assertEqual(self._main_identity([], {"AZURE_OPENAI_EASTUS2_API_KEY": "k"}, self.AZD), (None, None))

    def test_run_passes_identity_to_auth(self):
        seen = {}

        def fake_auth(*args):
            seen["args"] = args
            raise smoke_realtime.SmokeError("stop here")
        with patch.object(smoke_realtime, "get_auth_headers", fake_auth), \
                self.assertRaises(smoke_realtime.SmokeError):
            asyncio.run(smoke_realtime.run("https://x", "d", voice=None, timeout=1, skip_transcription=True,
                                           tenant_id="t", subscription_id="s"))
        self.assertEqual(seen["args"], ("t", "s"))

    def _auth(self, tenant_id, subscription_id, fail=(), api_key=""):
        """Returns (headers or SmokeError, credentials built, credentials asked for a token)."""
        built, asked = [], []

        class FakeCred:
            def __init__(self, kind, **kwargs):
                self.kind = kind
                built.append((kind, kwargs.get("tenant_id") or kwargs.get("subscription")))

            def get_token(self, *scopes, **_kw):
                asked.append((self.kind, scopes))
                if self.kind in fail:
                    raise RuntimeError(f"{self.kind} said no\nsecond line")
                return SimpleNamespace(token=f"tok-{self.kind}", expires_on=0)

        def az(**k):
            return FakeCred("az-sub" if k.get("subscription") else "az", **k)

        import azure.identity as identity
        with patch.dict(os.environ, {"AZURE_OPENAI_EASTUS2_API_KEY": api_key}), \
                patch.object(identity, "AzureDeveloperCliCredential", lambda **k: FakeCred("azd", **k)), \
                patch.object(identity, "AzureCliCredential", az), \
                patch.object(identity, "DefaultAzureCredential", lambda **k: FakeCred("default", **k)):
            try:
                result = smoke_realtime.get_auth_headers(tenant_id, subscription_id)
            except smoke_realtime.SmokeError as exc:
                result = exc
        return result, built, [kind for kind, _ in asked], [s for _, s in asked]

    def test_subscription_first_then_tenant_pinned_clis(self):
        headers, built, asked, scopes = self._auth("tenant-x", "sub-y")
        self.assertEqual(built, [("az-sub", "sub-y"), ("azd", "tenant-x"), ("az", "tenant-x")])
        self.assertEqual(asked, ["az-sub"])
        self.assertEqual(scopes, [("https://cognitiveservices.azure.com/.default",)])
        self.assertEqual(headers, {"Authorization": "Bearer " + "tok-az-sub"})

    def test_hard_failure_moves_on_to_the_next_credential(self):
        headers, _, asked, _ = self._auth("tenant-x", "sub-y", fail=("az-sub", "azd"))
        self.assertEqual(asked, ["az-sub", "azd", "az"])
        self.assertEqual(headers, {"Authorization": "Bearer " + "tok-az"})

    def test_all_failing_reports_every_credential(self):
        err, _, asked, _ = self._auth("tenant-x", "sub-y", fail=("az-sub", "azd", "az"))
        self.assertIsInstance(err, smoke_realtime.SmokeError)
        self.assertEqual(asked, ["az-sub", "azd", "az"])
        self.assertIn("az-sub said no", str(err))
        self.assertIn("azd said no", str(err))
        self.assertNotIn("second line", str(err))

    def test_tenant_only_pins_both_clis(self):
        _, built, _, _ = self._auth("tenant-x", None)
        self.assertEqual(built, [("azd", "tenant-x"), ("az", "tenant-x")])

    def test_subscription_only(self):
        _, built, _, _ = self._auth(None, "sub-y")
        self.assertEqual(built, [("az-sub", "sub-y")])

    def test_nothing_falls_back_to_default_credential(self):
        headers, built, _, _ = self._auth(None, None)
        self.assertEqual(built, [("default", None)])
        self.assertEqual(headers, {"Authorization": "Bearer " + "tok-default"})

    def test_api_key_wins(self):
        headers, built, _, _ = self._auth("tenant-x", "sub-y", api_key="secret")
        self.assertEqual(headers, {"api-key": "secret"})
        self.assertEqual(built, [])

    def test_no_args_still_works_for_the_benchmark(self):
        # scripts/benchmark_reasoning.py calls get_auth_headers() with no arguments.
        headers, built, _, _ = self._auth(None, None)
        self.assertEqual(built, [("default", None)])
        self.assertIn("Authorization", headers)


class PostdeployHookTests(unittest.TestCase):
    """An anonymous external `azd up` must never fail because of the smoke check."""

    def test_azure_yaml_postdeploy_is_non_fatal_and_non_interactive(self):
        hooks = yaml.safe_load((REPO / "azure.yaml").read_text(encoding="utf-8"))["hooks"]
        for platform, script in (("windows", "./scripts/smoke_realtime.ps1"), ("posix", "./scripts/smoke_realtime.sh")):
            with self.subTest(platform):
                hook = hooks["postdeploy"][platform]
                self.assertEqual(hook["run"], script)
                self.assertIs(hook["continueOnError"], True)
                self.assertIs(hook["interactive"], False)
                self.assertTrue((REPO / script).is_file())

    def _wrapper_text(self, name):
        return (REPO / "scripts" / name).read_text(encoding="utf-8")

    def test_wrappers_always_exit_0(self):
        for name in ("smoke_realtime.ps1", "smoke_realtime.sh"):
            with self.subTest(name):
                exits = [line.strip() for line in self._wrapper_text(name).splitlines()
                         if line.strip().startswith("exit")]
                self.assertTrue(exits)
                self.assertEqual(set(exits), {"exit 0"})
                self.assertIn("SONIC_SKIP_REALTIME_SMOKE", self._wrapper_text(name))

    def test_sh_wrapper_is_committed_with_lf(self):
        eol = subprocess.run(["git", "ls-files", "--eol", "scripts/smoke_realtime.sh"], cwd=REPO,
                             capture_output=True, text=True).stdout
        if eol:  # tracked
            self.assertIn("i/lf", eol)
            self.assertIn("eol=lf", eol)


if __name__ == "__main__":
    unittest.main()
