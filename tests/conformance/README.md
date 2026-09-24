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
  with `CONFORMANCE_BACKEND=python` (harness-launched) instead. **The same skip applies to any
  fixture that overrides `Deployment`** (PR #42 review item 2) — e.g. the
  `gpt-realtime-1.5-conformance`/`gpt-realtime-2.1-dz-conformance` fixtures in
  `ReasoningDeploymentFixtures.cs` — even when that fixture's `Profile` is otherwise Default:
  external mode's one already-running backend was started with whatever
  `AZURE_OPENAI_REALTIME_DEPLOYMENT` its operator gave it, which the harness cannot know or
  change, so running (say) the "reasoning is never sent for 1.5" assertions against it would pass
  or fail for the wrong reason instead of skipping.
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
failure). `version` is a **semver-shaped** string (`\d+\.\d+\.\d+` with optional pre-release/build
metadata) — the contract is the shape, not the Python backend's own literal value, which is an
implementation detail with its own release cadence (PR #42 review item 11).

### Wire ordering the conformance scenarios depend on (issue #8)

These orderings are asserted directly by frame sequence number in the scenarios below — they are
not incidental details of `app/backend/rtmt.py`'s current implementation, and any backend under
test (Python today, a future .NET backend for issue #7) must reproduce all of them, not just the
shape of each individual frame.

1. **The bootstrap `session.update` is the first upstream frame on the connection**, `Sequence == 0`
   on this connection's own upstream socket, before anything the browser has sent is ever
   forwarded. Asserted by frame index, not by content alone, so a backend that sent it second would
   fail even if the frame's own shape were otherwise correct.
   (`SmokeSessionBootstrapTests.Bootstrap_session_update_with_four_tools_is_the_first_upstream_frame`)

2. **The greeting `response.create` is gated on `session.updated`, but *triggered* by the browser's
   `session.update`, not the bootstrap's own reply.** `rtmt.py` arms a single `session_configured`
   gate the moment *any* `session.updated` arrives from upstream — in practice that's almost always
   the bootstrap's own near-instant reply, well before the browser sends anything (`rtmt.py`, ~line
   1320: "The bootstrap session.updated arrives as soon as the socket opens, so it must NOT trigger
   the greeting — the browser's session.update (conversation start) does that."). Only forwarding
   the *browser's* `session.update` upstream fires `send_greeting_once`, which then awaits that gate
   (already set, in the normal case) before actually emitting `response.create`. If no
   `session.updated` arrives at all within `CONFORMANCE_GREETING_TIMEOUT_SECONDS` (production
   default 5s), the greeting fires anyway rather than blocking forever.
   (`SmokeSessionBootstrapTests.Greeting_response_create_arrives_only_after_the_browser_session_update`,
   `GreetingTimeoutFallbackTests`)

3. **`extension.round_trip_token` reaches the browser strictly before `response.done` for the same
   turn.** `rtmt.py`'s `response.done` handler (inside `_process_message_to_client`) calls
   `emit_session_identifiers(client_ws, "extension.round_trip_token", ...)` directly and awaits it
   to completion *before returning* the (possibly tool-call-filtered) `response.done` payload; the
   caller only sends `response.done` to the browser after `_process_message_to_client` returns. This
   is a synchronous prerequisite, not a race that merely usually resolves in this order — no code
   path can reorder the two sends. (`ResponseCancelRelayTests`, `HeartbeatPongSurvivalTests`,
   `VoiceLockTests`, `SessionUpdateFallbackTests`, `UnrelatedErrorsDoNotTriggerFallbackTests` all
   assert this by comparing `Sequence` values directly rather than relying on arrival timing.)

4. **A rejected `session.update` recovers with exactly one fallback, sent before any
   browser-forwarded `session.update`.** When the bootstrap's own `session.update` (sequence 0) is
   rejected by an upstream `error`, the fallback (`instructions`/`tools`/`tool_choice`/`type` only)
   is sent upstream before the browser's `session.update` is ever forwarded —
   `bootstrap.Sequence < fallback.Sequence < browserUpdate.Sequence` — and no `error` frame ever
   reaches the browser for that rejection. (`SessionUpdateFallbackTests`)

5. **Close handshake ordering:**
   - **1000 `session_ended`** only fires in direct response to an `extension.end_session` frame from
     the browser; the backend closes immediately with no acknowledgement frame first.
   - **4002 `superseded`** — only the *original*, still-attached socket receives the 4002 close, and
     only *after* the resuming socket has already received its own `extension.session_resumed`
     confirmation, i.e. the resumer's success frame precedes the superseded socket's close on the
     wire. (`CloseCodeTests.Resuming_a_still_attached_session_supersedes_the_original_socket_with_4002`)
   - **4000 `idle_timeout`** fires with no client frame at all once the idle sweep interval elapses.
     Only the close-code *shape* is a contract fact here — the exact idle *timing* is issue #10's
     concern, not this suite's. (`IdleCloseCodeTests`)

   | Code | Reason string | Triggered by | Fixture |
   |---|---|---|---|
   | 1000 (`NormalClosure`) | `session_ended` | Browser sends `extension.end_session` | `CloseCodeTests` (Default) |
   | 4000 | `idle_timeout` | No client frame at all before the idle sweep fires | `IdleCloseCodeTests` (`ShortTimersConformanceFixture`, so the wait is seconds not the production 15s default) |
   | 4002 | `superseded` | Another socket resumes the same session while this one is still attached (not merely dropped) | `CloseCodeTests` (Default) |

6. **The resume-vs-fresh-start decision is made on the connection's very first client frame only.**
   Only a connection's first frame may be an `extension.resume`; sending anything else first (e.g.
   `session.update`) commits that connection to a fresh session and forecloses resuming on it later,
   and any resume attempt after the first frame is rejected outright.
   (`CloseCodeTests.Resuming_a_still_attached_session_supersedes_the_original_socket_with_4002`'s
   "A's very first client frame — not a resume — makes the resume decision fresh" comment)

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

### Reasoning contract (PR #42 review item 10)

Whether `reasoning` is sent upstream at all (`RTMiddleTier.reasoning_enabled()`/`_reasoning_model()`
in `app/backend/rtmt.py`) is decided by three independent inputs, checked in this precedence order —
a correct backend in another language must reproduce all three, in this order:

1. **A runtime rejection always wins, for the rest of the process.** If the upstream ever rejects a
   session.update because of `reasoning` (the fallback path — see the wire-ordering section above),
   an in-memory latch (`self._reasoning_rejected`, an instance field on the single per-process
   `RTMiddleTier`) flips to `True` and `reasoning` is never sent again on that connection *or any
   later connection in the same process*, regardless of what the other two inputs say. This is
   intentionally process-wide, not per-connection: a deployment that has already proven it rejects
   `reasoning` once shouldn't keep re-triggering the fallback path for every new browser tab.
2. **The explicit `reasoning_model` switch, tri-state.** `AZURE_OPENAI_REALTIME_REASONING_MODEL`
   (env) / `model.reasoning_model` (config.yaml) is parsed by `parse_reasoning_model` into
   `True` / `False` / `None`: the literal strings `"true"`/`"false"` (case-insensitive) force the
   feature on or off outright; anything else — `"auto"`, unset, empty, or an unrecognised value —
   is `None` and falls through to the name-based default (input 3). `None` is *not* the same as
   `False`: an explicit `false` and an unset/`"auto"` value are different tri-state members and are
   asserted separately (see below).
3. **The deployment-name check, `auto`'s default only.** `deployment_supports_reasoning` matches the
   deployment name against `_NON_REASONING_DEPLOYMENT_RE`, copied here **verbatim** from
   `app/backend/rtmt.py` so this doesn't silently drift from the real regex:

   ```python
   _NON_REASONING_DEPLOYMENT_RE = re.compile(
       r"^(gpt-4o.*|gpt-realtime(-mini.*|-1(\.\d+)?(-.*)?|-\d{4}-\d{2}-\d{2})?)$",
       re.IGNORECASE,
   )
   ```

   A match means *not* reasoning-capable (`gpt-4o*`, `gpt-realtime-mini*`, `gpt-realtime-1*`
   including `-1.5`, and the dated `gpt-realtime-YYYY-MM-DD` snapshot, which is the original
   non-reasoning `gpt-realtime`, not `gpt-realtime-2`). Everything else — including
   `gpt-realtime-2.1[-dz]`, this suite's own default deployment, and any unrecognised custom name —
   is assumed reasoning-capable, so an unrecognised name fails open into the fallback path (input 1)
   rather than silently omitting a feature it might actually support.

All four combinations input 2/3 can produce are covered, each pinned on its own dedicated fixture in
`ReasoningDeploymentFixtures.cs` (a distinct deployment name and/or env var forces its own backend
process, since `AZURE_OPENAI_REALTIME_DEPLOYMENT`/`AZURE_OPENAI_REALTIME_REASONING_MODEL` are read
once at Python module-import time):

| Deployment name | `reasoning_model` | Expected | Fixture |
|---|---|---|---|
| `gpt-realtime-2.1-conformance` (default) | `auto` (unset) | sent | `ConformanceFixture` (Default collection) |
| `gpt-realtime-2.1-dz-conformance` | `auto` (unset) | sent | `Gpt21DzConformanceFixture` |
| `gpt-realtime-1.5-conformance` | `auto` (unset) | **not** sent | `Gpt15ConformanceFixture` |
| `gpt-realtime-1.5-conformance` | `true` (forced) | sent (then rejected upstream → exactly one fallback) | `Gpt15ForcedReasoningConformanceFixture` |
| `gpt-realtime-2.1-conformance` (default) | `false` (forced) | **not** sent | `Gpt21ReasoningSwitchOffConformanceFixture` |

The last row is the tri-state's third member and completes the coverage: the explicit switch must
beat the name-based default in *both* directions, not just the "force reasoning on for a
non-reasoning name" direction the fourth row already proved.

**`Gpt15ForcedReasoningConformanceFixture`'s collection must stay a single test (PR #42 review item
16).** Because the rejection latch (input 1 above, `self._reasoning_rejected`) is process-wide and
permanent for the fixture's whole backend process's lifetime, a second `[Fact]` added to
`SessionUpdateFallbackTests`'s `[Collection(Gpt15ForcedReasoningConformanceCollection.Name)]` class
would run *after* the first test has already tripped the rejection and the fallback, so it would
silently observe `reasoning_enabled() == False` for the wrong reason (the latch, not a fresh
name/switch decision) — passing or failing without actually exercising what it claims to. Any new
scenario that also needs a forced-reasoning-then-rejected deployment must get its own dedicated
fixture/collection (a fresh backend process), not add a second `[Fact]` here.

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
