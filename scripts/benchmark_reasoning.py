"""Benchmark `reasoning.effort` (and `parallel_tool_calls`) for the drive-thru.

Runs realistic guest utterances as TEXT input against a live realtime
deployment, with audio output (what the guest hears), the real Sonic system
prompt and the real tool schemas, mirroring the middle tier's tool loop: every
function_call is executed and answered, and `response.create` is re-sent after
a response that called tools, until the model answers without tools. By default
`search` answers from canned results captured from the live menu index
(--tools stub) and the order tools are the real in-memory implementations;
--tools real also queries Azure AI Search.

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
    # one effort per invocation, appended to a JSONL that survives interruption:
    python scripts/benchmark_reasoning.py --efforts low --resume %TEMP%\\bench.jsonl
    python scripts/benchmark_reasoning.py --summarize %TEMP%\\bench.jsonl

Needs AZURE_OPENAI_EASTUS2_ENDPOINT and AZURE_OPENAI_REALTIME_DEPLOYMENT (plus
AZURE_SEARCH_ENDPOINT / AZURE_SEARCH_INDEX with --tools real), from the
environment or `azd env get-values`.
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
from rtmt import ToolResult, ToolResultDirection  # noqa: E402

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
    return len(_adds(t)) == 1 and _has_add(t, "cherry limeade", size="large")


def _check_modification(t):
    return len(_adds(t)) == 1 and any(
        "cheeseburger" in a.get("item_name", "").lower() and "pickle" in a.get("item_name", "").lower()
        for a in _adds(t))


def _check_multi(t):
    return (len(_adds(t)) == 3 and _has_add(t, "corn dog") and _has_add(t, "tots", size="medium")
            and _has_add(t, "cherry limeade", size="large"))


def _check_combo(t):
    return (len(_adds(t)) == 3 and _has_add(t, "supersonic", "combo") and _has_add(t, "tots", size="medium")
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

# Canned `search` results, in the exact format tools.search returns, captured from
# the live sonic-menu-items index (2026-09-22). With --tools stub (the default)
# search answers from these instantly, so the timings measure the model, not
# Azure AI Search, and runs are repeatable. The order tools are always the real
# (local, in-memory) implementations.
STUB_MENU = [
    ("burgers___sandwiches_sonic__cheeseburger", "SONIC® Cheeseburger", "Burgers & Sandwiches", "Standard ($5.29)"),
    ("burgers___sandwiches_supersonic__double_cheeseburger", "SuperSONIC® Double Cheeseburger",
     "Burgers & Sandwiches", "Standard ($6.59)"),
    ("combos_sonic__cheeseburger_combo", "SONIC® Cheeseburger Combo", "Combos", "Standard ($8.49)"),
    ("combos_supersonic__double_cheeseburger_combo", "SuperSONIC® Double Cheeseburger Combo", "Combos",
     "Standard ($10.19)"),
    ("combos_supersonic__bacon_double_cheeseburger_combo", "SuperSONIC® Bacon Double Cheeseburger Combo", "Combos",
     "Standard ($10.99)"),
    ("hot_dogs___tots_tots", "Tots", "Hot Dogs & Tots", "Small ($2.19), Medium ($2.79), Large ($3.49)"),
    ("extras___sides_cheese_tots", "Cheese Tots", "Extras & Sides", "Small ($2.69), Medium ($3.39), Large ($3.99)"),
    ("hot_dogs___tots_chili_cheese_tots", "Chili Cheese Tots", "Hot Dogs & Tots",
     "Small ($2.99), Medium ($3.79), Large ($4.49)"),
    ("hot_dogs___tots_corn_dog", "Corn Dog", "Hot Dogs & Tots", "Standard ($1.99)"),
    ("hot_dogs___tots_chili_cheese_coney", "Chili Cheese Coney", "Hot Dogs & Tots", "Standard ($3.19)"),
    ("hot_dogs___tots_all-american_dog", "All-American Dog", "Hot Dogs & Tots", "Standard ($3.19)"),
    ("slushes___drinks_cherry_limeade", "Cherry Limeade", "Slushes & Drinks",
     "Mini ($1.59), Small ($2.49), Medium ($2.89), Large ($3.39), Route 44 ($3.79)"),
    ("slushes___drinks_cranberry_limeade", "Cranberry Limeade", "Slushes & Drinks",
     "Mini ($1.59), Small ($2.49), Medium ($2.89), Large ($3.39), Route 44 ($3.79)"),
    ("slushes___drinks_strawberry_limeade", "Strawberry Limeade", "Slushes & Drinks",
     "Mini ($1.59), Small ($2.49), Medium ($2.89), Large ($3.39), Route 44 ($3.79)"),
    ("slushes___drinks_cherry_slush", "Cherry Slush", "Slushes & Drinks",
     "Mini ($1.39), Small ($2.19), Medium ($2.79), Large ($3.39), Route 44 ($3.79)"),
    ("slushes___drinks_grape_slush", "Grape Slush", "Slushes & Drinks",
     "Mini ($1.39), Small ($2.19), Medium ($2.79), Large ($3.39), Route 44 ($3.79)"),
    ("slushes___drinks_strawberry_slush", "Strawberry Slush", "Slushes & Drinks",
     "Mini ($1.89), Small ($2.69), Medium ($3.29), Large ($3.89), Route 44 ($4.29)"),
    ("slushes___drinks_blue_raspberry_slush", "Blue Raspberry Slush", "Slushes & Drinks",
     "Mini ($1.39), Small ($2.19), Medium ($2.79), Large ($3.39), Route 44 ($3.79)"),
]
_STUB_STOPWORDS = {"a", "an", "the", "of", "with", "and", "no", "please", "can", "i", "get", "small", "medium",
                   "large", "mini", "route", "44", "size", "what", "do", "you", "have", "menu", "item", "items"}


def _stub_words(text: str) -> set[str]:
    words = set()
    for raw in text.lower().replace("®", "").replace("-", " ").split():
        word = raw.strip(".,!?'\"()")
        word = word[:-1] if word.endswith("s") and len(word) > 4 else word  # slushes -> slushe; flavors -> flavor
        if word and word not in _STUB_STOPWORDS:
            words.add(word)
    return words


async def stub_search(args) -> ToolResult:
    query = _stub_words(str((args or {}).get("query", "")))
    scored = []
    for rid, name, category, sizes in STUB_MENU:
        haystack = _stub_words(f"{name} {category}")
        name_words = _stub_words(name)
        hits = len(query & haystack)
        if hits:
            # Prefer names fully covered by the query, then the tightest name.
            scored.append((-(hits + (name_words <= query)), len(name_words), rid, name, category, sizes))
    scored.sort()
    limit = 6 if query & {"slush", "slushe", "flavor"} else 3
    records = [f"[{rid}]: Item: {name}, Category: {category}, Available Sizes: {sizes}"
               for *_, rid, name, category, sizes in scored[:limit]]
    return ToolResult("\n-----\n".join(records) or "No matching menu entries found.", ToolResultDirection.TO_SERVER)


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
    """Guest already has a Medium Cherry Limeade on the order: the real tools ran,
    and the conversation holds the same items the live app's history would --
    the guest's request, the search + update_order calls with their outputs, and
    the carhop's confirmation."""
    import re
    search_args = {"query": "Cherry Limeade"}
    search = (await rtmt.tools["search"].target(search_args)).to_text()
    m = re.search(r"Medium \(\$([0-9.]+)\)", search)
    add_args = {"action": "add", "item_name": "Cherry Limeade", "size": "Medium", "quantity": 1,
                "price": float(m.group(1)) if m else 2.89}
    added = (await rtmt.tools["update_order"].target(add_args, session_id)).to_text()
    items = [
        {"type": "message", "role": "user", "content": [{"type": "input_text", "text": "Can I get a medium Cherry Limeade?"}]},
        {"type": "function_call", "call_id": "seed_search", "name": "search", "arguments": json.dumps(search_args)},
        {"type": "function_call_output", "call_id": "seed_search", "output": search},
        {"type": "function_call", "call_id": "seed_add", "name": "update_order", "arguments": json.dumps(add_args)},
        {"type": "function_call_output", "call_id": "seed_add", "output": added},
        {"type": "message", "role": "assistant",
         "content": [{"type": "output_text", "text": "You got it, one Medium Cherry Limeade. Anything else?"}]},
    ]
    for item in items:
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


def _p90(values):
    values = sorted(v for v in values if v is not None)
    if not values:
        return None
    return values[min(len(values) - 1, int(round(0.9 * (len(values) - 1))))]


def _med_p90(values):
    p90 = _p90(values)
    return f"{_fmt(values).strip()} / {'-' if p90 is None else f'{p90:.2f}s'}"


def summarize(trials: list[Trial]) -> str:
    lines = []
    keys = []
    for t in trials:
        k = (t.effort, t.parallel_tool_calls)
        if k not in keys:
            keys.append(k)
    header = ("| effort | parallel_tool_calls | trials | correct | TTFA median / p90 | first tool call median / p90 "
              "| total median / p90 | avg reasoning tok | errors |")
    lines += [header, "|" + "---|" * 9]
    for effort, ptc in keys:
        ts = [t for t in trials if (t.effort, t.parallel_tool_calls) == (effort, ptc)]
        lines.append(
            f"| {effort} | {'default' if ptc is None else ptc} | {len(ts)} | "
            f"{sum(t.correct for t in ts)}/{len(ts)} | {_med_p90([t.ttfa for t in ts])} | "
            f"{_med_p90([t.ttfc for t in ts])} | {_med_p90([t.total for t in ts])} | "
            f"{statistics.mean(t.reasoning_tokens for t in ts):.0f} | "
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


def _load_trials(path: str) -> list[Trial]:
    """Trials from a --resume JSONL file (one finished trial per line)."""
    trials = []
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        if line.strip():
            trials.append(Trial(**json.loads(line)))
    return trials


async def main_async(args) -> int:
    if args.summarize:
        trials = _load_trials(args.summarize)
        print(summarize(trials))
        return 0
    azd_values = _azd_env_values()
    endpoint = resolve_setting("AZURE_OPENAI_EASTUS2_ENDPOINT", args.endpoint, azd_values)
    deployment = resolve_setting("AZURE_OPENAI_REALTIME_DEPLOYMENT", args.deployment, azd_values)
    if not all((endpoint, deployment)):
        print("Need AZURE_OPENAI_EASTUS2_ENDPOINT and AZURE_OPENAI_REALTIME_DEPLOYMENT", file=sys.stderr)
        return 2

    import tools
    rtmt = build_middle_tier(endpoint, deployment, voice=args.voice)
    if args.tools == "stub":
        tools._prompt_loader = PromptLoader()
        rtmt.tools["search"].target = stub_search
        rtmt.tools["update_order"].target = tools.update_order
        rtmt.tools["get_order"].target = tools.get_order
        rtmt.tools["reset_order"].target = tools.reset_order
    else:
        search_endpoint = resolve_setting("AZURE_SEARCH_ENDPOINT", None, azd_values)
        search_index = resolve_setting("AZURE_SEARCH_INDEX", None, azd_values)
        if not all((search_endpoint, search_index)):
            print("--tools real needs AZURE_SEARCH_ENDPOINT and AZURE_SEARCH_INDEX", file=sys.stderr)
            return 2
        from azure.identity import DefaultAzureCredential
        rtmt.tools.clear()
        semantic = (resolve_setting("AZURE_SEARCH_SEMANTIC_RANKER", None, azd_values) or "standard").lower()
        tools.attach_tools_rtmt(
            rtmt, credentials=DefaultAzureCredential(exclude_interactive_browser_credential=True),
            search_endpoint=search_endpoint, search_index=search_index,
            semantic_configuration=resolve_setting("AZURE_SEARCH_SEMANTIC_CONFIGURATION", None, azd_values)
            or "menuSemanticConfig",
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
        for trial in _load_trials(args.resume):
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
    p.add_argument("--tools", choices=("stub", "real"), default="stub",
                   help="stub (default): canned search results + the real in-memory order tools, so timings "
                        "measure the model; real: live Azure AI Search too")
    p.add_argument("--summarize", metavar="JSONL", help="Only print the summary tables for a --resume file")
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
