"""Benchmark `reasoning.effort` (and `parallel_tool_calls`) for the drive-thru.

Runs realistic guest utterances as TEXT input against a live realtime
deployment, with audio output (what the guest hears), the real Sonic system
prompt, the real tool schemas and the REAL tool implementations (Azure AI
Search + order state), mirroring the middle tier's tool loop: every
function_call is executed and answered, and `response.create` is re-sent after
a response that called tools, until the model answers without tools.

Per trial it records:
  * ttfa   -- response.create -> first response.output_audio.delta (guest hears something)
  * ttfc   -- response.create -> first function_call output item
  * total  -- response.create -> final response.done of the turn
  * correctness -- right tools with the right items (scenario-specific check)

Usage (repo root, `az login` done, azd env selected or env vars set):

    python scripts/benchmark_reasoning.py                        # default grid, 3 reps
    python scripts/benchmark_reasoning.py --efforts low,medium --reps 5
    python scripts/benchmark_reasoning.py --parallel-only --effort low
    python scripts/benchmark_reasoning.py --out %TEMP%\\bench.json  # raw per-trial JSON

Needs AZURE_OPENAI_EASTUS2_ENDPOINT, AZURE_OPENAI_REALTIME_DEPLOYMENT,
AZURE_SEARCH_ENDPOINT, AZURE_SEARCH_INDEX (environment or `azd env get-values`).
Raw results contain no credentials; still, write them outside the repo.
"""
from __future__ import annotations

import argparse
import asyncio
import json
import os
import statistics
import sys
import time
from dataclasses import asdict, dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import aiohttp  # noqa: E402
from smoke_realtime import (  # noqa: E402
    SmokeError,
    _azd_env_values,
    build_middle_tier,
    get_auth_headers,
    realtime_url,
    resolve_setting,
)

from order_state import order_state_singleton  # noqa: E402
from prompt_loader import PromptLoader  # noqa: E402

DEFAULT_EFFORTS = ["default", "none", "minimal", "low", "medium", "high"]
MAX_ROUNDS = 10
TURN_TIMEOUT_SEC = 90.0


def _calls(trial, name):
    return [c for c in trial.tool_calls if c["name"] == name]


def _adds(trial):
    return [c["args"] for c in _calls(trial, "update_order") if c["args"].get("action") == "add"]


def _has_add(trial, *words, size=None):
    for args in _adds(trial):
        name = args.get("item_name", "").lower()
        if all(w in name for w in words) and (size is None or size in str(args.get("size", "")).lower()):
            return True
    return False


def _check_single(t):
    return _has_add(t, "cherry limeade", size="large")


def _check_modification(t):
    return any("cheeseburger" in a.get("item_name", "").lower() and "pickle" in a.get("item_name", "").lower()
               for a in _adds(t))


def _check_multi(t):
    return (_has_add(t, "corn dog") and _has_add(t, "tots", size="medium")
            and _has_add(t, "cherry limeade", size="large"))


def _check_combo(t):
    return (_has_add(t, "supersonic", "combo") and _has_add(t, "tots", size="medium")
            and _has_add(t, "cherry limeade", size="large"))


def _check_size_change(t):
    items = [(i.item.lower(), (i.size or "").lower(), i.quantity) for i in t.final_order]
    limeades = [i for i in items if "cherry limeade" in i[0]]
    return len(limeades) == 1 and "large" in limeades[0][1] and limeades[0][2] == 1


def _check_question(t):
    return bool(_calls(t, "search")) and not _adds(t) and bool(t.transcript)


SCENARIOS = {
    "single": ("Can I get a large Cherry Limeade?", _check_single, None),
    "modification": ("I'd like a SONIC Cheeseburger with no pickles.", _check_modification, None),
    "multi": ("Can I get a corn dog, a medium tots, and a large Cherry Limeade?", _check_multi, None),
    "combo": ("I'll take a SuperSONIC Double Cheeseburger combo with medium tots and a large Cherry Limeade.",
              _check_combo, None),
    "size_change": ("Actually, make that Cherry Limeade a large.", _check_size_change, "seed_medium_limeade"),
    "question": ("What slush flavors do you have?", _check_question, None),
}


@dataclass
class Trial:
    effort: str
    parallel_tool_calls: bool | None
    scenario: str
    rep: int
    ttfa: float | None = None
    ttfc: float | None = None
    total: float | None = None
    rounds: int = 0
    tool_calls: list = field(default_factory=list)
    transcript: str = ""
    reasoning_tokens: int = 0
    output_tokens: int = 0
    total_tokens: int = 0
    correct: bool = False
    error: str | None = None
    final_order: list = field(default_factory=list, repr=False)


async def _seed_medium_limeade(ws, rtmt, session_id):
    """Guest already has a Medium Cherry Limeade on the order (via the real tools)."""
    search = await rtmt.tools["search"].target({"query": "Cherry Limeade"})
    text = search.to_text()
    price = 2.99
    try:
        import re
        m = re.search(r'"size"\s*:\s*"Medium"\s*,\s*"price"\s*:\s*([0-9.]+)', text)
        price = float(m.group(1)) if m else price
    except Exception:  # noqa: BLE001
        pass
    await rtmt.tools["update_order"].target(
        {"action": "add", "item_name": "Cherry Limeade", "size": "Medium", "quantity": 1, "price": price}, session_id)
    for item in (
        {"type": "message", "role": "user", "content": [{"type": "input_text", "text": "Can I get a medium Cherry Limeade?"}]},
        {"type": "message", "role": "assistant",
         "content": [{"type": "output_text", "text": "You got it, one Medium Cherry Limeade. Anything else?"}]},
    ):
        await ws.send_json({"type": "conversation.item.create", "item": item})


async def run_trial(rtmt, url, headers, trial: Trial) -> Trial:
    text, check, seed = SCENARIOS[trial.scenario]
    session_id = order_state_singleton.create_session()
    rtmt.reasoning_effort = None if trial.effort == "default" else trial.effort
    rtmt.parallel_tool_calls = trial.parallel_tool_calls
    try:
        async with aiohttp.ClientSession() as http, http.ws_connect(url, headers=headers) as ws:
            await ws.send_str(rtmt.build_bootstrap_session_update())
            while True:
                ev = json.loads((await asyncio.wait_for(ws.receive(), 20)).data)
                if ev["type"] == "session.updated":
                    break
                if ev["type"] == "error":
                    raise RuntimeError(f"session.update rejected: {ev.get('error')}")
            if seed == "seed_medium_limeade":
                await _seed_medium_limeade(ws, rtmt, session_id)
            await ws.send_json({"type": "conversation.item.create", "item": {
                "type": "message", "role": "user", "content": [{"type": "input_text", "text": text}]}})
            t0 = time.perf_counter()
            await ws.send_json({"type": "response.create", "response": {"output_modalities": ["audio"]}})
            trial.rounds = 1
            pending_tools = False
            deadline = t0 + TURN_TIMEOUT_SEC
            while True:
                remaining = deadline - time.perf_counter()
                if remaining <= 0:
                    raise TimeoutError("turn timed out")
                msg = await asyncio.wait_for(ws.receive(), remaining)
                if msg.type != aiohttp.WSMsgType.TEXT:
                    raise RuntimeError(f"socket closed: {msg.type.name}")
                ev = json.loads(msg.data)
                kind = ev["type"]
                now = time.perf_counter() - t0
                if kind == "response.output_audio.delta" and trial.ttfa is None:
                    trial.ttfa = now
                elif kind == "response.output_item.added" and ev["item"].get("type") == "function_call":
                    if trial.ttfc is None:
                        trial.ttfc = now
                elif kind == "response.output_item.done" and ev["item"].get("type") == "function_call":
                    item = ev["item"]
                    args = json.loads(item.get("arguments") or "{}")
                    trial.tool_calls.append({"name": item["name"], "args": args, "t": round(now, 3)})
                    tool = rtmt.tools[item["name"]]
                    if item["name"] in ("update_order", "get_order", "reset_order"):
                        result = await tool.target(args, session_id)
                    else:
                        result = await tool.target(args)
                    await ws.send_json({"type": "conversation.item.create", "item": {
                        "type": "function_call_output", "call_id": item["call_id"], "output": result.to_text()}})
                    pending_tools = True
                elif kind == "response.output_audio_transcript.done":
                    trial.transcript += ev.get("transcript", "")
                elif kind == "response.done":
                    usage = (ev.get("response") or {}).get("usage") or {}
                    trial.output_tokens += usage.get("output_tokens", 0) or 0
                    trial.total_tokens += usage.get("total_tokens", 0) or 0
                    details = usage.get("output_token_details") or {}
                    trial.reasoning_tokens += details.get("reasoning_tokens", 0) or 0
                    status = (ev.get("response") or {}).get("status")
                    if status == "failed":
                        details = (ev.get("response") or {}).get("status_details") or {}
                        if (details.get("error") or {}).get("code") == "inference_rate_limit_exceeded":
                            raise RateLimitedError("deployment TPM limit hit")
                        raise RuntimeError(f"response failed: {(ev.get('response') or {}).get('status_details')}")
                    if pending_tools and trial.rounds < MAX_ROUNDS:
                        pending_tools = False
                        trial.rounds += 1
                        await ws.send_json({"type": "response.create"})
                    else:
                        trial.total = now
                        break
                elif kind == "error":
                    raise RuntimeError(f"error: {ev.get('error')}")
    except Exception as exc:  # noqa: BLE001 - record and continue the grid
        trial.error = f"{type(exc).__name__}: {exc}"[:300]
    trial.final_order = list(order_state_singleton.get_order_items(session_id))
    trial.correct = trial.error is None and check(trial)
    order_state_singleton.delete_session(session_id)
    return trial


TRANSPORT_RETRIES = 2
RATE_LIMIT_RETRIES = 4


class RateLimitedError(RuntimeError):
    """The deployment's tokens-per-minute limit, not the model: back off and retry."""


class TokenPacer:
    """Keep this script under a share of the deployment's TPM limit.

    Every tool round re-reads the full system prompt + tool schemas, so one
    combo turn can cost 30k+ tokens. The deployment is shared with the live
    demo; unpaced, the grid trips inference_rate_limit_exceeded within minutes
    and the throttled trials measure nothing.
    """

    def __init__(self, budget_per_minute: int):
        self.budget = budget_per_minute
        self.spent: list[tuple[float, int]] = []
        self.estimates: dict[str, list[int]] = {}

    def estimate(self, scenario: str) -> int:
        seen = self.estimates.get(scenario)
        return int(statistics.median(seen)) if seen else 20000

    async def wait(self, scenario: str) -> None:
        if self.budget <= 0:
            return
        need = min(self.estimate(scenario), self.budget)
        while True:
            now = time.monotonic()
            self.spent = [(t, n) for t, n in self.spent if now - t < 60]
            if sum(n for _, n in self.spent) + need <= self.budget:
                return
            await asyncio.sleep(max(1.0, 60 - (now - self.spent[0][0])))

    def record(self, scenario: str, tokens: int) -> None:
        self.spent.append((time.monotonic(), tokens))
        if tokens:
            self.estimates.setdefault(scenario, []).append(tokens)


def _is_transport_error(trial: Trial) -> bool:
    """Network blips (DNS, TLS, dropped socket) say nothing about the model; retry them."""
    return bool(trial.error) and trial.error.split(":", 1)[0] in (
        "ClientConnectorError", "ClientOSError", "ServerDisconnectedError", "WSServerHandshakeError",
        "ClientConnectionResetError")


def _fmt(values):
    values = [v for v in values if v is not None]
    if not values:
        return "   -  "
    return f"{statistics.median(values):5.2f}s"


def summarize(trials: list[Trial]) -> str:
    lines = []
    keys = []
    for t in trials:
        k = (t.effort, t.parallel_tool_calls)
        if k not in keys:
            keys.append(k)
    header = ("| effort | parallel_tool_calls | trials | correct | median TTFA | median first tool call "
              "| median total | p90 total | avg reasoning tok | errors |")
    lines += [header, "|" + "---|" * 10]
    for effort, ptc in keys:
        ts = [t for t in trials if (t.effort, t.parallel_tool_calls) == (effort, ptc)]
        totals = sorted(t.total for t in ts if t.total is not None)
        p90 = totals[min(len(totals) - 1, int(round(0.9 * (len(totals) - 1))))] if totals else None
        lines.append(
            f"| {effort} | {'default' if ptc is None else ptc} | {len(ts)} | "
            f"{sum(t.correct for t in ts)}/{len(ts)} | {_fmt([t.ttfa for t in ts])} | {_fmt([t.ttfc for t in ts])} | "
            f"{_fmt(totals)} | {_fmt([p90])} | {statistics.mean(t.reasoning_tokens for t in ts):.0f} | "
            f"{sum(t.error is not None for t in ts)} |")
    lines += ["", "Per scenario (correct / median TTFA / median total):", ""]
    scen_header = "| effort | ptc | " + " | ".join(SCENARIOS) + " |"
    lines += [scen_header, "|" + "---|" * (2 + len(SCENARIOS))]
    for effort, ptc in keys:
        cells = []
        for scen in SCENARIOS:
            ts = [t for t in trials if (t.effort, t.parallel_tool_calls, t.scenario) == (effort, ptc, scen)]
            if not ts:
                cells.append("-")
                continue
            cells.append(f"{sum(t.correct for t in ts)}/{len(ts)} · {_fmt([t.ttfa for t in ts]).strip()} · "
                         f"{_fmt([t.total for t in ts]).strip()}")
        lines.append(f"| {effort} | {'default' if ptc is None else ptc} | " + " | ".join(cells) + " |")
    return "\n".join(lines)


def _trial_dict(trial: Trial) -> dict:
    d = asdict(trial)
    d["final_order"] = [o if isinstance(o, str) else f"{o.quantity}x {o.size} {o.item}" for o in trial.final_order]
    return d


async def main_async(args) -> int:
    azd_values = _azd_env_values()
    endpoint = resolve_setting("AZURE_OPENAI_EASTUS2_ENDPOINT", args.endpoint, azd_values)
    deployment = resolve_setting("AZURE_OPENAI_REALTIME_DEPLOYMENT", args.deployment, azd_values)
    search_endpoint = resolve_setting("AZURE_SEARCH_ENDPOINT", None, azd_values)
    search_index = resolve_setting("AZURE_SEARCH_INDEX", None, azd_values)
    if not all((endpoint, deployment, search_endpoint, search_index)):
        print("Need AZURE_OPENAI_EASTUS2_ENDPOINT, AZURE_OPENAI_REALTIME_DEPLOYMENT, AZURE_SEARCH_ENDPOINT, "
              "AZURE_SEARCH_INDEX", file=sys.stderr)
        return 2

    from azure.identity import DefaultAzureCredential

    from tools import attach_tools_rtmt
    rtmt = build_middle_tier(endpoint, deployment, voice=args.voice)
    rtmt.tools.clear()
    semantic = (resolve_setting("AZURE_SEARCH_SEMANTIC_RANKER", None, azd_values) or "standard").lower()
    attach_tools_rtmt(
        rtmt, credentials=DefaultAzureCredential(exclude_interactive_browser_credential=True),
        search_endpoint=search_endpoint, search_index=search_index,
        semantic_configuration=resolve_setting("AZURE_SEARCH_SEMANTIC_CONFIGURATION", None, azd_values) or "menuSemanticConfig",
        identifier_field="id", content_field="description", embedding_field="embedding", title_field="name",
        use_vector_query=True, prompt_loader=PromptLoader(), use_semantic_ranker=semantic != "disabled")
    headers = get_auth_headers()
    url = realtime_url(endpoint, deployment)

    grid: list[tuple[str, bool | None, str]] = []
    if not args.parallel_only:
        grid += [(e, None, s) for s in args.scenarios for e in args.efforts]
    if args.parallel_only or args.parallel:
        grid += [(args.effort, p, "multi") for p in (True, False)]
        grid += [(args.effort, p, "combo") for p in (True, False)]

    trials: list[Trial] = []
    done: set[tuple] = set()
    if args.resume and Path(args.resume).exists():
        for line in Path(args.resume).read_text(encoding="utf-8").splitlines():
            if line.strip():
                trial = Trial(**json.loads(line))
                trials.append(trial)
                done.add((trial.effort, trial.parallel_tool_calls, trial.scenario, trial.rep))
    pacer = TokenPacer(args.tpm_budget)
    total = len(grid) * args.reps
    print(f"Benchmark: deployment={deployment} voice={rtmt.voice_choice} trials={total} "
          f"(already done: {len(done)})", flush=True)
    for rep in range(args.reps):  # interleave so drift in service latency hits every level equally
        for effort, ptc, scen in grid:
            if (effort, ptc, scen, rep) in done:
                continue
            for attempt in range(RATE_LIMIT_RETRIES + 1):
                await pacer.wait(scen)
                trial = await run_trial(rtmt, url, headers, Trial(effort, ptc, scen, rep))
                pacer.record(scen, trial.total_tokens)
                limited = bool(trial.error) and trial.error.startswith("RateLimitedError")
                if not (limited or (_is_transport_error(trial) and attempt < TRANSPORT_RETRIES)):
                    break
                if attempt == RATE_LIMIT_RETRIES:
                    break
                backoff = 20 * (attempt + 1) if limited else 2
                print(f"      {trial.error} -- retrying in {backoff}s", flush=True)
                await asyncio.sleep(backoff)
            trials.append(trial)
            if args.resume:
                with open(args.resume, "a", encoding="utf-8") as fh:
                    fh.write(json.dumps(_trial_dict(trial)) + "\n")
            names = ",".join(c["name"] for c in trial.tool_calls)
            print(f"[{len(trials):3d}/{total}] {effort:8s} ptc={str(ptc):5s} {scen:12s} "
                  f"ttfa={trial.ttfa or 0:5.2f} ttfc={trial.ttfc or 0:5.2f} total={trial.total or 0:5.2f} "
                  f"tok={trial.total_tokens:6d} ok={trial.correct!s:5s} tools=[{names}] {trial.error or ''}", flush=True)
            await asyncio.sleep(args.pause)

    print()
    print(summarize(trials))
    if args.out:
        raw = [_trial_dict(t) for t in trials]
        Path(args.out).write_text(json.dumps(raw, indent=2), encoding="utf-8")
        print(f"\nRaw results written to {args.out}")
    return 0


def main() -> int:
    p = argparse.ArgumentParser(description="Benchmark reasoning.effort on a live realtime deployment.")
    p.add_argument("--endpoint")
    p.add_argument("--deployment")
    p.add_argument("--voice")
    p.add_argument("--efforts", default=",".join(DEFAULT_EFFORTS),
                   type=lambda s: [x.strip() for x in s.split(",") if x.strip()],
                   help="Comma-separated effort levels; 'default' omits the reasoning field")
    p.add_argument("--scenarios", default=",".join(SCENARIOS),
                   type=lambda s: [x.strip() for x in s.split(",") if x.strip()])
    p.add_argument("--reps", type=int, default=3)
    p.add_argument("--parallel", action="store_true", help="Also compare parallel_tool_calls true/false")
    p.add_argument("--parallel-only", action="store_true", help="Only the parallel_tool_calls comparison")
    p.add_argument("--effort", default="low", help="Effort used for the parallel_tool_calls comparison")
    p.add_argument("--pause", type=float, default=0.5, help="Seconds between trials")
    p.add_argument("--tpm-budget", type=int, default=60000,
                   help="Max tokens/minute this script may use (0 = unpaced). Keep well under the "
                        "deployment's TPM limit -- it is shared with the live app.")
    p.add_argument("--out", help="Write raw per-trial JSON here (keep it out of the repo)")
    p.add_argument("--resume", help="JSONL file: append each finished trial, and skip trials already in it "
                                    "(lets a long grid survive an interrupted run)")
    args = p.parse_args()
    unknown = [s for s in args.scenarios if s not in SCENARIOS]
    if unknown:
        p.error(f"unknown scenarios {unknown}; choose from {list(SCENARIOS)}")
    os.environ.setdefault("PYTHONIOENCODING", "utf-8")
    try:
        return asyncio.run(main_async(args))
    except SmokeError as exc:
        print(f"Benchmark could not run: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
