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

Usage (from the repo root, with `az login` / `azd auth login` done):

    python scripts/smoke_realtime.py                       # uses azd env / env vars
    python scripts/smoke_realtime.py --deployment gpt-realtime-1.5
    python scripts/smoke_realtime.py --endpoint https://<aoai>.openai.azure.com/ --deployment gpt-realtime-2.1

Endpoint/deployment default to AZURE_OPENAI_EASTUS2_ENDPOINT /
AZURE_OPENAI_REALTIME_DEPLOYMENT, read from the environment or `azd env
get-values`. Auth: AZURE_OPENAI_EASTUS2_API_KEY if set, else Entra ID via
DefaultAzureCredential (Azure CLI / azd login; needs "Cognitive Services OpenAI
User" on the resource, which `azd up` grants the deploying principal).

Exit codes: 0 = all checks passed, 1 = a check failed, 2 = could not run
(missing endpoint/deployment, auth or network failure).
"""
from __future__ import annotations

import argparse
import asyncio
import base64
import copy
import json
import os
import subprocess
import sys
import time
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


def get_auth_headers() -> dict[str, str]:
    if key := os.environ.get("AZURE_OPENAI_EASTUS2_API_KEY"):
        return {"api-key": key}
    try:
        from azure.identity import DefaultAzureCredential
        token = DefaultAzureCredential(exclude_interactive_browser_credential=True).get_token(
            "https://cognitiveservices.azure.com/.default").token
    except Exception as exc:  # noqa: BLE001 - any credential failure means "cannot run"
        raise SmokeError(f"could not get an Entra ID token for Azure OpenAI: {exc}") from exc
    return {"Authorization": f"Bearer {token}"}


def realtime_url(endpoint: str, deployment: str) -> str:
    host = endpoint.split("://", 1)[-1].rstrip("/")
    return f"wss://{host}/openai/v1/realtime?model={deployment}"


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


async def _synthesize(url: str, headers: dict, text: str, timeout: float) -> bytes:
    pcm = bytearray()
    async with aiohttp.ClientSession() as http, http.ws_connect(url, headers=headers) as ws:
        await ws.send_json({"type": "session.update", "session": {
            "type": "realtime",
            "instructions": ("You are a text-to-speech engine, not an assistant. Speak the user's message aloud "
                             "word for word, exactly as written, and say nothing else. Never answer or react to it."),
            "audio": {"input": {"turn_detection": None}, "output": {"voice": "alloy"}}}})
        await ws.send_json({"type": "conversation.item.create", "item": {
            "type": "message", "role": "user", "content": [{"type": "input_text", "text": text}]}})
        await ws.send_json({"type": "response.create"})
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
                # The test audio is model-generated; a model that paraphrases instead of
                # reading verbatim still proves transcription works, so only note it.
                note = "" if "limeade" in transcript.lower() else " (test audio was paraphrased by the TTS step)"
                return [], [f"PASS  transcription ({model}): {transcript!r}{note}"]
            if kind == "conversation.item.input_audio_transcription.failed":
                err = event.get("error") or {}
                return [f"transcription ({model}): FAILED code={err.get('code')} message={err.get('message')} "
                        "-- guest speech will not be transcribed (does this model need its own deployment?)"], []
            if kind == "error":
                return [f"transcription ({model}): error {event.get('error')}"], []
    return [f"transcription ({model}): no transcription event within {timeout:.0f}s"], []


async def run(endpoint: str, deployment: str, *, voice: str | None, timeout: float,
              skip_transcription: bool) -> int:
    rtmt = build_middle_tier(endpoint, deployment, voice)
    url = realtime_url(endpoint, deployment)
    print(f"Realtime smoke check: deployment={deployment} voice={rtmt.voice_choice} "
          f"transcription={rtmt.transcription_model} "
          f"reasoning={rtmt.reasoning_effort if rtmt.reasoning_enabled() else 'off'}")
    headers = get_auth_headers()
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


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--endpoint", help="Azure OpenAI endpoint (default: AZURE_OPENAI_EASTUS2_ENDPOINT)")
    parser.add_argument("--deployment", help="Realtime deployment (default: AZURE_OPENAI_REALTIME_DEPLOYMENT)")
    parser.add_argument("--voice", help="Voice to send (default: AZURE_OPENAI_REALTIME_VOICE_CHOICE or config.yaml)")
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
    for name in ("AZURE_OPENAI_REALTIME_REASONING_EFFORT", "AZURE_OPENAI_REALTIME_TRANSCRIPTION_MODEL",
                 "AZURE_OPENAI_REALTIME_VOICE_CHOICE"):
        if name not in os.environ and azd_values.get(name):
            os.environ[name] = azd_values[name]
    try:
        return asyncio.run(run(endpoint, deployment, voice=args.voice, timeout=args.timeout,
                               skip_transcription=args.skip_transcription))
    except SmokeError as exc:
        print(f"Realtime smoke check could not run: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
