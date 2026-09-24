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
| `RUNNING_IN_PRODUCTION` | `true` | Prevents `load_dotenv()` from loading a developer's local `.env` over these values. |
| `LOG_LEVEL` | `INFO` | Consistent backend log verbosity across every launch. |
| `PYTHONUNBUFFERED` | `1` | Ensures `CapturedProcessOutput` sees stdout/stderr promptly instead of buffered, so failure diagnostics are complete. |
| `PYTHONUTF8` | `1` | Deterministic encoding regardless of the launching machine's default. |
| `APP_SESSION_SECRET` | random 256-bit hex, generated fresh per launch | Only this one process ever needs to validate tokens it issued itself. |
| `RATE_LIMIT_RECOVERY_ENABLED` | `true` | Matches production behaviour for the rate-limit-with-hints scenarios. |
| `CONFORMANCE_TEST_HOOKS` and its overrides (`CONFORMANCE_FIXED_NOW`, timer overrides, etc) | set only by `BackendProfiles.ShortTimers` / `.FixedClock(instant)` | See "Test hooks" below — **never** set for the default profile, so most scenarios exercise real production timing. |

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

Both call sites carry a `NOT independently live-verified` code comment pointing back here.
