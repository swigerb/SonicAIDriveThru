"""Post-deploy smoke check for the realtime session configuration.

Builds the EXACT session.update payloads the middle tier sends (bootstrap,
relayed browser update, minimal fallback) from the app code and config.yaml,
sends them to a live Azure OpenAI realtime deployment, and fails unless every
one comes back as `session.updated` with all tools registered, tool_choice
"auto", the instructions applied, and no `error`.

Why: GA rejects an invalid session.update WHOLESALE. One bad field (a voice
change after audio, `reasoning` on a non-reasoning model, ...) silently drops
the tools and the carhop takes orders it never records. This demo has lost its
tools that way twice.

It also checks, unless --skip-transcription, that guest speech is actually
transcribed with the configured transcription model: Azure accepts any model
name in session.update and only fails later, per turn, with DeploymentNotFound.
The test audio is the realtime model reading TRANSCRIPTION_PHRASE aloud, and the
transcript must match that phrase word for word (after normalising case,
punctuation and spacing); a model that answered or paraphrased the phrase
instead of reading it fails the check rather than passing it on a technicality.

Usage (from the repo root, with `az login` / `azd auth login` done):

    python scripts/smoke_realtime.py                       # uses azd env / env vars
    python scripts/smoke_realtime.py --deployment gpt-realtime-1.5
    python scripts/smoke_realtime.py --endpoint https://<aoai>.openai.azure.com/ --deployment gpt-realtime-2.1

    python scripts/smoke_realtime.py --tenant <tenant-id> --subscription <subscription-id>

Endpoint/deployment default to AZURE_OPENAI_EASTUS2_ENDPOINT /
AZURE_OPENAI_REALTIME_DEPLOYMENT, read from the environment or `azd env
get-values`. Auth: AZURE_OPENAI_EASTUS2_API_KEY if set, else an Entra ID token
from the resource's tenant (needs "Cognitive Services OpenAI User" on the
resource, which `azd up` grants the deploying principal). The tenant and
subscription come from --tenant / --subscription, else AZURE_TENANT_ID /
AZURE_SUBSCRIPTION_ID in the environment, else the azd env. With a subscription,
`az` is asked for a token for that subscription (the sign-in that owns it, without
changing the global `az account` default); with a tenant, azd and az are pinned
to it; with neither, DefaultAzureCredential. So a machine signed in to several
tenants no longer gets HTTP 400 "Tenant provided in token does not match
resource token".

Exit codes: 0 = all checks passed, 1 = a check failed, 2 = could not run
(missing endpoint/deployment, auth or network failure).
"""
from __future__ import annotations

import argparse
import asyncio
import base64
import copy
import difflib
import json
import os
import re
import subprocess
import sys
import time
import unicodedata
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
BACKEND_DIR = REPO_ROOT / "app" / "backend"
sys.path.insert(0, str(BACKEND_DIR))

import aiohttp  # noqa: E402
from azure.core.credentials import AzureKeyCredential  # noqa: E402

from config_loader import get_config  # noqa: E402
from prompt_loader import PromptLoader  # noqa: E402
from rtmt import RTMiddleTier, Tool, configure_realtime_model  # noqa: E402

EXPECTED_TOOLS = ("search", "update_order", "get_order", "reset_order")

# What app/frontend/src/hooks/useRealtime.tsx startSession() sends.
BROWSER_SESSION = {
    "turn_detection": {"type": "server_vad", "threshold": 0.7, "prefix_padding_ms": 300, "silence_duration_ms": 500},
    "input_audio_transcription": {"model": "whisper-1"},
}

TRANSCRIPTION_PHRASE = "Hi, can I get a large cherry limeade and a medium tots, please?"
# Minimum similarity (difflib ratio over the normalised text) between the phrase
# and its transcript. Tolerates a transcriber's spelling ("lime aid") but not an
# answer or a paraphrase.
TRANSCRIPT_MATCH_THRESHOLD = 0.85


class SmokeError(Exception):
    """The smoke check could not run (exit 2), as opposed to a failed check."""


def _azd_env_values() -> dict[str, str]:
    try:
        out = subprocess.run(["azd", "env", "get-values"], capture_output=True, text=True, timeout=30,
                             cwd=REPO_ROOT, shell=(os.name == "nt"))
    except (OSError, subprocess.SubprocessError):
        return {}
    if out.returncode != 0:
        return {}
    values = {}
    for line in out.stdout.splitlines():
        if "=" in line:
            key, _, value = line.partition("=")
            values[key.strip()] = value.strip().strip('"')
    return values


def resolve_setting(name: str, cli_value: str | None, azd_values: dict[str, str]) -> str | None:
    return cli_value or os.environ.get(name) or azd_values.get(name) or None


def _tool_schemas(prompt_loader: PromptLoader) -> list[dict]:
    """Same resolution as tools.attach_tools_rtmt: YAML schema, else the hardcoded one."""
    import tools
    schema_map = {s["name"]: s for s in prompt_loader.get_tool_schemas()}
    fallback = {
        "search": tools.search_tool_schema,
        "update_order": tools.update_order_tool_schema,
        "get_order": tools.get_order_tool_schema,
        "reset_order": tools.reset_order_tool_schema,
    }
    return [schema_map.get(name, fallback[name]) for name in EXPECTED_TOOLS]


def build_middle_tier(endpoint: str, deployment: str, voice: str | None = None,
                      environ: dict | None = None) -> RTMiddleTier:
    """An RTMiddleTier configured exactly like app.create_app() configures it
    (config.yaml `model:` + env overrides, real system prompt and tool schemas),
    without starting a server or touching Azure AI Search."""
    env = os.environ if environ is None else environ
    model_cfg = get_config().get("model", {})
    prompt_loader = PromptLoader()
    rtmt = RTMiddleTier(
        endpoint=endpoint,
        deployment=deployment,
        credentials=AzureKeyCredential("unused"),
        voice_choice=voice or env.get("AZURE_OPENAI_REALTIME_VOICE_CHOICE") or model_cfg.get("default_voice", "marin"),
    )
    configure_realtime_model(rtmt, model_cfg, env)
    rtmt.system_message = prompt_loader.get_system_prompt()
    for schema in _tool_schemas(prompt_loader):
        rtmt.tools[schema["name"]] = Tool(target=None, schema=schema)
    return rtmt


_TOKEN_SCOPE = "https://cognitiveservices.azure.com/.default"


def _credentials(tenant_id: str | None, subscription_id: str | None) -> list:
    """Credentials to try, most specific first.

    The token must come from the resource's tenant. Following whatever `az` or
    `azd` default is active gets HTTP 400 "Tenant provided in token does not match
    resource token" as soon as that default is another tenant, which is common on
    a machine with several sign-ins.
    """
    from azure.identity import (
        AzureCliCredential,
        AzureDeveloperCliCredential,
        DefaultAzureCredential,
    )
    creds = []
    if subscription_id:
        # Picks the `az` sign-in that owns the azd env's subscription, without
        # changing the global `az account` default.
        creds.append(AzureCliCredential(subscription=subscription_id, process_timeout=60))
    if tenant_id:
        creds.append(AzureDeveloperCliCredential(tenant_id=tenant_id, process_timeout=60))
        creds.append(AzureCliCredential(tenant_id=tenant_id, process_timeout=60))
    if not creds:
        creds.append(DefaultAzureCredential(exclude_interactive_browser_credential=True))
    return creds


def get_auth_headers(tenant_id: str | None = None, subscription_id: str | None = None) -> dict[str, str]:
    if key := os.environ.get("AZURE_OPENAI_EASTUS2_API_KEY"):
        return {"api-key": key}
    errors = []
    try:
        credentials = _credentials(tenant_id, subscription_id)
    except Exception as exc:  # noqa: BLE001 - any credential failure means "cannot run"
        raise SmokeError(f"could not get an Entra ID token for Azure OpenAI: {exc}") from exc
    # Tried in turn: unlike ChainedTokenCredential, a hard auth error (e.g. azd
    # signed in as a user who isn't in the tenant) moves on to the next one.
    for credential in credentials:
        try:
            return {"Authorization": "Bearer " + credential.get_token(_TOKEN_SCOPE).token}
        except Exception as exc:  # noqa: BLE001
            errors.append(f"{type(credential).__name__}: {str(exc).splitlines()[0] if str(exc) else exc!r}")
    raise SmokeError("could not get an Entra ID token for Azure OpenAI: " + "; ".join(errors))


def realtime_url(endpoint: str, deployment: str) -> str:
    host = endpoint.split("://", 1)[-1].rstrip("/")
    # Plain http only for a local fake endpoint (tests); Azure is always wss.
    scheme = "ws" if endpoint.startswith("http://") else "wss"
    return f"{scheme}://{host}/openai/v1/realtime?model={deployment}"


async def _next_event(ws, timeout: float) -> dict | None:
    msg = await asyncio.wait_for(ws.receive(), timeout=timeout)
    if msg.type in (aiohttp.WSMsgType.CLOSE, aiohttp.WSMsgType.CLOSED, aiohttp.WSMsgType.ERROR):
        raise SmokeError(f"upstream closed the socket ({msg.type.name}: {msg.data!r})")
    if msg.type != aiohttp.WSMsgType.TEXT:
        return None
    return json.loads(msg.data)


async def send_session_update(ws, payload: str, timeout: float) -> tuple[dict | None, dict | None]:
    """Send one session.update; return (session.updated session, error event)."""
    await ws.send_str(payload)
    deadline = time.monotonic() + timeout
    while (remaining := deadline - time.monotonic()) > 0:
        try:
            event = await _next_event(ws, remaining)
        except TimeoutError:
            break
        if event is None:
            continue
        if event.get("type") == "error":
            return None, event
        if event.get("type") == "session.updated":
            return event.get("session") or {}, None
    return None, {"error": {"code": "timeout", "message": f"no session.updated within {timeout:.0f}s"}}


def check_session(label: str, sent: dict, echoed: dict | None, error: dict | None,
                  expect_reasoning: dict | None) -> list[str]:
    """Return a list of failure strings (empty = pass)."""
    if error is not None:
        err = error.get("error") or {}
        return [f"{label}: REJECTED code={err.get('code')} param={err.get('param')} message={err.get('message')}"]
    failures = []
    tools = sorted(t.get("name") for t in echoed.get("tools") or [])
    if tools != sorted(EXPECTED_TOOLS):
        failures.append(f"{label}: tools registered {tools}, expected {sorted(EXPECTED_TOOLS)}")
    if echoed.get("tool_choice") != "auto":
        failures.append(f"{label}: tool_choice={echoed.get('tool_choice')!r}, expected 'auto'")
    if not echoed.get("instructions"):
        failures.append(f"{label}: instructions were not applied")
    if expect_reasoning is not None and echoed.get("reasoning") != expect_reasoning:
        failures.append(f"{label}: reasoning={echoed.get('reasoning')!r}, expected {expect_reasoning!r}")
    sent_voice = ((sent.get("audio") or {}).get("output") or {}).get("voice")
    echoed_voice = ((echoed.get("audio") or {}).get("output") or {}).get("voice")
    if sent_voice and echoed_voice != sent_voice:
        failures.append(f"{label}: voice={echoed_voice!r}, expected {sent_voice!r}")
    return failures


async def check_session_updates(rtmt: RTMiddleTier, url: str, headers: dict, timeout: float) -> tuple[list[str], list[str]]:
    payloads = [
        ("bootstrap", rtmt.build_bootstrap_session_update()),
        ("relayed browser session.update",
         json.dumps({"type": "session.update", "event_id": "smoke_relayed",
                     "session": rtmt._build_session(copy.deepcopy(BROWSER_SESSION))})),
        ("minimal fallback", rtmt.build_fallback_session_update()),
    ]
    expect_reasoning = {"effort": rtmt.reasoning_effort} if rtmt.reasoning_enabled() else None
    failures: list[str] = []
    report: list[str] = []
    async with aiohttp.ClientSession() as http, http.ws_connect(url, headers=headers) as ws:
        for label, payload in payloads:
            sent = json.loads(payload)["session"]
            echoed, error = await send_session_update(ws, payload, timeout)
            problems = check_session(label, sent, echoed, error,
                                     expect_reasoning if "reasoning" in sent else None)
            failures += problems
            if problems:
                report += [f"FAIL  {p}" for p in problems]
            else:
                report.append(f"PASS  {label}: session.updated, {len(echoed.get('tools') or [])} tools, "
                              f"tool_choice=auto, reasoning={echoed.get('reasoning')}")
    return failures, report


def synthesis_instructions(text: str) -> str:
    return f"Say exactly this sentence, word for word, and nothing else: \"{text}\""


async def _synthesize(url: str, headers: dict, text: str, timeout: float, voice: str = "alloy") -> bytes:
    """24 kHz mono PCM16 of the realtime model reading `text` aloud."""
    pcm = bytearray()
    async with aiohttp.ClientSession() as http, http.ws_connect(url, headers=headers) as ws:
        await ws.send_json({"type": "session.update", "session": {
            "type": "realtime",
            "instructions": "You are a text-to-speech engine. Say only what you are told to say.",
            "audio": {"input": {"turn_detection": None}, "output": {"voice": voice}}}})
        # The phrase goes in the response instructions, not a user turn: given a user
        # turn, gpt-realtime-2.1 answers the order instead of reading it. Live probe
        # (Sonic, 2.1 and 2.1-dz, 3 runs each): user turn 1/6 verbatim, this 6/6.
        await ws.send_json({"type": "response.create", "response": {"instructions": synthesis_instructions(text)}})
        deadline = time.monotonic() + timeout
        while (remaining := deadline - time.monotonic()) > 0:
            event = await _next_event(ws, remaining)
            if event is None:
                continue
            if event["type"] == "response.output_audio.delta":
                pcm += base64.b64decode(event["delta"])
            elif event["type"] == "response.done":
                break
            elif event["type"] == "error":
                raise SmokeError(f"could not synthesize test audio: {event.get('error')}")
    return bytes(pcm)


def normalise_transcript(text: str) -> str:
    """Lower case, no punctuation or symbols, single spaces (NFKC first, so
    full-width Japanese punctuation goes too)."""
    text = unicodedata.normalize("NFKC", text or "").lower()
    text = "".join(" " if unicodedata.category(ch)[0] in "PS" else ch for ch in text)
    return re.sub(r"\s+", " ", text).strip()


def transcript_similarity(phrase: str, transcript: str) -> float:
    return difflib.SequenceMatcher(None, normalise_transcript(phrase), normalise_transcript(transcript)).ratio()


def transcript_matches(phrase: str, transcript: str, threshold: float = TRANSCRIPT_MATCH_THRESHOLD) -> bool:
    """True if `transcript` is `phrase`, word for word, give or take case,
    punctuation, spacing and a transcriber's spelling."""
    return bool(normalise_transcript(transcript)) and transcript_similarity(phrase, transcript) >= threshold


async def check_transcription(rtmt: RTMiddleTier, url: str, headers: dict, timeout: float) -> tuple[list[str], list[str]]:
    pcm = await _synthesize(url, headers, TRANSCRIPTION_PHRASE, timeout)
    if not pcm:
        raise SmokeError("could not synthesize test audio (no audio returned)")
    session = json.loads(rtmt.build_bootstrap_session_update())["session"]
    # Commit explicitly instead of waiting on server VAD; same transcription field.
    session["audio"]["input"]["turn_detection"] = None
    model = session["audio"]["input"].get("transcription", {}).get("model")
    async with aiohttp.ClientSession() as http, http.ws_connect(url, headers=headers) as ws:
        echoed, error = await send_session_update(
            ws, json.dumps({"type": "session.update", "session": session}), timeout)
        if error is not None:
            return [f"transcription: session.update rejected: {error.get('error')}"], []
        for i in range(0, len(pcm), 4800):
            await ws.send_json({"type": "input_audio_buffer.append", "audio": base64.b64encode(pcm[i:i + 4800]).decode()})
        await ws.send_json({"type": "input_audio_buffer.commit"})
        deadline = time.monotonic() + timeout
        while (remaining := deadline - time.monotonic()) > 0:
            try:
                event = await _next_event(ws, remaining)
            except TimeoutError:
                break
            if event is None:
                continue
            kind = event.get("type")
            if kind == "conversation.item.input_audio_transcription.completed":
                transcript = event.get("transcript") or ""
                if not transcript.strip():
                    return [f"transcription ({model}): completed with an empty transcript"], []
                similarity = transcript_similarity(TRANSCRIPTION_PHRASE, transcript)
                if not transcript_matches(TRANSCRIPTION_PHRASE, transcript):
                    # Either the TTS step answered or paraphrased the phrase instead of
                    # reading it, or the transcriber got it wrong. Both mean this check
                    # did not show that guest speech is transcribed faithfully.
                    return [f"transcription ({model}): transcript {transcript!r} does not match the test phrase "
                            f"{TRANSCRIPTION_PHRASE!r} (similarity {similarity:.2f} < "
                            f"{TRANSCRIPT_MATCH_THRESHOLD:.2f})"], []
                return [], [f"PASS  transcription ({model}): {transcript!r} (matches the test phrase, "
                            f"similarity {similarity:.2f})"]
            if kind == "conversation.item.input_audio_transcription.failed":
                err = event.get("error") or {}
                return [f"transcription ({model}): FAILED code={err.get('code')} message={err.get('message')} "
                        "-- guest speech will not be transcribed (does this model need its own deployment?)"], []
            if kind == "error":
                return [f"transcription ({model}): error {event.get('error')}"], []
    return [f"transcription ({model}): no transcription event within {timeout:.0f}s"], []


async def run(endpoint: str, deployment: str, *, voice: str | None, timeout: float,
              skip_transcription: bool, headers: dict[str, str] | None = None,
              tenant_id: str | None = None, subscription_id: str | None = None) -> int:
    rtmt = build_middle_tier(endpoint, deployment, voice)
    url = realtime_url(endpoint, deployment)
    print(f"Realtime smoke check: deployment={deployment} voice={rtmt.voice_choice} "
          f"transcription={rtmt.transcription_model} "
          f"reasoning={rtmt.reasoning_effort if rtmt.reasoning_enabled() else 'off'}")
    headers = get_auth_headers(tenant_id, subscription_id) if headers is None else headers
    try:
        failures, report = await check_session_updates(rtmt, url, headers, timeout)
        if not skip_transcription:
            t_failures, t_report = await check_transcription(rtmt, url, headers, timeout)
            failures += t_failures
            report += t_report + [f"FAIL  {f}" for f in t_failures]
    except aiohttp.WSServerHandshakeError as exc:
        raise SmokeError(f"websocket handshake failed: HTTP {exc.status} {exc.message}") from exc
    except (aiohttp.ClientError, OSError) as exc:
        raise SmokeError(f"could not reach {url}: {exc}") from exc
    for line in report:
        print(f"  {line}")
    if failures:
        print(f"SMOKE CHECK FAILED ({len(failures)} problem(s)) -- the carhop would run without its tools "
              "or without transcripts on this deployment.")
        return 1
    print("SMOKE CHECK PASSED")
    return 0


def resolve_identity(tenant: str | None, subscription: str | None,
                     azd_values: dict[str, str] | None = None) -> tuple[str | None, str | None]:
    """(tenant id, subscription id): CLI, else env, else the azd env, each on its own.
    The azd env is read only when something is still missing."""
    identity = {"AZURE_TENANT_ID": tenant or os.environ.get("AZURE_TENANT_ID") or None,
                "AZURE_SUBSCRIPTION_ID": subscription or os.environ.get("AZURE_SUBSCRIPTION_ID") or None}
    if not all(identity.values()):
        azd_values = azd_values or _azd_env_values()
        for name, value in identity.items():
            identity[name] = value or azd_values.get(name) or None
    return identity["AZURE_TENANT_ID"], identity["AZURE_SUBSCRIPTION_ID"]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--endpoint", help="Azure OpenAI endpoint (default: AZURE_OPENAI_EASTUS2_ENDPOINT)")
    parser.add_argument("--deployment", help="Realtime deployment (default: AZURE_OPENAI_REALTIME_DEPLOYMENT)")
    parser.add_argument("--voice", help="Voice to send (default: AZURE_OPENAI_REALTIME_VOICE_CHOICE or config.yaml)")
    parser.add_argument("--tenant", help="Entra tenant of the Azure OpenAI resource (default: AZURE_TENANT_ID)")
    parser.add_argument("--subscription", help="Subscription whose `az` sign-in to use (default: AZURE_SUBSCRIPTION_ID)")
    parser.add_argument("--timeout", type=float, default=20.0, help="Seconds to wait per server reply (default 20)")
    parser.add_argument("--skip-transcription", action="store_true",
                        help="Skip the live speech-transcription check")
    args = parser.parse_args(argv)

    azd_values = {} if (args.endpoint and args.deployment) else _azd_env_values()
    endpoint = resolve_setting("AZURE_OPENAI_EASTUS2_ENDPOINT", args.endpoint, azd_values)
    deployment = resolve_setting("AZURE_OPENAI_REALTIME_DEPLOYMENT", args.deployment, azd_values)
    if not endpoint or not deployment:
        print("Realtime smoke check could not run: set --endpoint/--deployment or "
              "AZURE_OPENAI_EASTUS2_ENDPOINT/AZURE_OPENAI_REALTIME_DEPLOYMENT (or run inside an azd env).",
              file=sys.stderr)
        return 2
    # Every env var the app reads for the realtime session.
    for name in ("AZURE_OPENAI_REALTIME_REASONING_EFFORT", "AZURE_OPENAI_REALTIME_REASONING_MODEL",
                 "AZURE_OPENAI_REALTIME_TRANSCRIPTION_MODEL", "AZURE_OPENAI_REALTIME_VOICE_CHOICE"):
        if name not in os.environ and azd_values.get(name):
            os.environ[name] = azd_values[name]
    tenant_id = subscription_id = None
    if not os.environ.get("AZURE_OPENAI_EASTUS2_API_KEY"):
        tenant_id, subscription_id = resolve_identity(args.tenant, args.subscription, azd_values)
    try:
        return asyncio.run(run(endpoint, deployment, voice=args.voice, timeout=args.timeout,
                               skip_transcription=args.skip_transcription,
                               tenant_id=tenant_id, subscription_id=subscription_id))
    except SmokeError as exc:
        print(f"Realtime smoke check could not run: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
