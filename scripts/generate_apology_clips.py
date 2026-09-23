#!/usr/bin/env python3
"""Generate the carhop's pre-recorded "one moment" apology clips.

When the realtime model is rate-limited twice in a row, the browser plays
app/frontend/public/audio/apology-<lang>.wav (see docs/rate_limit_recovery.md).
The clip has to be local audio: the model is the thing that is rate-limited, so
it can't say sorry at that moment. This script records one clip per UI language
with the live model, once, so it can be re-run if the default voice changes:

    python scripts/generate_apology_clips.py                  # all languages, voice marin
    python scripts/generate_apology_clips.py --lang ja --voice cedar

Each clip is the realtime model reading the phrase from its response
instructions (like the smoke check's test audio), 24 kHz mono PCM16, with the
leading/trailing silence trimmed. Unless --no-verify, the clip is transcribed
with whisper-1 and kept only if the transcript matches the phrase word for word.

Endpoint, deployment, tenant and subscription are resolved like
scripts/smoke_realtime.py (flags, env, then `azd env get-values`).
"""
from __future__ import annotations

import argparse
import asyncio
import base64
import json
import sys
import time
import wave
from array import array
from pathlib import Path

import aiohttp

sys.path.insert(0, str(Path(__file__).resolve().parent))

import smoke_realtime  # noqa: E402

OUT_DIR = smoke_realtime.REPO_ROOT / "app" / "frontend" / "public" / "audio"
SAMPLE_RATE = 24_000

# One per locale in app/frontend/src/locales (and APOLOGY_LANGUAGES in lib/apology.ts).
APOLOGY_PHRASES = {
    "en": "Sorry, give me just a second.",
    "es": "Perdón, dame un segundito.",
    "fr": "Pardon, juste une petite seconde.",
    "ja": "申し訳ありません、少々お待ちください。",
}

VOICE_INSTRUCTIONS = (
    "You are the voice of a friendly Sonic Drive-In carhop. Say only what you are told to say, in the language it is "
    "written in, in a warm, upbeat, apologetic tone, at a relaxed pace."
)


def clip_path(lang: str) -> Path:
    return OUT_DIR / f"apology-{lang}.wav"


def trim_silence(pcm: bytes, threshold: int = 300, pad_ms: int = 60) -> bytes:
    """Drop near-silent PCM16 samples at both ends, keeping `pad_ms` of each."""
    samples = array("h", pcm[: len(pcm) // 2 * 2])
    loud = [i for i, s in enumerate(samples) if abs(s) >= threshold]
    if not loud:
        return bytes(pcm)
    pad = SAMPLE_RATE * pad_ms // 1000
    start, end = max(0, loud[0] - pad), min(len(samples), loud[-1] + pad + 1)
    return samples[start:end].tobytes()


def write_wav(path: Path, pcm: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(SAMPLE_RATE)
        wav.writeframes(pcm)


async def synthesize(url: str, headers: dict, text: str, voice: str, timeout: float) -> bytes:
    pcm = bytearray()
    async with aiohttp.ClientSession() as http, http.ws_connect(url, headers=headers) as ws:
        await ws.send_json({"type": "session.update", "session": {
            "type": "realtime",
            "instructions": VOICE_INSTRUCTIONS,
            "audio": {"input": {"turn_detection": None}, "output": {"voice": voice}}}})
        await ws.send_json({"type": "response.create",
                            "response": {"instructions": smoke_realtime.synthesis_instructions(text)}})
        deadline = time.monotonic() + timeout
        while (remaining := deadline - time.monotonic()) > 0:
            event = await smoke_realtime._next_event(ws, remaining)
            if event is None:
                continue
            if event["type"] == "response.output_audio.delta":
                pcm += base64.b64decode(event["delta"])
            elif event["type"] == "response.done":
                status = (event.get("response") or {}).get("status")
                if status not in (None, "completed"):
                    raise smoke_realtime.SmokeError(f"response {status}: {event['response'].get('status_details')}")
                break
            elif event["type"] == "error":
                raise smoke_realtime.SmokeError(f"could not synthesize: {event.get('error')}")
    return bytes(pcm)


async def transcribe(url: str, headers: dict, pcm: bytes, lang: str, timeout: float) -> str:
    async with aiohttp.ClientSession() as http, http.ws_connect(url, headers=headers) as ws:
        _, error = await smoke_realtime.send_session_update(ws, json.dumps({"type": "session.update", "session": {
            "type": "realtime",
            "audio": {"input": {"turn_detection": None,
                                "transcription": {"model": "whisper-1", "language": lang}}}}}), timeout)
        if error is not None:
            raise smoke_realtime.SmokeError(f"transcription session rejected: {error.get('error')}")
        for i in range(0, len(pcm), 4800):
            await ws.send_json({"type": "input_audio_buffer.append",
                                "audio": base64.b64encode(pcm[i:i + 4800]).decode()})
        await ws.send_json({"type": "input_audio_buffer.commit"})
        deadline = time.monotonic() + timeout
        while (remaining := deadline - time.monotonic()) > 0:
            event = await smoke_realtime._next_event(ws, remaining)
            kind = (event or {}).get("type")
            if kind == "conversation.item.input_audio_transcription.completed":
                return event.get("transcript") or ""
            if kind in ("conversation.item.input_audio_transcription.failed", "error"):
                raise smoke_realtime.SmokeError(f"transcription failed: {event}")
    raise smoke_realtime.SmokeError("no transcript")


async def generate(url: str, headers: dict, langs: list[str], voice: str, verify: bool,
                   attempts: int, timeout: float) -> int:
    failed = []
    for lang in langs:
        phrase = APOLOGY_PHRASES[lang]
        for attempt in range(1, attempts + 1):
            pcm = trim_silence(await synthesize(url, headers, phrase, voice, timeout))
            seconds = len(pcm) / 2 / SAMPLE_RATE
            transcript = await transcribe(url, headers, pcm, lang, timeout) if verify else None
            ok = not verify or smoke_realtime.transcript_matches(phrase, transcript)
            print(f"{lang} attempt {attempt}: {seconds:.2f}s"
                  + (f" transcript={transcript!r} {'OK' if ok else 'MISMATCH'}" if verify else ""))
            if ok and pcm:
                write_wav(clip_path(lang), pcm)
                print(f"  wrote {clip_path(lang).relative_to(smoke_realtime.REPO_ROOT)}")
                break
        else:
            failed.append(lang)
    if failed:
        print(f"FAILED: no verified clip for {', '.join(failed)} (existing files left unchanged)")
        return 1
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--endpoint", help="Azure OpenAI endpoint (default: AZURE_OPENAI_EASTUS2_ENDPOINT)")
    parser.add_argument("--deployment", help="Realtime deployment (default: AZURE_OPENAI_REALTIME_DEPLOYMENT)")
    parser.add_argument("--voice", default="marin", help="Voice (default marin, the app's default voice)")
    parser.add_argument("--lang", default=",".join(APOLOGY_PHRASES),
                        help=f"Comma-separated languages (default {','.join(APOLOGY_PHRASES)})")
    parser.add_argument("--tenant", help="Entra tenant of the Azure OpenAI resource (default: AZURE_TENANT_ID)")
    parser.add_argument("--subscription", help="Subscription whose `az` sign-in to use (default: AZURE_SUBSCRIPTION_ID)")
    parser.add_argument("--attempts", type=int, default=3, help="Tries per language before giving up (default 3)")
    parser.add_argument("--no-verify", action="store_true", help="Keep the first clip without transcribing it")
    parser.add_argument("--timeout", type=float, default=30.0, help="Seconds to wait per server reply (default 30)")
    args = parser.parse_args(argv)
    # Transcripts can be Japanese; don't crash on a cp1252 console.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(errors="backslashreplace")

    langs = [lang.strip() for lang in args.lang.split(",") if lang.strip()]
    if unknown := [lang for lang in langs if lang not in APOLOGY_PHRASES]:
        parser.error(f"unknown language(s) {unknown}; known: {list(APOLOGY_PHRASES)}")
    azd_values = smoke_realtime._azd_env_values()
    endpoint = smoke_realtime.resolve_setting("AZURE_OPENAI_EASTUS2_ENDPOINT", args.endpoint, azd_values)
    deployment = smoke_realtime.resolve_setting("AZURE_OPENAI_REALTIME_DEPLOYMENT", args.deployment, azd_values)
    if not endpoint or not deployment:
        print("Set --endpoint/--deployment or run inside an azd env.", file=sys.stderr)
        return 2
    tenant_id, subscription_id = smoke_realtime.resolve_identity(args.tenant, args.subscription, azd_values)
    try:
        headers = smoke_realtime.get_auth_headers(tenant_id, subscription_id)
        return asyncio.run(generate(smoke_realtime.realtime_url(endpoint, deployment), headers, langs, args.voice,
                                    not args.no_verify, args.attempts, args.timeout))
    except (smoke_realtime.SmokeError, aiohttp.ClientError, OSError) as exc:
        print(f"Could not generate clips: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
