# Conformance suite

Black-box, language-neutral conformance harness for the Sonic AI Drive-Thru realtime backend
(issue [#7](https://github.com/swigerb/SonicAIDriveThru/issues/7),
CI: [#11](https://github.com/swigerb/SonicAIDriveThru/issues/11)). Talks to a backend only over
HTTP and WebSocket; never imports backend source.

> This file documents the GA realtime protocol validation fidelity work (PR #22 review item 9)
> and the neutral `BackendContract` (PR #22 review item 13).

## Choosing a backend: `CONFORMANCE_BACKEND` / `CONFORMANCE_BACKEND_URL`

`BackendLauncherFactory` (`src/Conformance.Harness/BackendLauncherFactory.cs`) picks the backend
under test:

- `CONFORMANCE_BACKEND_URL=<uri>` — talk to an already-running backend at a fixed URL; the harness
  starts and cleans up nothing (external mode). External mode additionally requires
  `CONFORMANCE_FAKE_REALTIME_PORT` and `CONFORMANCE_FAKE_SEARCH_PORT` (both a valid TCP port
  1-65535) — the two Kestrel fakes normally bind to `http://127.0.0.1:0` and let the OS pick a
  free port each run, which an already-running external backend has no way to discover after the
  fact. Missing or invalid ports fail fast with a clear message (`ExternalModePortPolicy`, PR #22
  review item 16) before either fake even starts, instead of silently running against ports the
  external backend can never match. Start the external backend pointed at those same two fixed
  ports, then run the suite with the identical env vars set.
- In external mode, only the **Default** backend profile collection actually runs against the
  external backend. The `ShortTimers` and `FixedClock` profile collections (see "Backend
  profiles" below) skip themselves instead, with a clear reason (`ExternalModeProfilePolicy`, PR
  #22 review item N6): external mode has exactly one already-running backend process, which
  cannot simultaneously satisfy the Default profile's requirements and a different profile's
  `CONFORMANCE_TEST_HOOKS` overrides, and every profile collection would otherwise try to bind
  the same fixed fake ports concurrently and race for them. Run non-Default profile scenarios
  with `CONFORMANCE_BACKEND=python` (harness-launched) instead.
- `CONFORMANCE_BACKEND=python` (the default) — launch `app/backend` via `.venv`.
- `CONFORMANCE_BACKEND=dotnet` — the S2 .NET backend placeholder (issue #7; the backend doesn't
  exist yet). **This FAILS the suite by default** (PR #22 review item 15) — CI must never silently
  skip real backend coverage just because the S2 backend isn't built yet. Set
  `CONFORMANCE_ALLOW_SKIP=1` on your own machine to turn that failure into a skip instead; this
  opt-in is always ignored when `GITHUB_ACTIONS=true` or `CI=true` is set (see
  `CiEnvironment.IsCi` / `DotnetPlaceholderPolicy`), so there is no way to make CI skip it.

## BackendContract — the neutral contract every backend under test must satisfy

`Conformance.Harness.BackendContract` (`src/Conformance.Harness/BackendContract.cs`) is the
language-agnostic set of facts *any* backend implementation needs to run against the fakes —
Python today, the future .NET backend once S2 exists (issue #7). Per-launcher classes (today just
`PythonBackendOptions` / `PythonBackendLauncher`) layer their own language-specific extras (env
var names, process-start mechanics) on top of this same contract, so a future .NET launcher can
reuse the identical `BackendContract` values without duplicating the "what does a conforming
backend need" knowledge.

### Every environment variable the harness sets on the Python backend process

All of these are set explicitly by `BackendEnvironment.Build` — the backend is launched with
`RUNNING_IN_PRODUCTION=true` specifically so it never calls `load_dotenv()` and never picks up a
developer's local `.env`; every value below is authoritative for the launched process.

| Variable | Value | Source / why |
|---|---|---|
| `HOST` | `127.0.0.1` | `BackendContract.Host` — loopback only, never reachable off-box. |
| `PORT` | a free TCP port picked per test run | `NetworkUtils.GetFreeTcpPort()`. |
| `AZURE_OPENAI_EASTUS2_API_KEY` | `conformance-test-openai-key` | `BackendContract.OpenAiApiKey` — fixed key-auth value; `FakeRealtimeUpstreamServer.ExpectedApiKey` is set to the exact same constant. |
| `AZURE_SEARCH_API_KEY` | `conformance-test-search-key` | `BackendContract.SearchApiKey`, analogous to the OpenAI key above. |
| `AZURE_OPENAI_EASTUS2_ENDPOINT` | `FakeRealtimeUpstreamServer.BaseUri` | Points the backend's realtime client straight at the fake instead of the real Azure OpenAI GA service. |
| `AZURE_OPENAI_REALTIME_DEPLOYMENT` | `gpt-realtime-2.1-conformance` (default; overridable per contract) | Echoed back on the upstream `?model=` query string; also drives the `reasoning`-rejection-on-`1.5`-style-deployment-name validation rule. |
| `AZURE_OPENAI_REALTIME_VOICE_CHOICE` | `marin` | Sent in the bootstrap `session.update`. |
| `AZURE_SEARCH_ENDPOINT` | `FakeSearchServer.BaseUri` | Points the backend's `azure-search-documents` client at the fake instead of the real Azure AI Search service. |
| `AZURE_SEARCH_INDEX` | `menu-index` | Must match what the backend's search client sends as the index name in its REST path. |
| `AZURE_SEARCH_SEMANTIC_CONFIGURATION` | `menuSemanticConfig` | Echoed in the search request body's semantic query options; `FakeSearch` accepts and ignores the value (any well-formed request is answered). |
| `AZURE_SEARCH_IDENTIFIER_FIELD` | `id` | Field-shape config the backend's search client applies when building the request. |
| `AZURE_SEARCH_CONTENT_FIELD` | `description` | ″ |
| `AZURE_SEARCH_EMBEDDING_FIELD` | `embedding` | ″ — drives the vector-query part of the request body. |
| `AZURE_SEARCH_TITLE_FIELD` | `name` | ″ |
| `AZURE_SEARCH_USE_VECTOR_QUERY` | `true` | Backend includes a `vectorQueries[]` array in the search request body when set. |
| `AZURE_SEARCH_SEMANTIC_RANKER` | `standard` | Backend includes `queryType: "semantic"` plus the semantic configuration when set. |
| `STORE_TIMEZONE` | `America/Chicago` (default; overridable per contract) | Drives happy-hour and time-based pricing logic together with a fixed clock (`BackendProfiles.FixedClock`). |
| `RUNNING_IN_PRODUCTION` | `true` | Prevents `load_dotenv()` from loading a developer's local `.env` over these values. Named after a Python-specific mechanism (`load_dotenv()`), but the *need* — never silently pick up ambient local config — is neutral: any backend under test would need an equivalent "run exactly as configured, nothing ambient" switch. |
| `LOG_LEVEL` | `INFO` | Consistent backend log verbosity across every launch; a neutral need, not Python-specific. |
| `APP_SESSION_SECRET` | random 256-bit hex, generated fresh per launch | Only this one process ever needs to validate tokens it issued itself; a neutral need (any backend issuing its own session tokens needs a secret), even though the concrete var name here is this backend's own. |
| `RATE_LIMIT_RECOVERY_ENABLED` | `true` | Matches production behaviour for the rate-limit-with-hints scenarios; a neutral need, not Python-specific. |
| `PYTHONUNBUFFERED` | `1` | Ensures `CapturedProcessOutput` sees stdout/stderr promptly instead of buffered, so failure diagnostics are complete. **Python-specific** (a CPython interpreter env var). |
| `PYTHONUTF8` | `1` | Deterministic encoding regardless of the launching machine's default. **Python-specific** (a CPython interpreter env var). |
| `CONFORMANCE_TEST_HOOKS` and its overrides (`CONFORMANCE_FIXED_NOW`, timer overrides, etc) | set only by `BackendProfiles.ShortTimers` / `.FixedClock(instant)` | See "Test hooks" below — **never** set for the default profile, so most scenarios exercise real production timing. |

PR #22 review item N8: as of this pass, only `PYTHONUNBUFFERED`/`PYTHONUTF8` are genuinely
Python-specific (CPython's own interpreter env vars) — `RUNNING_IN_PRODUCTION`, `LOG_LEVEL`,
`APP_SESSION_SECRET`, and `RATE_LIMIT_RECOVERY_ENABLED` were reclassified into the neutral group in
`BackendEnvironment.Build` (their *names* happen to come from this backend's own config surface,
but the underlying *need* — production-like startup, an explicit log level, a session secret, and
rate-limit recovery enabled — applies to any backend under test, not just this one).

### Environment stripping (PR #22 review item 13)

`new ProcessStartInfo(...).Environment` is pre-populated with a **copy of the current process's
entire environment**, not a blank slate — so without an explicit stripping step, anything ambient
in the coordinator's or CI runner's shell would silently leak into the launched backend process on
top of the explicit values above: a leftover `CONFORMANCE_TEST_HOOKS=1` from a prior manual run, an
`AZURE_SUBSCRIPTION_ID` from an unrelated `az account set`, or the corporate `HTTP_PROXY` /
`HTTPS_PROXY` the NuGet/npm/pip proxy setup relies on.

`Conformance.Harness.InheritedEnvironmentFilter.Apply(startInfo)` (called by
`PythonBackendLauncher` before layering the explicit values above on top) removes any *inherited*
variable matching, case-insensitively:

- `CONFORMANCE_*` (prefix)
- `AZURE_*` (prefix)
- `VERBOSE_*` (prefix)
- `*_PROXY` (suffix — catches `HTTP_PROXY`, `HTTPS_PROXY`, `ALL_PROXY`, `NO_PROXY`, lowercase variants, etc)

...then pins `NO_PROXY` / `no_proxy` to `127.0.0.1,localhost`, since the backend under test only
ever needs to reach the fakes and itself, both on loopback — an inherited corporate forward proxy
must never see that traffic. Because the strip runs *before* the explicit values are applied, any
`AZURE_*` value the harness deliberately wants set (the endpoint/key/deployment/etc above) still
wins; only variables the harness does **not** explicitly set are removed. Unit-tested and
mutation-checked in `InheritedEnvironmentFilterTests.cs` (pure, process-free — no backend or fakes
needed to exercise this policy).

### `/health` response shape

`GET /health` (`app/backend/app.py`'s `_health_handler`) always returns:

```json
{
  "status": "healthy",
  "version": "<app version string>",
  "checks": { "...": "per-startup-check booleans" }
}
```

`status` is `"healthy"` (HTTP 200) once every startup check passes, or `"unhealthy"` (HTTP 503)
otherwise. `PythonBackendLauncher.WaitForHealthAsync` polls this endpoint until it returns 200 (or
the backend process exits early, in which case captured stdout/stderr is included in the
failure).

### Backend logging is not a wire contract (PR #42 review item 1)

`ConformanceFixture.RunAsync(body, allowedNewBackendErrors)` bounds — from **above only** — how
many new backend ERROR-level log lines (`CapturedProcessOutput.CountUnhandledErrors`) a scenario's
own body may cause, asserted as `actual <= baseline + allowedNewBackendErrors`, never exact
equality. This is deliberately a ceiling, not a pinned count: *how many* ERROR-level lines a
backend logs for a given recovered condition (one line vs. two, or ERROR vs. WARNING) is a
logging/observability choice specific to this backend's own code, not part of the neutral contract
a correct backend in another language must reproduce. A future .NET backend that logs one line
where the Python backend logs two — or logs at a level this harness doesn't count as an "unhandled
error" at all — must still pass every scenario that uses this overload. Only genuinely *unexpected*
errors (anything above the declared ceiling) fail a scenario. The zero-arg `RunAsync(body)` overload
still asserts a hard `0` ceiling, i.e. this scenario must cause no new backend errors at all.

### Shared files

| File | Consumed by | Purpose |
|---|---|---|
| `app/frontend/src/data/menuItems.json` | `FakeSearchServer` | Source data for every fake Azure AI Search response — the same menu data the real backend's search client would otherwise be querying against the real index. |
| `app/backend/static/index.html` (gitignored; built via `npm run build`) | Python backend startup | aiohttp's `add_static` raises at app-creation time without this directory existing — `PythonBackendLauncher` checks for it explicitly and fails with a clear message instead of the opaque "backend exited early" (PR #22 review item 1). |

### Process lifecycle hardening (PR #22 review item 17)

`PythonBackendLauncher` treats "no orphaned `python.exe` after this suite exits, however it exits"
as a hard requirement, not just the graceful `IAsyncDisposable.DisposeAsync` path:

- **Windows Job Object** (`WindowsJobObject.cs`): every launched Python process is assigned to a
  job object created with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. If this .NET test process is
  itself killed forcibly (`Stop-Process`, a crash, a CI runner reaping an orphaned job) with no
  chance to run any cleanup code at all, Windows closes every handle the killed process owned —
  including the job handle — which (because of the kill-on-close limit) makes the OS itself kill
  the whole Python process tree. No-op on non-Windows (CI runs on `ubuntu-latest`); `WindowsJobObjectTests.cs`
  proves the mechanism directly (assign a real spawned process to a job, `Dispose()` the job,
  assert the process exits) and skips cleanly off-Windows.
- **`AppDomain.ProcessExit` handler**: a cross-platform belt-and-suspenders net for the *graceful*
  exit paths that skip normal disposal (an unhandled exception unwinding past
  `IAsyncDisposable`, a stray `Environment.Exit` elsewhere in the process). Registered right after
  the process starts, unregistered in `ProcessBackend.DisposeAsync` so it never fires twice or
  outlives the backend it was meant to guard.
- **Retry on port-bind races** (`PortRaceDetection.cs`): `NetworkUtils.GetFreeTcpPort()` has an
  inherent (tiny) TOCTOU race between releasing its probe socket and the backend's own bind. If
  the Python process exits within `PortRaceDetection.RaceDetectionWindow` (20s — widened from an
  initial 5s guess after empirically observing the real backend's own non-fatal Azure
  OpenAI/Search reachability probes, each with their own ~2s timeout, run *before* it attempts its
  socket bind, PR #22 review item 17) **and** its captured output matches a known bind-failure
  signature (`errno 98`/`WinError 10048`/`WinError 10013`/"address already in use"),
  `PythonBackendLauncher.StartAsync` picks a fresh port and retries, up to 3 attempts total. Any
  other early exit (a real crash) is never retried — retrying it would just hide a real bug behind
  a slow, flaky-looking pass. `PortRaceDetectionTests.cs` covers the pure heuristic; verified for
  real by pre-occupying a port with a raw `TcpListener` and confirming `PythonBackendLauncher`
  retries past it and starts healthy on a different port.
- **CI**: `.github/workflows/conformance.yml` sets `timeout-minutes` on every job (15m for
  `python-tests`/`frontend-tests`, 20m for `conformance`) so a genuine hang fails the job instead
  of burning the whole Actions time budget, and the `dotnet test` step passes
  `--blame-hang --blame-hang-timeout 10min --blame-hang-dump-type mini --blame-crash
  --blame-crash-dump-type mini` so a hang or crash produces a diagnostic dump in `TestResults`
  (already uploaded as an artifact on failure) well before the job-level timeout would otherwise
  kill it with no diagnostics at all.

### Upstream socket lifecycle on browser disconnect (PR #22 review item N14)

`ConformanceFixture.RunAsync` waits for all upstream connections to close after each scenario
(`Realtime.WaitForNoOpenConnectionsAsync`, 30s) before letting the next scenario start. **A
backend must close its upstream socket when the browser disconnects.** Python does this today
(`RTMiddleTier`'s `finally` block closes `target_ws`/detaches the session as soon as the browser
socket loop exits, independent of the resume grace hold on the *session*, which only keeps the
order/transcript around in memory — it never keeps the old upstream socket open). A C# (or any
other) backend that instead kept its upstream connection open through the full grace hold (120s in
production, 1s under `BackendProfiles.ShortTimers`) would fail every scenario after the first
30s — `WaitForNoOpenConnectionsAsync` would time out waiting for a connection nothing is ever
going to close. Documented here (rather than newly enforced) since `ConformanceFixture` already
behaves this way; see `ConformanceFixture.cs`'s `RunAsync`.

### WebSocket Origin validation (swigerb/SonicAIDriveThru#25)

A backend **must** validate the `Origin` header on the `/realtime` WebSocket upgrade by an
**exact** match — not a string-suffix match — against one of:

- the request's own `Host` header (host, and port when non-default, case-insensitive), or
- an entry in the backend's configured cross-origin allow-list (`security.allowed_origins` in
  `app/backend/config.yaml`), when that list is non-empty.

A `.endswith(host)`-style suffix check (the bug fixed by #25) is **not** conformant: it accepts a
lookalike Origin an attacker actually controls, e.g. `https://evil-<host>`, because the string
`"evil-<host>"` ends with `"<host>"` even though the two are different domains. Python's fix
(`_origin_matches_host` in `app/backend/rtmt.py`) parses the Origin with `urllib.parse.urlsplit`
and compares its `netloc` to `Host` for equality.

A request with **no** `Origin` header at all is accepted unchanged (pre-existing, #25-unaffected
behaviour) — this covers non-browser callers (e.g. server-to-server, curl) that never send one.
The Origin check runs *before* session-token validation, so it rejects a bad Origin with `403`
regardless of `security.require_session_token`.

Exercised by `Scenarios/Security/OriginValidationTests.cs`: exact origin accepted, lookalike-suffix
origin rejected with `403`, and missing origin accepted (documenting the unchanged behaviour).

An empty `host` (e.g. a malformed or missing request `Host` header) is treated as **no possible
match** — `_origin_matches_host` returns `False` rather than comparing against an empty string,
which would otherwise let a lookalike Origin with an empty host component slip through. The
rejection warning log line includes both values (`host=%s origin=%s`) so a real-world 403 is
diagnosable from logs alone.

## Session-scrub conversation item contract (swigerb/SonicAIDriveThru#29)

A backend must never let the browser see operator-only text: the bootstrap `session.instructions`
and `tools[].description`/`parameters` (scrubbed from every `session.created`/`session.updated`
echo — see `_scrub_session_for_client` in `rtmt.py`), and any conversation item the middle tier
itself authored (the greeting, resume rehydration, silence nudge, and a tool's
`function_call_output`) — none of these may reach the browser verbatim, across **every** GA
subtype that can carry a full item (`conversation.item.created`/`.added`/`.done`/`.retrieved`).

**Client item ids.** Every item the middle tier creates is stamped with an id from
`new_middle_tier_item_id()`: prefix `sonic_mt_` followed by 12 hex characters
(`secrets.token_hex(6)`), generated **fresh on every send** — including the greeting, which is
rebuilt (not cached) so a second greeting send on a different connection never reuses the first
one's id. `_drop_from_client` treats this prefix as the primary authorship signal (dropping any
`conversation.item.*` whose `item.id` starts with it, whichever subtype it arrives on), with the
legacy `role == "system"` check kept only as a second-line backstop — the greeting item is
`role: "user"`, not `"system"`, so the id-prefix check is load-bearing, not redundant.

GA accepts client-supplied item ids on `conversation.item.create` (verified live against
`gpt-realtime-2.1` / `gpt-realtime-2.1-dz`, swigerb/SonicAIDriveThru#30 review "G1"): `message`,
`function_call`, and `function_call_output` items with a `sonic_mt_<12hex>` id are accepted and
echoed back on both `.added` and `.done`. A **duplicate id within the same conversation is
rejected** with:

```json
{"type":"invalid_request_error","code":"item_create_duplicate_item_id","message":"Error adding item: an item with id '<id>' already exists.","param":null,"event_id":null}
```

A backend must therefore never reuse an item id within one upstream conversation. The fake
upstream (`RealtimeScript.cs`) models this: it tracks every client-supplied id it has seen on a
connection (`RealtimeSessionState.SeenConversationItemIds`) and replies with the exact error above
— same `code`, `type`, `param: null`, `event_id: null` — on a repeat, instead of `.added`/`.done`.
It also tracks `previous_item_id` the way GA does (set on the next item's `.added` to the
preceding item's id, client-created ids included). `WholeSessionLeakTests`' `AssertNoRepeatedItemId`
checks every `conversation.item.create` id sent upstream is unique per connection as a black-box
proof of this contract; `session_manager.py`'s own id generator is unit-tested for freshness
directly (`test_greeting_msg_gets_a_fresh_id_on_every_call` and friends).

**Session keep-alive.** `session_manager.py`'s idle-disconnect timer is reset by
`SessionManager.touch_activity`, called from `rtmt.py` on: (a) every browser→upstream client frame
*except* raw mic audio-append frames (silence streams constantly and must not count, PR #22's
idle-timeout intent), and (b) independently, on the upstream→browser path, whenever GA reports
`input_audio_buffer.speech_started` or `input_audio_transcription.completed` — i.e. the backend
also resets the idle clock on genuine guest-speech signals coming back from GA, not only on raw
client traffic. A backend that only touched activity on client frames (and never on upstream
speech-detection echoes) would still be conformant for a guest actively sending frames, but would
diverge from Python's behaviour for a guest who is speaking while some other client-frame gap
exists — documented here since it's easy to miss when porting the timer semantics (F3).

## Test hooks (`app/backend/conformance_hooks.py`)

Everything in this section is gated behind `CONFORMANCE_TEST_HOOKS=1` and is a complete no-op
without it — the inertness itself is proven by `test_conformance_hooks.py::TestInertWhenUnset`,
mutation-checked (see that file's module docstring). **Never** set `CONFORMANCE_TEST_HOOKS` in
`infra/` (bicep), the `Dockerfile`, or `azure.yaml` — guarded by
`test_conformance_hooks.py::TestNeverInInfraOrDockerfile`, which fails the build if the literal
string `CONFORMANCE_` ever appears in any of those files. It is only ever set by the conformance
harness's own child-process environment (`BackendProfiles.ShortTimers` / `.FixedClock(instant)` →
`PythonBackendOptions.ExtraEnvironment` → `BackendEnvironment.Build`).

Two independent mechanisms:

- **The frozen clock** (`CONFORMANCE_FIXED_NOW`) freezes *business wall-clock logic only* — today
  the sole consumer is `order_state.py`'s happy-hour / time-based pricing lookup. It has no effect
  on any timer duration (idle timeout, resume grace, etc). The frozen instant **does not advance**
  — every call to `now(tz)` while hooks are enabled and the var is set returns the exact same
  instant, converted into whichever `tz` the caller asked for.
- **Timer overrides** (`seconds(env_var, default)`) replace the *duration* fed into
  `asyncio.sleep`/deadline arithmetic for a specific timer. They have no effect on what `now()`
  returns.

| Variable | Units / format | Allowed range | Consumer | Failure behaviour |
|---|---|---|---|---|
| `CONFORMANCE_TEST_HOOKS` | literal string `"1"` to enable | only the exact string `"1"` counts as enabled — `"true"`/`"yes"`/`"TRUE"`/anything else leaves hooks **disabled** | `conformance_hooks.HOOKS_ENABLED`, read once at process import time | N/A — any other value is silently treated as disabled, never an error. |
| `CONFORMANCE_FIXED_NOW` | RFC 3339 timestamp with an explicit **numeric** UTC offset (e.g. `2026-07-04T15:30:00-05:00`, or a trailing `Z` for UTC) | any parseable, offset-aware instant; optional (timer-only profiles like `ShortTimers` leave it unset) | `order_state.py`'s happy-hour / time-based pricing via `conformance_hooks.now(tz)` | **Fails fast at import time** (raises `ValueError`, non-zero backend startup) if hooks are enabled and the value is missing its offset or isn't parseable at all — never silently ignored. This module (and issue #7's original task description) sometimes describes the format loosely as "ISO-8601 plus IANA zone" — that phrasing is imprecise: `datetime.fromisoformat` does **not** accept a trailing IANA zone *name* (e.g. `... America/Chicago`), only a numeric offset. Use a numeric offset always. |
| `CONFORMANCE_IDLE_TIMEOUT_SECONDS` | seconds, float | positive, finite | `session_manager.py`'s idle-disconnect timer | **Fails fast** (raises `ValueError` at the module's own import time, non-zero backend startup) if hooks are enabled and the value is present but unparseable, NaN, +/-infinity, zero, or negative. Absent/empty falls back to the production default (300s) without error. |
| `CONFORMANCE_GRACE_SECONDS` | seconds, float | positive, finite | `session_manager.py`'s resume grace-hold window | Same fail-fast rule as above (production default 120s). |
| `CONFORMANCE_NUDGE_AFTER_SECONDS` | seconds, float | positive, finite | `session_manager.py`'s resume "nudge" timer | Same fail-fast rule as above (production default 30s). |
| `CONFORMANCE_FIRST_FRAME_TIMEOUT_SECONDS` | seconds, float | positive, finite | `session_manager.py`'s post-resume first-frame timeout | Same fail-fast rule as above (production default 2.0s). |
| `CONFORMANCE_SWEEP_INTERVAL_SECONDS` | seconds, float | positive, finite | `session_manager.py`'s idle/grace sweep loop interval | Same fail-fast rule as above (production default 15s). Added for `BackendProfiles.ShortTimers` (PR #22 review item N4) — without it, a resume-sweep scenario would still wait up to 15s per sweep even under `ShortTimers`. |
| `CONFORMANCE_GREETING_TIMEOUT_SECONDS` | seconds, float | positive, finite | `rtmt.py`'s greeting-fallback timeout | Same fail-fast rule as above (production default 5.0s). |
| `CONFORMANCE_RATE_LIMIT_RETRY_DELAY_SECONDS` | seconds, float | positive, finite | `rate_limit.py`'s first retry delay default | Same fail-fast rule as above (production default 1.5s). See the caveat below: only the *default* is overridable, not the clamp bounds. |
| `CONFORMANCE_RATE_LIMIT_SECOND_RETRY_DELAY_SECONDS` | seconds, float | positive, finite | `rate_limit.py`'s second retry delay default | Same fail-fast rule as above. Same clamp-bound caveat. |

**Hint clamping caveat** (documented in `rate_limit.py`'s own module docstring, cross-referenced
here per PR #22 review item N8): the *default* delay fed into `retry_delay()` is overridable via
`seconds()` as above, but the clamp bounds (`FIRST_RETRY_BOUNDS` / `SECOND_RETRY_BOUNDS`) applied to
a scripted rate-limit hint are **not** overridable — a scripted hint still clamps into the
*production* bounds even when hooks are enabled and the retry-delay defaults are shortened.

**Startup warning**: whenever hooks are enabled, the backend logs one `WARNING`-level line
containing `CONFORMANCE_TEST_HOOKS` at process startup (`conformance_hooks._validate_at_startup`),
so a stray enabled-hooks backend is loud in its own logs rather than silently behaving oddly.

## GA validation fidelity — live-probe evidence

`GaSessionValidator` (in `src/Conformance.Fakes/GaSessionValidator.cs`) re-derives the Azure OpenAI
GA realtime `session.update` / client-event validation surface independently of
`app/backend/rtmt.py`, so the fake can't accidentally pass just because it copies the backend's own
(possibly wrong) assumptions. Two sources were used:

1. **Official OpenAI GA realtime API reference**
   <https://developers.openai.com/api/reference/resources/realtime> — fetched 2026-09-24 (saved
   locally as a scratch file during research, not committed). Used for the 14 top-level `session`
   keys, the nested `audio.input`/`audio.output` shapes, the `Realtime Error`/`Realtime Error
   Event` schema, and the client-event-type union.
2. **A single authorized live probe** against the real Azure OpenAI GA realtime service, run once
   per the coordinator's explicit instruction ("you may do ONE live probe... to confirm an error
   code"), on 2026-09-24:
   - Subscription: `44847a42-6b69-4e6c-b7e5-ce7140469dd6`
   - Endpoint: `wss://cog-axgpampkq3yfa.openai.azure.com/openai/v1/realtime?model=gpt-realtime-2.1`
   - Auth: `az account get-access-token --resource https://cognitiveservices.azure.com` (bearer
     token held only in an environment variable for the duration of the probe; never written to
     disk or committed; the throwaway probe scripts have since been deleted).

### Recorded findings

| # | Probe | Result |
|---|-------|--------|
| 1 | `session.update` with an unrecognised top-level `session` key (`totally_made_up_field`) | `error.error.code = "unknown_parameter"`, `param = "session.totally_made_up_field"`, `message = "Unknown parameter: 'session.totally_made_up_field'."`, `event_id` echoed from the request. **This caught a real bug**: the fake previously used the coarse `error.error.type` value (`"invalid_request_error"`) as the `code` field — fixed to use the specific live-confirmed code. |
| 2 | `session.update` whose `session` object omits the required `type` discriminator | `error.error.code = "missing_required_parameter"`, `param = "session.type"`, `message = "Missing required parameter: 'session.type'."`, `event_id` echoed. |
| 3 | An unrecognised top-level client event type (`extension.middle_tier_tool_response`, a leaked internal browser-protocol event type, sent directly upstream) | `error.error.code = "invalid_value"`, `param = "type"`, `message` enumerating the service's own exact supported-type list. The live list includes `session.close` and `transcription_session.update` (not derivable from the doc's prose alone) and does **not** include `output_audio_buffer.clear` (which the doc does list as a client event). `GaClientEventTypes` in the fake is the union of both, so nothing the doc promises or the live service accepts is rejected. |
| 4 | WebSocket handshake with a missing `Authorization` header | Handshake fails at the HTTP layer with **401** before any WebSocket frame is exchanged — no JSON error frame. Mirrors `FakeRealtimeUpstreamServer`'s `RequireApiKey` 401 rejection (also a plain HTTP status, not a frame). |

A representative recorded error frame (case 1, field values as observed; not the literal
byte-for-byte transcript, since the probe scripts were deleted after use per the "no push/PR,
proxy-only, clean up scratch files" rules — but the code/param/message/event_id values below are
exactly what was recorded and are what `GaSessionValidator` now asserts against):

```json
{
  "type": "error",
  "event_id": null,
  "error": {
    "type": "invalid_request_error",
    "code": "unknown_parameter",
    "message": "Unknown parameter: 'session.totally_made_up_field'.",
    "param": "session.totally_made_up_field",
    "event_id": "probe-unknown-key"
  }
}
```

### Explicitly NOT independently live-verified

Per the coordinator's "if you can't verify, say so in the README rather than guess" instruction,
the following are **not** confirmed against the real service (both would require pre-established
session/model state not reachable in a single scripted probe) and are kept only for behavioural
parity with the Python fake's own prior assumptions:

- `cannot_update_voice` — rejecting a `session.audio.output.voice` change after assistant audio has
  already been sent. Code, param, and message shape are carried over unverified.
- `reasoning` rejection on `gpt-realtime-1.5`-style deployment names. Code/message shape carried
  over unverified; the *fact* that 1.5-style deployments don't support `reasoning` is documented
  Azure/OpenAI behaviour, but the exact wire error was not reproduced live (doing so would require
  a real 1.5 deployment, which wasn't available to probe against in this pass).
- `conversation_already_has_active_response` — the error returned when `response.create` is
  received while a response is already streaming on the default conversation. See "Response cancel
  and concurrent response.create" below.
- `response_cancel_not_active` — the error returned when `response.cancel` targets nothing (no
  active response, or a `response_id` that doesn't match the active one). See below.

Both call sites carry a `NOT independently live-verified` code comment pointing back here.

## Response cancel and concurrent response.create (item N7)

`FakeRealtimeUpstreamServer` models the GA `response.cancel` event and the "only one response
writes to the default conversation at a time" rule, per the official reference
(`https://developers.openai.com/api/reference/resources/realtime`, fetched 2026-09-24 — the same
primary source cited by `GaSessionValidator`):

- **"Response Cancel Event"** — sending `response.cancel` while a response is actively streaming
  interrupts it: the fake stops emitting further scripted delta/done events, closes any still-open
  output item with `status: "incomplete"`, and sends a final `response.done` with
  `status: "cancelled"` carrying whatever output items had already been opened. The optional
  `response_id` field on the request, when present, must match the currently-active response id;
  if it doesn't (or nothing is active at all), the doc says the request errors and "the session
  will remain unaffected" — modelled here as a rejection with code `response_cancel_not_active`,
  `param: "response_id"`.
- **"Response Create Event"** — "Only one Response can write to the default Conversation at a
  time." The fake tracks one `ActiveResponseId` per connection (multiple concurrent *out-of-band*
  responses are real-GA behaviour but are not modelled by this fake, which only ever drives the
  default-conversation case exercised by the backend under test); a `response.create` received
  while that id is set is rejected rather than started, with code
  `conversation_already_has_active_response`.
- **Error code caveat**: the reference documents the cancel/conflict *behaviour* precisely (the
  `response.done`/`status: "cancelled"` shape, and the "remains unaffected" / "only one Response"
  prose) but does **not** give literal error `code` strings for either rejection case anywhere in
  the resource reference. Both codes above were grepped for verbatim in the full downloaded
  reference markdown with zero hits, so — following the same policy already applied to
  `cannot_update_voice` / `reasoning` above — they are chosen names that read naturally against the
  documented `{type, code, message, param}` error shape, but are **not independently live-verified**
  (no new live probe was run for this item; the coordinator did not re-authorise one, and the
  existing Stage B item 9 probes did not exercise `response.cancel`/concurrent `response.create`).
- Tests: `ResponseCancelTests.cs` — `Cancel_with_nothing_active_is_rejected_and_the_session_remains_unaffected`,
  `Cancel_while_streaming_stops_the_response_early_and_reports_cancelled_status`,
  `Response_create_while_a_response_is_already_active_is_rejected`. All three are mutation-checked
  (see PR history / squad history for outputs).

## Ordering scenarios (issue #9)

Black-box `update_order`/`get_order`/`reset_order`/`search` scenarios in
`tests/Conformance.Tests/Scenarios/Ordering/`, driven by scripted function calls from the fake
upstream and asserting on both the browser-bound `extension.middle_tier_tool_response` and the
`function_call_output` sent upstream. Golden pricing/tax/combo/Route-44 data ported from
`app/backend/tests/test_order_state*.py`, `test_tools*.py`, `test_order_logic.py`, and
`test_combo_orders.py` lives in one file, `tests/conformance/testdata/golden-order-pricing.json`
(loaded via `GoldenOrderPricingData.cs`), so a future C# backend (S4) can assert against the exact
same cent-accurate cases instead of a second, independently-transcribed copy.

### Money contract (PR #38 review item 1)

Every money computation this suite asserts on follows exactly one rule, stated in full in
`golden-order-pricing.json`'s own top-level `description` field:

> `line = unit * qty * (happyHourDiscount iff isDrink and happy hour is active)`;
> `subtotal = sum(line)`; `tax = subtotal * taxRate`, computed **once per order** (never per line,
> never re-derived from a rounded subtotal); `finalTotal = subtotal + tax`. There is **no rounding
> at any step** of this arithmetic — `.2f`/currency formatting is presentation-only and must never
> feed back into subtotal/tax/finalTotal math.

Consequences for how this suite is written:

- Every money value in the golden file (`unitPrice`/`price`/`taxRate`/`happyHourDiscount`/
  `expectedSubtotal`/`expectedTax`/`expectedFinalTotal`/`expectedTotal`) is stored as a **quoted,
  exact decimal string** (e.g. `"0.8152"`, `"10.185"`), computed with true decimal arithmetic —
  never as a bare JSON number, which would round-trip through `double` during parsing.
- `GoldenOrderPricingData.cs` loads every money-typed property as C# `decimal` (never `double`).
  `JsonNumberHandling.AllowReadingFromString` lets a quoted JSON string deserialize straight into a
  `decimal` property with no intermediate `double` and no custom converter.
- Scenario code parses money values off the live backend's wire responses via
  `JsonElement.GetDecimal()` — **never** `JsonElement.GetDouble()` — which reads the raw JSON number
  token text directly into `decimal`.
- All money assertions go through `OrderScenarioHelpers.AssertMoneyEqual(expected, actual)`, an
  absolute-tolerance decimal comparison. The tolerance is **backend-conditional**
  (PR #38 re-review should-fix 3): `0.000001m` when testing the live Python backend (the default,
  `CONFORMANCE_BACKEND=python`, or an external URL pointed at one) — solely to absorb *that*
  backend's own internal `double` arithmetic noise on the wire (e.g. it may echo back
  `0.8151999999999999` instead of the golden `0.8152`) — and exactly `0m` (no slack at all) when
  `CONFORMANCE_BACKEND=dotnet`. A real `decimal`-based .NET implementation has no excuse for any
  noise whatsoever; giving it the same 1e-6 slack as Python would silently let a broken
  `double`-internally implementation pass, reintroducing precisely the bug this exact-decimal
  contract exists to catch. Neither tolerance is a license to round anywhere in this suite's own
  math, and 1e-6 is far too tight to mask a genuinely wrong implementation (e.g. one that rounds
  tax to cents per line before summing).
- **Never** use xUnit's `Assert.Equal(double, double, precision: N)` for money in this stream: it
  rounds *both* operands via `Math.Round(double, N)` (banker's/to-even rounding) before comparing,
  which is simply the wrong operation for asserting on an exact wire value — a correct
  implementation's exact `10.185` and a broken one that happens to round to `10.19` first can both
  satisfy `precision: 2` equally well, and a correct exact `10.185` can just as easily be reported
  as unequal to another correct exact `10.185` if float parsing introduced even a whisker of noise
  below the second decimal place. (Separately, `10.185` *rendered* to two decimal places under this
  suite's own display-rounding rule is `10.19` — see "Rendering money for display" below — but that
  rule is about presentation text, never about how wire/golden values are compared.) There must be
  no `precision: 2` (or any other precision-based money assertion) anywhere under
  `Scenarios/Ordering/`.

### Tool-error unhandled-error-count contract (PR #38 review item 2)

`ConformanceFixture.RunAsync(Func<Task> body)` asserts, by default, that a scenario introduces
**zero** new backend unhandled-error log lines relative to a baseline captured before the scenario
runs (see the fixture's own doc comments for why it's baseline-relative rather than an absolute
zero). A scenario that deliberately provokes one **caught-and-reported** application-level tool
exception — the kind `tools.py` itself catches and turns into a graceful apology `ToolResult`
rather than letting propagate — is expected, even in a fully correct implementation, to log exactly
one such ERROR line for that exception. Use the second overload,
`RunAsync(Func<Task> body, int allowedNewBackendErrors)`, to declare an upper bound on that count
instead of letting the zero-new-errors invariant block an otherwise-passing scenario
(`ToolErrorSessionSurvivesTests.cs`'s `Session_survives_an_unhandled_tool_exception` — currently
`[Fact(Skip = ...)]` pending the Python fix tracked in #36 — is written to pass
`allowedNewBackendErrors: 1` once that fix lands). The assertion is `actual <= baseline +
allowedNewBackendErrors`, never exact equality: per PR #42 review item 1 (see "Backend logging is
not a wire contract" above), *how many* ERROR-level lines a backend logs for a given recovered
condition is a logging/observability choice, not a wire contract — a correct backend that logs
fewer lines (or none, if it logs at a level this harness doesn't count) must still pass. This
overload is shared harness (added by the parallel issue #8 stream, originally `100ed8c` as
`expectedNewBackendErrorCount` with exact-equality semantics, superseded by `39de3e1`'s rename and
ceiling semantics), which also added a `Deployment` fixture extension point unrelated to this
stream's scenarios.

### `search`'s two `select` field sets (should-fix #8)

`app/backend/tools.py::search` issues its Azure AI Search query with one of two different
`$select` field lists, depending on whether the primary attempt succeeded:

- **Primary** `select_fields`: `[id, name, category, description, sizes]` (or the configured
  identifier/content field names in place of `id`/`description`).
- **Fallback** `select` (used only after Azure responds "Could not find a property named" — the
  field-name-mismatch 400 this suite's `FakeSearchServer.RejectSelectFieldOnce` hook simulates):
  `[id, description]` (or the configured identifier/content field names). Notably, `sizes` is
  present in the primary set and **absent** from the fallback — `SearchToolTests.cs`'s fallback
  test deliberately rejects `"sizes"` for exactly this reason, so a successful retry can only be
  observed by the second request omitting it.

### Order-summary wire schema

`update_order`/`get_order`/`reset_order` are all `ToolResultDirection.TO_BOTH` (see
`app/backend/tools.py`): the `tool_result` field of the browser-bound
`extension.middle_tier_tool_response` frame is a **JSON-encoded string** (not a nested JSON object —
parse it with a second `JsonDocument.Parse`/`JsonSerializer.Deserialize` call) containing the order
summary:

```json
{
  "items": [
    { "item": "<name>", "size": "<display size, or empty>", "quantity": <int>, "price": <number>, "display": "<full display string>" }
  ],
  "total": <number>,
  "tax": <number>,
  "finalTotal": <number>
}
```

All four money fields (`items[].price`, `total`, `tax`, `finalTotal`) are numbers on the wire (not
quoted, unlike the golden file's storage format) and must always be parsed via
`JsonElement.GetDecimal()` per the money contract above. Any valid JSON spelling of the same numeric
value is equivalent on the wire (`10.185`, `10.1850`, `1.0185e1` all parse to the identical
`decimal`) — this suite must never assert on the literal token text, only on the parsed `decimal`
value, per `AssertMoneyEqual`. `search`'s `tool_result` is always `null`
(it's `ToolResultDirection.TO_SERVER`-only and never reaches the browser at all) — its
model-visible content is instead the plain-text `function_call_output` sent upstream.

### Rendering money for display (PR #38 re-review should-fix 2)

The exact-decimal contract above governs every wire/golden numeric field (`total`, `tax`,
`finalTotal`, `items[].price`) — there is no rounding anywhere in that arithmetic. Separately, the
**spoken/human-readable `$X.XX` text** the model reads back to the guest (and any `.2f`-style
display formatting) is presentation-only and follows its own, additional rule: round the exact
decimal to two places using **round half away from zero** (C#: `decimal` value with
`Math.Round(value, 2, MidpointRounding.AwayFromZero)`). This rule only ever consumes the exact
decimal as input — it must never feed back into subtotal/tax/finalTotal math, and it is
independent of (not a replacement for) the wire/golden exact-decimal contract.

Python's actual behavior does not implement this (or any single) decimal rounding rule for
half-cent-landing totals: it renders with `float`'s `:.2f` format specifier, which round-trips
through IEEE-754 double and can disagree with *every* consistent decimal rounding rule (round half
away from zero, round half to even, etc.) depending on the specific value's binary representation.
Rick's 200k-order simulation found hundreds of disagreements for values that land exactly on a half
cent. Golden cases whose `finalTotal` lands exactly on a half cent (e.g. a scenario engineered so
pre-tax subtotal + tax produces an `X.XX5` total) therefore have their spoken-text assertion
`Skip`'d, referencing #46 — this is a known, filed Python defect, not a harness or contract defect.
Non-half-cent cases are not affected by this ambiguity and their spoken-text assertions stay
active, so a backend that (for example) speaks the pre-tax subtotal instead of the final total is
still caught today.

