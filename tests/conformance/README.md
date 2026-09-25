# Conformance suite

Black-box, language-neutral conformance harness for the Sonic AI Drive-Thru realtime backend
(issue [#7](https://github.com/swigerb/SonicAIDriveThru/issues/7),
CI: [#11](https://github.com/swigerb/SonicAIDriveThru/issues/11)). Talks to a backend only over
HTTP and WebSocket; never imports backend source.

> This file documents the GA realtime protocol validation fidelity work (PR #22 review item 9)
> and the neutral `BackendContract` (PR #22 review item 13).

## Restoring packages (locked mode) — issue #24

Every project here restores with `RestorePackagesWithLockFile=true` (set repo-wide in
`Directory.Build.props`), so each project's committed `packages.lock.json` fully pins the
resolved package graph, including transitive dependencies. CI restores with
`dotnet restore Conformance.slnx --locked-mode`, which fails loudly instead of silently
re-resolving if a committed lock file is missing or doesn't match `Directory.Packages.props`/the
`.csproj` files — this is what makes the NuGet cache key
(`hashFiles('tests/conformance/Directory.Packages.props', 'tests/conformance/**/*.csproj',
'tests/conformance/**/packages.lock.json')` in `conformance.yml`) trustworthy.

**Regenerating a lock file** after changing a package reference or `Directory.Packages.props`:

```powershell
dotnet restore Conformance.slnx --force-evaluate
```

`--force-evaluate` re-resolves the full dependency graph and rewrites every project's
`packages.lock.json` in place, even though the lock files already exist — plain `dotnet restore`
alone will *not* update a lock file it can already satisfy, and deleting the lock files first is
unnecessary and just means restore has to resolve at CI/local-locked-mode time too. Commit the
updated `packages.lock.json` file(s) alongside the dependency change.

**When you need this:** if `dotnet restore --locked-mode` (or CI) fails with
**NU1004: The packages lock file is not present. Run "dotnet restore" to generate a new lock
file.**, or with a restore error naming a mismatch between the lock file and the resolved graph
(e.g. after bumping a version in `Directory.Packages.props` without regenerating), run the
`--force-evaluate` command above, verify the suite still restores cleanly with `--locked-mode`,
and commit the regenerated lock file(s).

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
  ports, then run the suite with the identical env vars set. **The external backend must also be
  started with `CONFORMANCE_TEST_HOOKS=1`** (PR #49 review round 5, "F3") if you want the
  **Default** collection's browser→upstream allow-list scenarios to pass against it — the harness
  never sets env vars for an already-running external process (unlike harness-launched mode,
  where `BackendProfiles.Default` sets it automatically, see the table below); an external backend
  started without it will fail the scenarios that forge a browser `response.create` and expect it
  forwarded, since the production gate (see "The exact allow-list" below) drops it when hooks are
  disabled.
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
- **`HooksOff` is a non-Default profile, so it is skipped in external mode too** (PR #49 review
  round 5, "F3"): `ResponseCreateHooksGateTests` launches its own dedicated `HooksOff`-profile
  backend process specifically to prove the *production* gate (`CONFORMANCE_TEST_HOOKS` unset —
  see "The exact allow-list" below) drops a browser `response.create` when hooks are off. In
  external mode that collection skips itself via the same `ExternalModeProfilePolicy` check as
  `ShortTimers`/`FixedClock` above, since there's only one already-running external backend and no
  way to know or control whether hooks are enabled on it. **This means the prod `response.create`
  gate is never verified end-to-end against an external backend (including a future C# one) by
  this suite** — it's covered only by the Python unit test
  (`test_rtmt.py::ProcessMessageToServerTests` gate coverage) and by `HooksOff` against the
  harness-launched Python backend. A C# backend's own test suite must cover this gate itself; when
  planning C# conformance coverage (issue #7), either give the C# backend an equivalent
  hooks-off unit/integration test, or extend the harness to launch a second dedicated external
  process pair for a `HooksOff`-style external run.
- **#28 N24:** `CONFORMANCE_BACKEND_URL` only changes *how* the backend is reached — it never
  implies *which* backend is running there. Pointing `CONFORMANCE_BACKEND_URL` at an
  already-running C# backend instance **also** requires setting `CONFORMANCE_BACKEND=dotnet`
  alongside it; without that, the suite still assumes Python (the default), which means the wrong
  money tolerance (`0.000001m` instead of the exact `0m` a `decimal`-based .NET backend must meet —
  see "Exact-decimal money contract" below) and the wrong set of "Known Python bug" scenarios being
  skipped instead of run (see "Python-bug scenarios skip only against Python" below). Both `env`
  vars must be set together; there is no auto-detection from the URL or from a live probe of the
  backend.
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
| `CONFORMANCE_TEST_HOOKS` and its overrides (`CONFORMANCE_FIXED_NOW`, timer overrides, etc) | `CONFORMANCE_TEST_HOOKS=1` set for **every** profile including `Default` (PR #49 review round 2, "S1"); the timer/clock *override* vars (`CONFORMANCE_FIXED_NOW`, `CONFORMANCE_*_SECONDS`) are set only by `BackendProfiles.ShortTimers` / `.FixedClock(instant)` | See "Test hooks" below — `CONFORMANCE_TEST_HOOKS=1` alone, with no paired override var also set, is a verified no-op for every timer/clock behaviour (`now()`/`seconds()` each independently require their own specific override var), so `Default` gained it purely to keep the browser-simulated `response.create` allow-list entry (gated on `conformance_hooks.hooks_enabled_now()`, see the browser→upstream allow-list contract above) working for every existing scenario without changing any scenario's timing. |

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
   `True` / `False` / `None`. The literal string comparison is case-insensitive and accepts
   synonyms, not just `"true"`/`"false"`:
   - `"true"`, `"yes"`, `"on"`, `"1"` → `True` (force reasoning on).
   - `"false"`, `"no"`, `"off"`, `"0"` → `False` (force reasoning off).
   - `""`, `"auto"`, `"null"`, `"none"` (or an unset value) → `None` ("auto"), explicitly, not
     merely by falling through as an unrecognised value.
   - Anything else is *also* `None`, but logs a `WARNING` ("Ignoring unknown reasoning_model...")
     since it wasn't one of the recognised spellings above.

   `None` is *not* the same as `False`: an explicit `false` and an unset/`"auto"` value are
   different tri-state members and are asserted separately (see below).
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

**Inputs 1–3 above (`_reasoning_model()`) only decide whether reasoning-model-only fields *may* be
sent at all — they are not sufficient on their own.** `reasoning_enabled()`, the actual gate
`_build_session` checks before adding the `reasoning` key, additionally requires
`normalize_reasoning_effort(self.reasoning_effort) is not None`:

```python
def reasoning_enabled(self) -> bool:
    return normalize_reasoning_effort(self.reasoning_effort) is not None and self._reasoning_model()
```

So even on a deployment/switch combination where `_reasoning_model()` is `True`, `reasoning` is
still omitted entirely if `AZURE_OPENAI_REALTIME_REASONING_EFFORT` / `model.reasoning_effort`
normalizes to `None` — i.e. it is unset, empty, or one of `_REASONING_DISABLED_VALUES`
(`""`, `"off"`, `"disabled"`, `"false"`, `"null"`). This is a 4th, independent precondition on top
of the three-input precedence above, not a fourth member of that precedence chain: it doesn't
interact with the rejection latch or the deployment-name default at all, it just short-circuits
`reasoning_enabled()` to `False` regardless of what they decide. `config.yaml`'s own default
(`reasoning_effort: "low"`) means every existing fixture below already has a non-`None` effort, so
this precondition isn't independently exercised by any dedicated fixture yet — noted here rather
than silently assumed.

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

## Browser→upstream allow-list contract (swigerb/SonicAIDriveThru#31)

A backend must never forward a browser-sent WebSocket event to the upstream GA realtime socket
just because it recognised (or failed to recognise) the event's `type` — every browser→upstream
event type must be checked against an explicit allow-list, and anything not on it must be dropped
(not forwarded, socket left open) with a WARNING logged, not silently passed through. Before this
contract existed, the Python backend rewrote only `session.update` by name and forwarded every
other client event type completely unchanged, which let a compromised/malicious browser page (not
just the real frontend — anything running same-origin script) override the operator's system
prompt/tools for a turn, inject a fabricated system-authored conversation item, or read back any
conversation item verbatim including middle-tier-authored ones never meant for the browser.

**PR #49 review round 2 hardened this further** after the original allow-list-by-type check turned
out to be bypassable three ways, all reproduced against the real backend, plus several should-fix
items. Every backend implementation must satisfy the full **parse → validate → re-serialise**
contract below for every browser→upstream frame — checking the type by name is necessary but not
sufficient.

### Parse → validate → re-serialise

Every browser→upstream frame is either matched byte-for-byte by the exact-frame fast path below, or
fully JSON-parsed, validated, and **rebuilt from scratch** before being forwarded — a backend must
never forward the browser's original bytes/object once its type has merely been checked ("M2"):
forwarding the original object let any extra top-level key riding along on an otherwise-legitimate
event (e.g. a forged `item` object smuggled onto `input_audio_buffer.clear`) reach upstream
unfiltered.

- **Exact-match fast path** (`_CLIENT_APPEND_FAST_PATH_RE` in `rtmt.py`, "M1"): the *only* frame
  allowed to skip JSON parsing entirely is an exact, byte-for-byte match of
  `` {"type":"input_audio_buffer.append","audio":"<base64>"} `` — no extra whitespace, no extra
  keys, this exact key order — confirmed against `useRealtime.tsx`'s/the audio worklet's
  `JSON.stringify({type: "input_audio_buffer.append", audio: base64Audio})` compact, key-order-stable
  output. A prior implementation used an *unanchored* `"type":"..."` substring search that matched
  the *first* such substring anywhere in the raw frame; combined with `json.loads` keeping the
  *last* value for a repeated key, that let a forged frame reach upstream completely unfiltered via:
  a repeated top-level `"type"` key, a `"type"` substring nested inside an arbitrary free-form
  sub-object (e.g. `item.type` or `response.metadata.type`) placed earlier in the byte stream than
  the real top-level `type`, or the same trick played on `session.update` specifically (which used
  to skip session building entirely if it took the old fast path). Anything that doesn't match the
  anchored regex exactly — including a well-formed append frame with different whitespace/key order,
  or any of the three bypasses above — falls through to the slow path, which still allows a
  *genuine* append frame in any other shape, just at the cost of a full parse instead of a regex
  match.
- **Slow path**: `json.loads` the frame; if that raises, or the result isn't a JSON object, or its
  `type` isn't a string (covers e.g. `{"type": ["x"]}`, `[]`, `"a string"`, non-JSON text), drop the
  frame with a WARNING and keep the socket open — never raise out of the message loop ("S2"). Then
  check the type against the allow-list below. If allowed, **rebuild** the message from only that
  type's allow-listed top-level keys (never the browser's original dict) and `json.dumps` it fresh —
  this is what always happens for every type, including `session.update`, whose `session` object then
  gets a second, narrower filtering pass (below).

### The exact allow-list

**Top-level event types** (`_CLIENT_ALLOWED_TYPES` in `rtmt.py`), derived from every event type
`app/frontend/src/hooks/useRealtime.tsx` (the only frontend code that talks to this socket)
actually sends, plus each type's **allow-listed top-level keys** (`_CLIENT_TOP_LEVEL_KEYS`) that
survive the rebuild:

| Type | Allowed top-level keys | Notes |
|---|---|---|
| `session.update` | `type`, `event_id`, `session` | `session` object is further filtered — see below |
| `input_audio_buffer.append` | `type`, `event_id`, `audio` | matches the fast path above in the common case |
| `input_audio_buffer.clear` | `type`, `event_id` | |
| `response.cancel` | `type`, `event_id`, `response_id` | |

Neither the legacy `input_audio_buffer.commit` nor `response.create` is sent by the real frontend
(server VAD always auto-commits and auto-triggers the model's turn) — both were **removed from the
production allow-list** ("S1"). `response.create` is re-added, but *only* when
`conformance_hooks.hooks_enabled_now()` is true (never in a real deployment — see
`test_conformance_hooks.py::TestNeverInInfraOrDockerfile`), with allowed top-level keys
`type`, `event_id`: many existing conformance scenarios use a bare `{"type":"response.create"}` as a
same-effect stand-in for a server-VAD-triggered turn. Because its allowed keys never include
`response`, any `response.instructions`/`response.tools`/`response.tool_choice` override is stripped
unconditionally as a structural property of the allow-list, not special-cased code.

**Session keys the browser may set** (`_CLIENT_SESSION_KEYS` in `rtmt.py`, "M3"): exactly
`turn_detection`, `input_audio_transcription` — what `useRealtime.tsx`'s `startSession()` actually
sends. Every other GA session key the upstream API accepts (`prompt`, `tracing`, `include`,
`truncation`, `model`, `output_modalities`, `instructions`, `tools`, `tool_choice`, ...) is
server-owned and is filtered out of the browser's `session` object *before* it reaches session
building — not merely overwritten afterwards, which previously left a fail-open gap: `instructions`
was only overwritten when the backend had its own `system_message` configured, so a backend with none
configured would forward the browser's `instructions` unchanged.

Being an allowed top-level *key* is not the same as trusting every value inside it — being GA's
own name for the fields is not a safety guarantee either (review round 3, sub-key hardening):

- **`turn_detection` sub-keys** (`_sanitize_turn_detection` in `rtmt.py`): allow-listed down to
  exactly `type`, `threshold`, `prefix_padding_ms`, `silence_duration_ms` — the four sub-keys
  `useRealtime.tsx` ever sends. `type` must be the literal `"server_vad"`; any other value (or a
  missing `type`, or a non-dict `turn_detection`) rejects the WHOLE object rather than partially
  filtering it, falling back to the server's own known-good default
  (`_BOOTSTRAP_CLIENT_SESSION["turn_detection"]`). Each numeric sub-key is bounds-checked and
  dropped *individually* if out of range or the wrong type (`bool` included — Python's `bool` is
  an `int` subclass, but `true`/`false` is never legitimate here): `threshold` ∈ `[0, 1]`
  (`int | float`); `prefix_padding_ms`/`silence_duration_ms` ∈ `[0, 5000]`, and — since review
  round 5's "S3" — must additionally be a **plain `int`**, not a `float` (`300.5` is rejected: a
  millisecond count is never legitimately fractional). Real GA `server_vad` also accepts
  `create_response`, `interrupt_response`, and `idle_timeout_ms` — none of which the frontend ever
  sends, and none of which are safe to take from the browser (`create_response: false` can
  silence the assistant entirely); they are simply never in the allow-list, regardless of value.
- **`input_audio_transcription` is fully server-owned**: the whole object is rebuilt from
  `self.transcription_model` (`_build_session` in `rtmt.py`) — never merged with whatever the
  browser sent. If a transcription model is configured, the outgoing object is exactly
  `{"model": <server's model>}`; if none is configured, no `input_audio_transcription` key is
  sent at all. Previously this only overwrote the `model` sub-key while merging in the rest of the
  browser's dict (so a smuggled sub-key, e.g. a Whisper `prompt`, could ride along unfiltered),
  and if no model was configured the browser's object — `model` included — was forwarded
  completely unchanged, a fail-open gap.

Exercised black-box by `Scenarios/Security/ClientToServerAllowListTests.cs`'s
`Turn_detection_and_transcription_sub_keys_are_sanitized_or_server_owned` (forges
`create_response`/`interrupt_response`/`idle_timeout_ms` and a smuggled `input_audio_transcription`
`model`/`prompt`; asserts the fake upstream sees only the allow-listed `turn_detection` sub-keys
and the server's own transcription model, nothing else) and
`Turn_detection_with_invalid_type_falls_back_to_the_servers_own_default` (a wrong/missing `type`
falls back to the server's default rather than partially surviving). Unit-tested at the Python
level in `test_rtmt.py`'s `_sanitize_turn_detection` tests (allow-list, bounds, bool-rejection,
wrong-type-rejection) and `ProcessMessageToServerTests`'s integration-path tests for both fields.

**Anything else — including, explicitly, `conversation.item.create` (a browser has no legitimate
reason to author a conversation item; this is also how `role: "system"`/`"developer"` injection is
blocked, by rejecting the whole event type rather than filtering the role field),
`conversation.item.retrieve`, `input_audio_buffer.commit` in production, and any bare `extension.*`
type sent directly by the browser instead of through its dedicated pre-forwarding handling — is
dropped: not forwarded, connection left open, one WARNING logged** (`Dropped disallowed
client→server event type %r`). A backend must not close the socket on an unexpected frame; an
unrecognised event on an otherwise-legitimate session is not itself proof of compromise.

`extension.*` types are deliberately absent from the allow-list table above: the Python backend
fully consumes them (resume, end_session, set_verbose_logging, set_log_to_file, `set_voice`) before
a message ever reaches the allow-list check, so they never need an entry there — but a backend
implementation that instead let an `extension.*` type fall through to this check (e.g. a bare
`extension.middle_tier_tool_response` sent directly by the browser, bypassing the normal tool-call
flow — see the GA-validation-fidelity finding #3 below) must still drop it, since it is not one of
the allow-listed entries. Side effects keyed on a browser frame (idle-activity reset, silence-nudge
cancellation, the greeting trigger, and echo-suppression barge-in) must fire on the **parsed,
validated type of the frame actually forwarded** — never on a raw substring match of the browser's
original bytes — so a dropped/rejected frame triggers none of them ("F1"; a barge-in check keyed on
a raw `"response.cancel"` substring match, evaluated before the filter ran, was itself found and
fixed in review round 5, since a frame merely *containing* that substring somewhere — without
actually being that type — could still disable echo suppression). A backend implementation should
compute this validated type once per frame and hand it directly to every side effect that needs
it, rather than having each side effect (or its caller) re-parse the forwarded string itself
("F4") — this backend's `_process_message_to_server` returns `(forwarded, sent_type)` for exactly
that reason.

### Value-shape validation, not just key allow-listing (PR #49 review round 5, "S3")

Being an allowed top-level key (or session sub-key) with the right name is still not enough — the
**shape of the value** must also be validated, independent of the key/type allow-listing above:

- **`audio`** (`input_audio_buffer.append`) must be a string drawn from the base64 alphabet
  (`^[A-Za-z0-9+/]*={0,2}$`, `_CLIENT_BASE64_RE` in `rtmt.py`) — an object, array, or
  non-base64-alphabet string drops the **whole frame** (there is no safe partial-audio fallback).
- **`event_id`** and **`response_id`** must each be a short string matching
  `^[A-Za-z0-9_-]{1,64}$` (`_CLIENT_EVENT_ID_RE`). `event_id` is advisory only — every
  `session.update`'s `event_id` is unconditionally replaced by `_SessionUpdateGuard.stamp`
  regardless of what the browser sent (see "S2" below), and no other type's `event_id` is
  load-bearing to this backend — so an invalid shape just has the **key** stripped, and the rest of
  the frame is still forwarded. `response_id` (on `response.cancel`) **is** load-bearing — it tells
  upstream which response to cancel, with no safe fallback value — so an invalid shape drops the
  **whole frame**.
- **`turn_detection.prefix_padding_ms`/`silence_duration_ms`** must be a plain `int`, not `float`
  (`300.5` is rejected) or `bool` — a millisecond count is never legitimately fractional.
  `turn_detection.threshold` remains `int | float` (`0.7` is a legitimate value).
- Every re-serialisation of a filtered frame uses `json.dumps(..., allow_nan=False)`
  (`_dump_client_to_server` in `rtmt.py`); a `ValueError` (a `NaN`/`Infinity` float smuggled
  through) drops the whole frame rather than forwarding non-standard JSON upstream. This is
  defense-in-depth: after the shape validation above, no currently-reachable code path can still
  produce a `NaN`/`Infinity` at this point, but a backend implementation must have some such
  backstop for future fields.

Exercised black-box by `ClientToServerAllowListTests.cs`'s
`Turn_detection_ms_field_as_float_is_dropped_not_the_whole_object`,
`Forged_event_id_shapes_are_stripped_not_the_whole_frame`, and
`Malformed_response_id_and_audio_shapes_drop_the_whole_frame`. Unit-tested at the Python level in
`test_rtmt.py`'s `ClientToServerAllowListTests` (audio/event_id/response_id shape probes and
`_dump_client_to_server`'s NaN backstop).

### `extension.set_voice` allow-list and per-connection semantics (issue #43, PR #49 review rounds 5-6, "M1"/"S1")

`extension.set_voice` is consumed entirely by the middle tier (never forwarded as-is — see the
"`extension.*` types are deliberately absent..." paragraph above) but the **value** it adopts is
still part of this contract, because a bad value here doesn't just corrupt one guest's own session
— it used to leak into every *other* guest's bootstrap and session-echo too:

- **Allow-listed values only**: a browser-sent voice must be a string drawn from a server-side,
  per-brand voice list (`RTMiddleTier.allowed_voices`, defaulting to the ten voices in
  `app/frontend/src/lib/voices.ts` — the frontend itself is never touched). Anything else (a
  non-string, or a string not in the list) is dropped with a WARNING; neither forwarded upstream nor
  adopted anywhere.
- **Per-connection, session-persisted, never a shared mutable global**: picking a valid voice used
  to overwrite `self.voice_choice` — a single field shared across the whole worker process — so
  guest A's pick leaked into guest B's bootstrap and echo the moment B connected, even though A and
  B never shared a session. Round 5 stopped it leaking into an *already-open* connection (a local
  `voice` read once at connection start) but left a process-wide `self._voice_override` field that
  every subsequent NEW connection's bootstrap still read — so guest A's pick still became guest B's,
  C's, ... default for as long as the worker process ran (Rick's S1: "Guest A can still change
  every guest's voice"). Round 6 removes `self._voice_override` entirely: a validated pick is
  stored by `session_manager.py`'s `SessionManager.set_voice(session_id, voice)`, keyed on the
  picking guest's own `session_id` — never on `RTMiddleTier`, never readable by any other session.
  `self.voice_choice` (the config default) is never mutated after `__init__`; a brand-new
  connection's `voice` local is always initialised from it, so a totally unrelated guest always
  gets the server default, regardless of what any other guest ever picked.
- **Resume restores the picking guest's OWN voice, and only that guest's**: because the pick is
  keyed on `session_id` (not the connection), it survives a detach/resume (a Wi-Fi blip must not
  silently revert a guest's own choice). A resume opens a brand-new upstream connection (this
  backend never reattaches an existing one), so the unconditional bootstrap sent at connection
  start always uses the config default — the resuming guest's own persisted voice can only be
  applied by a **follow-up** `session.update`, sent once `handle_resume` confirms which
  `session_id` this socket is resuming, mirroring the existing rehydration-item follow-up pattern.
  A different, brand-new session (no resume presented) never reads any other session's persisted
  voice, so it is unaffected either way.

Exercised black-box by `Scenarios/Security/ClientToServerAllowListTests.cs` (an unknown voice
reaches neither the sender's own upstream frames nor a later guest's) and
`Scenarios/Sessions/VoicePickerTests.cs`'s
`Two_concurrent_guests_voice_choices_do_not_leak_into_each_other` (an already-open connection is
unaffected by a concurrent guest's pick),
`Voice_picked_after_lock_does_not_carry_to_a_brand_new_unrelated_connection` (a brand-new,
unrelated connection's bootstrap always uses the server default, never a prior guest's pick), and
`Resumed_connection_restores_the_picked_voice` (the SAME guest's resumed connection restores their
own picked voice via a follow-up session.update, while the bootstrap that fires before the resume
is confirmed still shows the default). Unit-tested at the Python level in
`test_session_bootstrap.py` (`_only_session`-scoped assertions against
`self.rtmt._sessions.get_voice/set_voice`), `test_order_resume.py`'s `VoicePersistenceTests`
(`test_resumed_connection_restores_the_picked_voice`,
`test_a_different_brand_new_session_still_gets_the_default`), and `test_rtmt.py`'s
`ExtensionSetVoiceTests`/`SanitizeVoiceTests`.

#### Issue #57 follow-ups to the voice contract

- **FU1 (defense in depth, no observable behaviour change today)**: the resume voice-restore guard
  (`handle_resume`) also checks `not assistant_audio_seen` before sending the restore
  `session.update`. In practice `handle_resume` only ever runs on a connection's first frame
  (`reject_late_resume` handles every later one), so `assistant_audio_seen` is always `False` at
  this point and the guard changes nothing *today*. It is kept as a free, explicit safety net: were
  a future refactor (a rebrand port, or the C# backend) to relax the "resume is first-frame-only"
  invariant, sending a voice `session.update` after the upstream has voice-locked (assistant audio
  already produced) would otherwise be silently rejected (`cannot_update_voice`; see
  `SessionUpdateFallbackTests`). No new test was written for this guard specifically — it is
  unreachable via any current code path, so a synthetic test would only assert on dead code. Any
  future PR that removes the first-frame-only invariant must add one then.
- **FU2 — allowed-voices config validation**: `configure_realtime_model()` now rejects a
  non-`list` `model.allowed_voices` (e.g. a bare string, silently iterated character-by-character)
  at startup with `ValueError`, and separately rejects a configured `default_voice` that is not a
  member of the resulting allow-list. An omitted/empty `allowed_voices` falls back to the
  10-voice default set (`app/frontend/src/lib/voices.ts`). Unit-tested in `test_session_bootstrap.py`
  (`VoiceConfigValidationTests`): bare string rejected, dict rejected, list accepted, falls back to
  the default set when empty/omitted, default-voice-not-in-list fails startup, default-voice-in-list
  succeeds, `voice_choice=None` skips the membership check, and the shipped `config.yaml`'s default
  voice is confirmed present in the default allow-list.
- **FU3 — CI artifact upload on failure**: already satisfied by the existing
  `.github/workflows/conformance.yml` (`if: failure()` upload-artifact step uploads
  `tests/conformance/TestResults` — containing `conformance.trx` — and
  `tests/conformance/conformance-output.log`). The "backend log" is not a separate file: on any
  scenario failure `ConformanceFixture.RunAsync` embeds `Backend.DumpDiagnostics()` (the captured
  backend stdout/stderr ring buffer) directly into the exception message, so it lands in both the
  `.trx` and the detailed-console output log already uploaded. No workflow change was needed.
- **Probes D/E/F** (`VoicePickerTests.cs`), extending the round-6 scenarios above:
  - **D — object voice**: `Object_voice_is_rejected_and_never_reaches_upstream_or_a_new_guests_bootstrap`
    sends `{"type":"extension.set_voice","voice":{"nested":"value"}}` (a non-string value, distinct
    from the already-covered unknown-*string* case) and asserts it reaches neither the sender's own
    upstream frames nor a fresh second guest's bootstrap.
  - **E — restore ordering + a third guest**:
    `Resumed_voice_restore_precedes_any_response_create_and_a_third_guest_still_gets_the_default`
    extends the existing resume-restore scenario: after the resumed connection's restore
    `session.update` is confirmed, it sends `response.create` and asserts the restore frame's
    sequence number is strictly earlier than the `response.create` frame's — then connects a
    **third**, entirely unrelated guest and asserts its bootstrap voice is still the server default.
    (The restore-precedes-response.create ordering is additionally structurally guaranteed by
    `_forward_messages`'s single-threaded per-connection message loop — `handle_resume` fully
    `await`s its restore send before the loop advances to the next inbound frame — so no single-line
    mutation can reorder it; this is documented rather than mutation-tested for that specific
    sub-assertion.)
  - **F — end_session clears the voice**:
    `Ending_the_session_clears_the_voice_so_the_next_fresh_session_gets_the_default` has a guest pick
    a non-default voice, end the session via `extension.end_session`, then connects a second guest
    and asserts the default. **Known black-box limitation**: because every new connection gets a
    brand-new `session_id`, this scenario cannot actually distinguish whether `end_session` popped
    the voice or not (a new session was never in the voice map regardless), so it is inert against a
    regression that removes `SessionManager.end_session`'s `self._voices.pop(session_id, None)`. The
    real pin for that line is the companion Python white-box test
    `test_order_resume.py::VoicePersistenceTests::test_end_session_clears_the_persisted_voice`, which
    asserts `get_voice(session_id) is None` directly after `end_session`. Both are kept: the C#
    scenario exercises the real `extension.end_session` code path end-to-end (unlike the pre-existing
    bare-close test), the Python test is what actually catches the regression.


Exercised black-box by `Scenarios/Security/ClientToServerAllowListTests.cs`: a malicious
`response.create` override is stripped down to the bare form before the fake upstream ever sees it
(and the override text never appears on any upstream frame); a `conversation.item.create` with
`role: "system"` never reaches upstream; a `conversation.item.retrieve` never reaches upstream; each
of the three fast-path bypasses above (repeated `type` key, nested `type` substring, and the same on
`session.update`) is reproduced and proven *not* to reach upstream unfiltered; a forged extra
top-level key on an otherwise-legitimate event is proven stripped; each malformed-frame shape from
"S2" is proven dropped without tearing down the connection; and — to prove the allow-list is a
*filter*, not a kill-switch — every one of the frontend's own legitimate event types
(`session.update`, `input_audio_buffer.append`, `response.cancel`, `extension.set_voice`) still
reaches the fake upstream unimpeded. Unit-tested at the Python level in `test_rtmt.py`'s
`ProcessMessageToServerTests` (integration path, including the fast-path bypass and malformed-frame
reproductions) and `ClientToServerAllowListTests` (pure `_filter_client_to_server`/
`_CLIENT_ALLOWED_TYPES`/`_CLIENT_TOP_LEVEL_KEYS`/`_CLIENT_SESSION_KEYS` unit tests, log assertions
included).

**C# note — the repeated-`type`-key bypass has two equally-acceptable outcomes, not one:**
`AllowListBypassHardeningTests.cs`'s repeated-top-level-`type`-key scenario
(`Duplicate_top_level_type_key_is_resolved_by_last_value_or_the_whole_frame_is_dropped`) accepts
either (a) the frame is parsed with last-value-wins semantics (matching Python's `json.loads`,
which RFC 8259 permits but does not mandate) and forwarded as the *last* `type`'s allow-listed
shape with the forged override stripped, **or** (b) the whole frame is dropped outright, because
some JSON parsers (including some `System.Text.Json` configurations) reject duplicate keys rather
than silently resolving them. The security property under test is "the forged override/type-sniff
bypass must never reach upstream" — both outcomes satisfy it equally, and a future C# backend
should not be required to reproduce Python's specific last-value-wins choice to pass this
scenario.



## Session-echo allow-list contract (swigerb/SonicAIDriveThru#45)

Symmetrically, a backend must never let the browser see the *full* upstream `session.created`/
`session.updated` object either — it must build a fresh, minimal, explicitly allow-listed copy for
every such echo, not a deny-list scrub of the full object. A deny-list has to be updated every time
the upstream GA API adds a new top-level session key, and silently leaks any key nobody has
enumerated yet: GA has since shipped `prompt`, `tracing`, `include`, and `truncation`, none of which
a hypothetical deny-list written before they existed could have known to strip.

**The exact session-echo shape** (`_client_session_echo` in `rtmt.py`) every `session.created` and
`session.updated` frame is replaced with before being sent to the browser — nothing else, no matter
what the upstream session object also contains:

```json
{
  "type": "session.created",
  "event_id": "evt_...",
  "session": {
    "id": "sess_...",
    "object": "realtime.session",
    "audio": { "output": { "voice": "marin" } }
  }
}
```

i.e. exactly `{type, event_id, session:{id, object, audio:{output:{voice}}}}` — every other
top-level session key (`instructions`, `tools`, `tool_choice`, `model`, `prompt`, `tracing`,
`include`, `truncation`, `reasoning`, `parallel_tool_calls`, `max_output_tokens`,
`output_modalities`, ...) is absent, unconditionally, because the copy is built field-by-field
rather than derived from (and then pruned out of) the upstream object. `voice` is kept only because
a hypothetical future frontend handler might reasonably want the active voice — the current
frontend (`useRealtime.tsx`) has no handler at all for either event type, verified by inspection.

Exercised black-box by `Scenarios/Security/ScrubHardeningTests.cs`'s
`Session_updated_relays_only_the_allow_listed_shape_even_with_every_ga_top_level_key_set`: every GA
top-level key (including `prompt`, `tracing`, `include`, `truncation`) is set upstream via the
browser's own `session.update` (all of them are legitimately forwardable per `_GA_SESSION_TOP_LEVEL`
and accepted by `GaSessionValidator`), yet the resulting `session.updated` the browser receives has
top-level keys of exactly `{id, object, audio}`, `audio` keys of exactly `{output}`, and `output`
keys of exactly `{voice}`. The pre-existing scrub/visibility scenarios (`SessionUpdatedClientVisibilityTests.cs`
and the rest of `ScrubHardeningTests.cs`) continue to pass unchanged under the new allow-list shape.
Unit-tested at the Python level in `test_rtmt.py`.

**Related scrub, not a separate allow-list of its own:** `response.done`'s `output` array is also
scrubbed before being relayed to the browser — every `function_call` and `function_call_output`
item is stripped out (tool names and raw arguments/results are operator-only), leaving only
`message` items. Covered by `WholeSessionLeakTests` (tool-call arguments added to the tracked secret
set) plus a targeted `test_rtmt.py` scenario per output-item kind
(swigerb/SonicAIDriveThru#32).

## Session-scrub conversation item contract (swigerb/SonicAIDriveThru#29)

A backend must never let the browser see operator-only text: the bootstrap `session.instructions`
and `tools[].description`/`parameters` (scrubbed from every `session.created`/`session.updated`
echo — see the session-echo allow-list contract above, `_client_session_echo` in `rtmt.py`), and any conversation item the middle tier
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
harness's own child-process environment (`BackendProfiles.Default` / `.ShortTimers` /
`.FixedClock(instant)` → `PythonBackendOptions.ExtraEnvironment` → `BackendEnvironment.Build`).
`Default` sets `CONFORMANCE_TEST_HOOKS=1` alone (PR #49 review round 2, "S1") — with no
`CONFORMANCE_FIXED_NOW`/`CONFORMANCE_*_SECONDS` override also set, this is a verified no-op for
every mechanism below; it exists purely to keep the browser→upstream allow-list's
`response.create` entry available for every scenario, since that entry is gated on the flag being
set (see the browser→upstream allow-list contract above).

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
| `CONFORMANCE_TEST_HOOKS` | literal string `"1"` to enable | only the exact string `"1"` counts as enabled — `"true"`/`"yes"`/`"TRUE"`/anything else leaves hooks **disabled** | `conformance_hooks.HOOKS_ENABLED`, read once at process import time; also `conformance_hooks.hooks_enabled_now()`, which re-reads it live on every call (used by `rtmt.py`'s `response.create` allow-list gate — see the browser→upstream allow-list contract above — so it can't be left stuck disabled by an unrelated Python test file's in-process module reload) | N/A — any other value is silently treated as disabled, never an error. |
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

### Tool-argument price trust (#28 N23)

`SpokenTotalTests`'s two golden spoken-total cases for "Cherry Limeade medium" use `2.99`/`3.79`
as the unit price, while `golden-order-pricing.json`'s menu prices that size at `2.89`. This is
deliberate, not a stale fixture: it is this suite's explicit contract rule that **the backend
trusts whatever unit price the `update_order` tool call's own argument carries and never
re-prices, re-validates, or cross-checks it against its own menu lookup.** A scenario asserting a
spoken total is therefore free to pick any unit price for its `update_order` fixture — including
one that deliberately does not match the menu — specifically to prove the total is derived from
the tool-call argument, not silently recomputed server-side from a menu re-lookup a real customer
order would never trigger. Do not "fix" a scenario's price to match the menu; if a genuinely
menu-matching golden case is later wanted for its own reasons, add a new case rather than
resolving this apparent mismatch in the existing one.

### Python-bug scenarios skip only against Python (PR #38 review should-fix 3)

Every scenario documenting a "Known Python bug" — reproducing a genuine defect in `app/backend`
that this stream is explicitly not allowed to fix (see the fan-out rules) — is marked
`[Fact(Skip = "...", SkipWhen = nameof(BackendUnderTest.IsPython), SkipType =
typeof(BackendUnderTest))]` rather than an unconditional `Skip`. `Conformance.Harness.
BackendUnderTest.IsPython` reads `CONFORMANCE_BACKEND` (default `python`) the same way
`BackendLauncherFactory` does, so this can never drift from which backend actually got launched
(and stays correct in external mode too — `CONFORMANCE_BACKEND_URL` only changes *how* the backend
is reached, never whether `CONFORMANCE_BACKEND` still says which language is running there).

The consequence: **a new backend under test (`CONFORMANCE_BACKEND=dotnet`) actually runs these
scenarios instead of silently skipping them.** A conforming C# backend must not reproduce these
Python bugs, so the scenario is expected to *pass* there — an unconditional `Skip` would have hidden
that expectation entirely, letting a backend with the exact same bug slip through green. The 8
scenarios marked this way as of this commit: `SpokenTotalHalfCentTests` (#46),
`ComboAbsorptionTests.Reset_order_clears_the_previous_orders_absorbed_component_display` (#41),
`HappyHourBoundaryTests`'s Ched 'R' Peppers case (#39), `UpdateOrderAddRemoveModifyTests`'s two
Route 44 alias cases (#40), `SearchToolTests`'s two fallback cases (#37), and
`ToolErrorSessionSurvivesTests.Session_survives_an_unhandled_tool_exception` (#36).
(`VoicePickerTests.Two_concurrent_guests_voice_choices_do_not_leak_into_each_other` was un-skipped
in PR #49 review round 5 once #43 was actually fixed — this paragraph's count/list is corrected
here to match; it is a plain `[Fact]` now, listed instead in the voice contract section above.)

This is deliberately **not** applied to the Windows-only Job Object tests elsewhere in the suite
(`WindowsJobObjectTests.cs`) — those are plain `[Fact]`s that call `Assert.Skip(...)` at runtime
when `!OperatingSystem.IsWindows()`, a platform fact about the machine the suite itself is running
on, not about which backend is under test, and are unrelated to `BackendUnderTest`.

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
    { "item": "<name>", "size": "<canonical size key, or empty>", "quantity": <int>, "price": <number>, "display": "<full display string>" }
  ],
  "total": <number>,
  "tax": <number>,
  "finalTotal": <number>,
  "totalDisplay": "<$0.00 string>",
  "taxDisplay": "<$0.00 string>",
  "finalTotalDisplay": "<$0.00 string>"
}
```

**`items[].size` is the canonical size key, not the raw spelling or the display string (#40, PR #50
review follow-up)**: `order_state.handle_order_update` runs every incoming size through
`menu_utils.canonical_size_key()` *before* constructing the `OrderItem`, so this field is always the
lowercase, alias-resolved key used for order-line matching/merging/removal — `"route 44"`, `"xl"`,
`"medium"`, `"standard"`, etc. — never the guest's raw spelling (`"RT. 44"`, `"Route-44"`,
`"44 oz"`, `"Extra Large"` all canonicalize to `"route 44"`/`"xl"` respectively) and never the
human-readable prefix that appears in `display`/`normalize_size()` (`"Route 44"`, `"Extra Large"`).
`canonical_size_key` and `normalize_size` share the same alias-resolution table and the same
punctuation/whitespace-stripping compact-key lookup, so the matching key and the display prefix can
never disagree about which physical size a given spelling means. See
`app/backend/tests/test_tool_calling.py::CanonicalSizeKeyTests` and
`GoldenOrderPricingData.Route44.Aliases`/`Route44AliasCases` in this suite for the full alias list
(now including the punctuation variants `"44 oz"`, `"Route-44"`, `"rt. 44"`).

**`items[].item` may carry a parenthesized customization suffix, and every menuItems.json-based
classification must strip it before looking anything up (PR #50 review, second round)**:
customizations travel *inside* `item_name` itself (e.g. `"Tots (Extra Crispy)"`,
`"Cherry Limeade (Extra Cherries)"` — see `tools.py`'s `update_order`), not as a separate field. A
naive `item_name.lower()` lookup against `menu_utils.MENU_CATEGORY_MAP` therefore misses every
customised item and silently falls through to keyword-substring guessing, which can disagree with
the item's own real menu category — the concrete regression Rick caught in review: a Cheeseburger
Combo plus `"Chili Cheese Tots (Extra Cheese)"` was absorbing the tots for free (matching the bare
substring `"tots"`) instead of charging $3.79 in full, because the true item ("Chili Cheese Tots")
is a real `menuItems.json` item that is *not* one of the two combo-side-slot items. The fix is one
shared rule, `menu_utils.strip_modifiers()` (a customised name minus its trailing `(...)` suffix,
whitespace-collapsed), used everywhere a raw item name is turned into a lookup key:
`infer_category`, `infer_combo_component`, `is_happy_hour_discounted` (all via the private
`_menu_key()` wrapper) *and* `order_state.py`'s combo-conversion base-name matching (auto-removing a
standalone entree when its combo is added) — one implementation, so lookup and combo-conversion
matching can never drift apart on how a customization suffix is stripped. A direct implication: an
unknown/off-menu item (customised or not) **never** falls back into the combo side slot — only the
literal, allow-listed `"tots"`/`"groovy fries"` names do (post-modifier-stripping); the drink
keyword fallback remains for genuinely off-menu fountain drinks (Dr Pepper, Coke, Sprite, root
beer, ...) and for shakes/blasts/malts, but the latter obey
`menu_utils._SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED` for the happy-hour-discount question exactly
like their on-menu counterparts do — that flag is the single switch for every shake/blast, plain or
customised, on-menu or off. See `app/backend/tests/test_menu_utils.py::CustomisedItemMenuLookupTests`
and `CustomisedItemMenuLookupTests.cs` in this suite.

**The exact `_menu_key()` normalisation algorithm (PR #50 review, round 4 — state it precisely so
C# does the same thing, not just "something similar")**, applied in this order to *every* raw
`item_name` before it is used as a lookup key into `MENU_CATEGORY_MAP`, `_COMBO_SIDE_ITEMS`, or
`_SUNDAES`, and before the two keyword-fallback functions ever see it:
1. Remove **every** `\s*\([^)]*\)\s*` group anywhere in the string, not just a trailing one —
   `"Chili Cheese (Extra Cheese) Tots"` (a mid-string group) strips to `"Chili Cheese Tots"` exactly
   like a trailing one would, and `"Tots (Extra Crispy) (No Salt)"` (two groups) strips to `"Tots"`.
   `[^)]*` cannot cross an inner `(`, so a **nested or unbalanced** group — e.g.
   `"Tots (Extra (Really) Crispy)"` — only partially matches and leaves a stray `)` in the result
   (`"Tots Crispy)"`); this is a deliberate fail-safe, not a bug: the mangled string matches no real
   menu key, so the item falls through to full-price/no-discount rather than risking a wrong match.
2. Collapse all whitespace via `str.split()`/`" ".join(...)`, which uses the same definition as
   Python's `str.isspace()` — this treats Unicode whitespace (including U+00A0 NBSP, present
   verbatim in some `menuItems.json` names, e.g. the OREO Blast) the same as an ordinary space, no
   special-casing needed. Every whitespace character actually used across `menuItems.json` names is
   either an ordinary ASCII space or a single NBSP, and C#'s `char.IsWhiteSpace` also classifies
   NBSP as whitespace, so a C# reimplementation agrees on every real name without any extra rule.
3. Lowercase, **culture-invariantly** (`str.lower()` on the Python side; C# must use
   `ToLowerInvariant()`, not the culture-sensitive `ToLower()`, so casing can never depend on the
   host's current culture/locale).
4. Remove the `®` character (`str.replace("®", "")`), the `™` character
   (`str.replace("™", "")`), and normalise the curly/typographic apostrophe `’` (U+2019) to a plain
   ASCII apostrophe `'` (U+0027) (`str.replace("’", "'")`) — PR #50 review round 5 added the last
   two, applying the same reasoning as `®`. These three character rules are the *one* place they
   live — used everywhere `_menu_key()` is used (map construction, map lookup, combo-slot/sundae/
   happy-hour classification, and `order_state.py`'s combo-conversion matching) — there is no
   second place left where any of them could drift. Eight `menuItems.json` names carry `™` (the
   "SONIC Smasher™" family, plain and Combo variants) and one carries `’` (the "SONIC Blast® made
   with REESE'S", whose raw JSON name uses the curly apostrophe verbatim); without this step, a
   spoken "All-American SONIC Smasher" or "Reese's" (naturally omitting the unspeakable `™`, or
   typed with a plain apostrophe) would miss its own map entry, exactly like the OREO Blast's NBSP
   did before round 4. See `.squad/decisions.md` for the history of why this pattern of
   consolidating symbol-handling into `_menu_key()` started.

Keyword fallbacks (`_keyword_fallback_combo_drink`, `_keyword_fallback_happy_hour_discounted`, for
names that resolve to no `MENU_CATEGORY_MAP` entry at all, i.e. genuinely off-menu) match on
**word boundaries**, not bare substrings (PR #50 review round 4): a bare substring check let
`"tea"` match inside `"steak"`, silently absorbing an off-menu `"Philly Cheesesteak"`/
`"Steak Sandwich"` into a combo's drink slot for free and happy-hour-discounting it. A hyphen is a
non-word character in both engines (Python `\b`/`re` and C#'s `\b`/`Regex`, whose word-character
definition matches .NET's), so it is a word boundary in either regex, on either side, with no
special-casing (PR #50 review round 5, no behaviour change) — a keyword adjacent to a hyphen, e.g.
a hyphenated customization like `"(Extra-Crispy)"` or the `"All-American"` prefix on the Smasher
family, still gets a correct boundary. Both keyword lists are compiled regexes:
```
r"\b(?:slush(?:ie|y)?|limeade|ocean water|drink|tea|lemonade|coke|sprite|root beer)(?:e?s)?\b"
```
for fountain drinks, and
```
r"(?:\b|milk)(?:shake|blast|malt)(?:e?s)?\b"
```
for shakes/blasts/malts; Dr Pepper keeps its own, already-word-boundary regex unchanged. The
`(?:e?s)?` suffix (not a bare `s?`) matches the plural `-s`/`-es` forms (`"Cokes"`, `"Slushes"`) as
well as the singular; `slush(?:ie|y)?` additionally matches the spoken `"Slushie"`/`"Slushy"`
variants. The shake/blast/malt regex's `(?:\b|milk)` prefix is a narrow, deliberate carve-out (PR
#50 review round 5, "keyword over-correction"): a plain `\bshake\b` never matches `"Milkshake"` at
all because there is no word boundary between "milk" and "shake" (both are word characters), so a
guest's spoken `"Chocolate Milkshake"` fell all the way through to unclassified. Matching either a
normal word boundary OR the literal `"milk"` immediately before the keyword resolves that specific
compound without loosening the boundary for anything else — a nonsense `"Overshake Deluxe"` still
correctly does not match. Every genuine on-menu item still resolves via `MENU_CATEGORY_MAP`
directly and never reaches these fallbacks at all — see
`test_menu_utils.py::MenuCategoryMapDirectResolutionTests`, which patches both fallback functions to
raise and asserts classification never touches them for any of the 60 `menuItems.json` names.

See `app/backend/tests/test_menu_utils.py::KeywordFallbackWordBoundaryTests`,
`KeywordOverCorrectionTests`, `MenuCategoryMapDirectResolutionTests`, and
`CustomisedItemMenuLookupTests.cs`'s `ParenGroupNormalisationTests` in this suite for the
paren-group-stripping edge cases (two groups, mid-string group, nested/unbalanced group) end to end
against the live backend.

All four money fields (`items[].price`, `total`, `tax`, `finalTotal`) are numbers on the wire (not
quoted, unlike the golden file's storage format) and must always be parsed via
`JsonElement.GetDecimal()` per the money contract above. Any valid JSON spelling of the same numeric
value is equivalent on the wire (`10.185`, `10.1850`, `1.0185e1` all parse to the identical
`decimal`) — this suite must never assert on the literal token text, only on the parsed `decimal`
value, per `AssertMoneyEqual`. `search`'s `tool_result` is always `null`
(it's `ToolResultDirection.TO_SERVER`-only and never reaches the browser at all) — its
model-visible content is instead the plain-text `function_call_output` sent upstream.

**`totalDisplay`/`taxDisplay`/`finalTotalDisplay` (#47, additive)**: three extra string fields on
`OrderSummary` (`app/backend/models.py`), each the exact-decimal, `format_money()`-rendered
`"$0.00"` string for the corresponding numeric field — i.e. the *same* round-half-up display rule
documented below in "Rendering money for display", computed server-side from the pre-float-conversion
`Decimal` before it's ever exposed as a JSON number. They are additive: every existing consumer that
only reads the four numeric fields is unaffected, and this suite's exact-decimal contract above still
applies unchanged to `total`/`tax`/`finalTotal`/`items[].price`. They exist because the frontend
ticket (`app/frontend/src/components/ui/order-summary.tsx`) previously re-derived its own display
strings from the numeric fields with `.toFixed(2)`, which cannot reliably distinguish e.g.
`88.04499999999999` from `88.045` (both meant to be the exact decimal `88.045`) once either has
already degraded into a noisy IEEE-754 double — the backend's `Decimal` pipeline is the only place
with access to the true exact value, so it is the single source of truth for what the guest reads
on the ticket. Pydantic auto-fills any of the three fields that a caller omits (via
`format_money()` on the numeric field), so pre-existing direct `OrderSummary(...)` construction
sites never need to change, but `order_state.py`'s two real call sites pass the more-precise,
pre-float-conversion values explicitly.

### Rendering money for display (PR #38 re-review should-fix 2)

The exact-decimal contract above governs every wire/golden numeric field (`total`, `tax`,
`finalTotal`, `items[].price`) — there is no rounding anywhere in that arithmetic. Separately, the
**spoken/human-readable `$X.XX` text** the model reads back to the guest (and any `.2f`-style
display formatting) is presentation-only and follows its own, additional rule: round the exact
decimal to two places using **round half up** and render it as an exact, culture-invariant `"$0.00"`
string. The precise formula (PR #50 review, should-fix 3):

- **C#**: `"$" + Math.Round(v, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture)`
  for a `decimal` value `v`.
- **Python**: `app/backend/money_utils.py::format_money`, which converts the value to `Decimal`
  first — never through a binary `float` intermediate — and rounds with `decimal.ROUND_HALF_UP`
  (the `Decimal` equivalent of `MidpointRounding.AwayFromZero`), formatting the result as `"$0.00"`.

This rule only ever consumes the exact decimal as input — it must never feed back into
subtotal/tax/finalTotal math, and it is independent of (not a replacement for) the wire/golden
exact-decimal contract.

**(#46, resolved)** Python previously did not implement this (or any single) decimal rounding rule
for half-cent-landing totals: it rendered with `float`'s `:.2f` format specifier, which round-trips
through IEEE-754 double and can disagree with *every* consistent decimal rounding rule (round half
away from zero, round half to even, etc.) depending on the specific value's binary representation.
As of #46, every spoken-money surface (`tools.py`'s prompt/template paths, `order_state.py`'s
`get_order` readback, and the `*Display` wire fields above) routes through `format_money()`, which
derives its `Decimal` from the same pre-float-conversion values used for the exact wire numerics and
rounds with `ROUND_HALF_UP` — so it now agrees with this suite's round-half-up rule exactly,
including for values that land precisely on a half cent (e.g. `5.265` → `$5.27`, never `$5.26`). The
previously `Skip`'d half-cent spoken-text assertions (`SpokenTotalHalfCentTests`, referencing #46)
are un-skipped and green.

**(PR #50 review follow-up, one source of truth)** `tools.py`'s delta text and `order_state.py`'s
`get_grouped_order_for_readback` no longer call `format_money(summary.finalTotal)` a second time to
build their own `"$0.00"` string — they read `summary.finalTotalDisplay` directly, the exact same
string the wire's `finalTotalDisplay` field carries. There is now exactly one call to
`format_money()` per order mutation (inside `OrderSummary`'s construction), and every spoken surface
downstream of it is a plain string read, not a re-derivation, so the readback and the wire field can
never independently drift out of sync with each other.

### `response.cancel` still emits the normal `.done`-shaped events (#8 follow-up)

A question came up while re-checking `EchoSuppressionBargeInTests` (barge-in, #8): does GA skip the
usual per-item/per-response `.done` events for a *cancelled* response, or still emit them (just with
an "incomplete"/"cancelled" status instead of "completed")? This matters because
`app/backend/rtmt.py` drives `audio_pipeline.EchoSuppressor.on_audio_done()` off
`response.output_audio.done` regardless of why the response ended, so if GA silently dropped that
event for a cancellation, the fake would need a corresponding special case.

**Checked against the official GA realtime reference** (no live probe needed — the docs are
unambiguous on this point):

- <https://developers.openai.com/api/reference/resources/realtime/client-events.md>, `response.cancel`
  section: cancelling an in-progress response makes "the server ... respond with a `response.done`
  event with a status of `response.status=cancelled`."
- <https://developers.openai.com/api/reference/resources/realtime/server-events.md>:
  `response.output_audio.done`, `response.content_part.done`, `response.output_text.done`,
  `response.output_audio_transcript.done`, and `response.function_call_arguments.done` are each
  documented as **"Also emitted when a Response is interrupted, incomplete, or cancelled."**
  `response.done` itself is documented as **"Always emitted, no matter the final state"**
  (completed/cancelled/failed/incomplete).

**Conclusion: GA does not skip these events on cancellation** — it still emits the full
`...output_item.done` / `response.content_part.done` / `response.output_audio.done` /
`response.done(status:"cancelled")` sequence for whatever output had already started streaming,
exactly matching what `FakeRealtimeUpstreamServer.RespondAsync`'s cancellation path already does via
`CloseOpenAudioItemAsync(itemStatus: "incomplete")` followed by the cancelled `response.done`. **No
fake change was needed here** — the fake was already GA-accurate on this point.

That said, this GA behaviour is exactly why isolating `on_barge_in()` from `on_audio_done()` in a
test is subtle: any scenario where the AI has spoken real audio and then gets cancelled will *also*
run `on_audio_done()`'s own cooldown-clearing path a moment later, on the same wire sequence. A live
mutation of `on_barge_in()` (removing its `self.ai_speaking = False` line, keeping only
`self.cooldown_end = 0.0`) surfaced exactly this: `EchoSuppressionBargeInTests`'s "Phase A" was
*intended* to sidestep the race entirely by giving the greeting a silent (audio-free) fake response,
so nothing but `on_barge_in()` could ever clear `ai_speaking` before its own isolated
`response.cancel`+append proof ran — but the greeting was actually still using
`ResponseScript.Default` (which *does* carry one `AudioDeltaEvent`), so the greeting's own normal
completion cleared `ai_speaking` via `on_audio_done()` before Phase A's cancel was even sent, leaving
only `cooldown_end` to drive suppression by that point — which the mutation's surviving
`cooldown_end = 0.0` line was sufficient to lift on its own. Fixed by explicitly enqueuing an
audio-free `ResponseScript` (`[new DoneEvent()]`, no `AudioDeltaEvent`) for the greeting in that test
before triggering it, restoring genuine isolation; the mutation now fails Phase A as intended. See
the updated docstring on `EchoSuppressionBargeInTests` for the full account.

### `response.done` is the greeting's own echo-suppression safety net (#48)

`audio_pipeline.EchoSuppressor.start_greeting_suppression()` pre-sets `ai_speaking = True` before
the greeting's `response.create` is even sent, on the assumption real audio is about to stream. Two
events normally clear it: `response.output_audio.done` (`on_audio_done()`, a real audio delta/done
pair actually played) and the browser's own `response.cancel` (`on_barge_in()`, an explicit
barge-in). A greeting that produces **no audio at all** — a text-only fallback, a response
cancelled/failed by the model before any audio, a rate-limited retry with empty output — triggers
neither. Before this fix, `should_suppress_audio()` then dropped every `input_audio_buffer.append`
**forever**: the guest's mic stayed muted until they physically interrupted, which they have no
reason to do since the AI never said anything to interrupt.

The fix: `response.done` is the one event GA guarantees for *every* response regardless of status
(see "`response.cancel` still emits the normal `.done`-shaped events" above), so
`audio_pipeline.EchoSuppressor.on_response_done()` uses it as the fallback — but **only** for the
pending greeting, and **only** if nothing else already ended it:

- If `greeting_in_progress` is already `False` (no greeting pending, or `on_audio_done()` already
  ran normally for it), `on_response_done()` is a pure no-op — it must never touch a genuinely
  unrelated in-flight response's `ai_speaking` (e.g. a late/duplicate `response.done` racing a
  different, still-active response).
- If `ai_speaking` is still `True` (`on_audio_done()` never ran for this response — no completed
  `audio.done`), `on_response_done()` now splits on whether the guest actually heard anything, via
  a dedicated `_greeting_audio_seen` flag set by `on_audio_delta()` — **not** on `ai_speaking`
  itself, which `start_greeting_suppression()`'s own pre-set makes `True` before any audio exists,
  so it can't tell the two cases apart on its own (PR #58 re-review, "S1"; an earlier version of
  this fix conflated them: "latched ⇒ nothing rendered" was wrong):
  - **No audio ever seen** (`_greeting_audio_seen` is `False` — a text-only fallback, or cancelled
    before the first delta): clear `ai_speaking` **immediately, with no cooldown**. Nothing was
    ever actually rendered to the guest, so there is no residual/echo risk that would warrant
    `on_audio_done()`'s extended post-greeting cooldown (`ECHO_COOLDOWN_SEC * 2`). This is
    deliberately the same "instant, no cooldown" behaviour as `on_barge_in()`, not a delegation to
    `on_audio_done()` — an earlier draft of this fix *did* delegate to `on_audio_done()`, which
    reintroduced an artificial multi-second mute after a greeting the guest never actually heard
    (caught by `GreetingWithoutAudioUnmutesTests`, whose single post-greeting mic append landed
    inside that unwarranted cooldown window and was dropped forever, since a dropped mic frame is
    never retried/requeued by the browser at that point in the flow).
  - **At least one audio delta was seen** (`_greeting_audio_seen` is `True` — the greeting's audio
    started streaming but was cancelled/errored mid-stream, with no completing `audio.done`):
    apply the **same doubled post-greeting cooldown** `on_audio_done()` would, not the instant
    unmute above. Partial audio already reached the guest, carrying the same residual echo risk a
    normal completion does.
- If `ai_speaking` is already `False` (a real barge-in, `on_barge_in()`, already cleared it before
  this `response.done` arrived), only the `greeting_in_progress` bookkeeping flag is cleared — no
  cooldown is re-armed retroactively.

**A rate-limited greeting may still be retried (#48, PR #58 re-review "M1").** The "no audio at
all" case above legitimately includes a rate-limited `response.done` with no output — but
`rate_limit.py`'s `RateLimitRecovery` ladder (see "Rate-limit recovery" below) can then retry that
same greeting with a bare `response.create`. The greeting isn't actually over in that case, even
though the mic was correctly (and still is) unmuted in the meantime: `on_response_done()` sets
`_greeting_awaiting_retry = True` alongside the instant unmute, and `on_audio_delta()` checks it on
the very next audio delta — if set, it re-enters `greeting_in_progress` instead of treating the
retry's audio as an ordinary response. This matters because, without it, the retry's own
`speech_started` would no longer be ignored as greeting echo (a false barge-in — a regression from
`dev`, where the flag stayed latched for the whole greeting) and its own `on_audio_done()` would
apply only the normal cooldown instead of the doubled post-greeting one. The pending re-arm is
itself cancelled — by `on_speech_started()`'s genuine-speech path and by `on_barge_in()` — the
instant anything other than a retry actually happens next (real guest speech, or an explicit
browser interrupt), so a guest who starts talking during the unmuted gap before any retry audio
arrives is never mistaken for the retry. `GreetingRateLimitRetryEchoSuppressionTests` is the
black-box proof: it scripts the greeting's first attempt as a rate-limited failure, lets the
ladder's own retry produce real audio, injects a synthetic `speech_started` during that retry's
audio (bypassing the client→server filter entirely, the same way a real echoed/overlapping guest
utterance would reach the model), and asserts both that a mic append sent immediately after is
still suppressed (echo, not barge-in) and that one sent at 1.5× the plain cooldown is *still*
suppressed (the doubled cooldown, not the normal one) while one sent after the full doubled
cooldown is finally forwarded.

Wired in `rtmt.py`'s `from_server_to_client` dispatch on a new `MARKER_RESPONSE_DONE` (`'"response.done"'`)
raw-substring check, alongside the existing audio/speech markers (same substring-based dispatch
style as the rest of that loop; the fragility of substring dispatch itself is tracked separately as
#53's follow-up "F2", not addressed here).

`GreetingWithoutAudioUnmutesTests` is the direct, unassisted regression proof: it scripts the
greeting with a bare `DoneEvent()` (no `AudioDeltaEvent` at all, immediate completion, no `Pace`),
then sends exactly one guest mic `input_audio_buffer.append` with **no `response.cancel` anywhere in
the test** — proving `response.done` alone, with no browser interrupt, is what unmutes the mic.
Contrast with `EchoSuppressionBargeInTests`'s Phase A, which deliberately *delays* the greeting's
completion (via the harness's `DoneEvent.Pace`, added for this purpose) and relies on an explicit
browser barge-in to isolate `on_barge_in()` specifically — a different, narrower claim than
`GreetingWithoutAudioUnmutesTests`'s.

## A tool that raises an unhandled exception must not kill the guest's connection (#36)

`rtmt.py`'s `response.output_item.done` dispatch (`_process_message_to_client`) is the seam between
the model's function-call request and the actual tool execution (`await tool.target(...)`) plus
result marshaling (building the `function_call_output` sent upstream, and — for `TO_CLIENT`/`TO_BOTH`
results — the `extension.middle_tier_tool_response` sent to the browser). Before this fix, none of
that block was guarded: any exception raised anywhere in it (JSON-decoding the model's own
`arguments`, the tool handler itself, or the result-marshaling code) propagated straight up through
`_forward_messages`'s connection-wide `except Exception: logger.exception(...)`, which tears the
*entire* guest WebSocket down — turning one malformed or buggy tool call into a hard disconnect for
the whole ordering session.

The fix has two layers, deliberately kept as defense-in-depth rather than either one alone:

1. **Generic seam** — the whole tool-execution + result-marshaling block is wrapped in a single
   `try/except Exception`. On any exception: log server-side only via `logger.exception(...)` with
   the **tool name and session id only** (never the raw `args`, which may contain guest-entered
   text); build a short, neutral apology via `self._prompt_loader.render_error("tool_execution_failed")`
   (new key in `error_messages.yaml`; a hardcoded fallback string is used if `self._prompt_loader`
   is `None`); send it as a normal `function_call_output` to the **server only** (`server_ws`) so
   the model can gracefully recover the conversation — **never** to the client/browser (no stray
   `extension.middle_tier_tool_response` for a failed call). The guest's session, and the socket,
   survive; the very next tool call on the same connection works normally.
2. **`update_order`'s own upfront validation** (`tools.py`) — the literal reproduction cited in #36
   was a scripted `update_order` call missing `item_name`, which raised a bare `KeyError` at
   `args["item_name"]`. `update_order` now validates its full required-argument list
   (`action`, `item_name`, `size`, `quantity`) up front and returns the same kind of graceful,
   `TO_SERVER`-only `ToolResult` apology used by its other application-level rejections (the
   zero/negative-price guard, extras rules, per-item/-order limits) — instead of ever reaching a
   raise in the first place.

These two layers are complementary, not redundant: layer 2 gives `update_order`'s specific known
failure mode a precise, immediate, well-tested response; layer 1 is the safety net for *any* tool
(present or future) that raises for a reason nobody anticipated. Mutation-testing this confirmed the
layering is real, not accidental — removing layer 2 alone (`tools.py`'s validation) is still fully
caught by layer 1 at the black-box level (`ToolErrorSessionSurvivesTests` stays green, because the
generic seam in `rtmt.py` catches the resulting `KeyError` just the same as any other tool
exception); removing layer 1 alone (`rtmt.py`'s `try/except`) is *not* caught by
`ToolErrorSessionSurvivesTests` at all, because layer 2 already prevents that specific scenario from
ever raising — only a Python-level unit test that bypasses `tools.py` entirely (a directly-raising
mock tool target) pins layer 1's own behaviour.

`ToolErrorSessionSurvivesTests.Session_survives_an_unhandled_tool_exception` (previously skipped,
now unskipped) is the black-box regression proof: it scripts an `update_order` `FunctionCallEvent`
with no `item_name`, then asserts (a) a `function_call_output` for that `call_id` arrives; (b) the
connection survives and a subsequent tool call still works; (c) **no** stray
`extension.middle_tier_tool_response` reaches the browser for the failed call; (d) the output text
does not look like a coincidentally-successful order-summary JSON object. The test allows exactly
one new backend error log line (`allowedNewBackendErrors: 1`) — the `logger.exception(...)` call
itself is expected and desired (per the issue's ask to log the failure server-side); it just must no
longer propagate and tear the socket down.

## Client-controlled server logging must be gated off in production (#53)

`extension.set_verbose_logging` and `extension.set_log_to_file` are two browser-sent extension
types `rtmt.py` has always intercepted and consumed (never forwarded upstream). Both toggle
**process-wide** state on the shared `sonic-verbose` logger (`audio_pipeline.vlogger`) — a
`logging.Logger` instance, not anything scoped per-connection — so before this fix, *any* connected
guest could flip verbose logging on for the whole worker process (raising log volume for every other
connection sharing it) or attach a real `logging.FileHandler` that writes to disk under
`app/backend/logs/` (a disk-filling and guest-data-in-logs risk with zero relation to *that guest's
own* session).

**The fix:** both handlers now consult `rtmt._client_log_control_allowed()` before touching any
shared state. Off (frame dropped, `logger.warning(...)` logged, socket kept open, nothing forwarded)
unless:
- `conformance_hooks.hooks_enabled_now()` is true (live-rechecked every call, same reasoning as the
  `response.create` S1 gate — see the browser→upstream allow-list contract above), **or**
- an operator has explicitly opted in via `config.yaml`'s `security.allow_client_log_control: true`
  (env override: `ALLOW_CLIENT_LOG_CONTROL=true|false`, checked live and taking precedence over the
  config file when set to a non-empty value).

Both are **off by default** in `config.yaml` — a real production deployment ignores both extension
types entirely, exactly as if the frontend had never sent them, unless an operator has deliberately
turned on debug-mode log control.

### No conformance (wire-level) scenario for this gate — documented, not filed

Unlike `response.create` (#31/G1), there is no wire-level signal a black-box test could observe here
either way: these two extension types are **always** consumed and **never** forwarded to the fake
upstream, whether the gate allows the toggle or drops it — so "did a frame reach the fake upstream"
can't distinguish the gated case from the ungated one. The only observable effect of either branch is
the backend's own internal `logging` module state (and, for `set_log_to_file`, a file written to
disk) — inherently a Python-implementation detail, not a wire contract a future C# backend could be
held to the same way (see the "Backend logging is not a wire contract" note above, and the
`IBackendUnderTest.UnhandledErrorCount()` doc comment's identical reasoning for why this suite
deliberately avoids coupling assertions to captured backend log *text*).

Per this stream's own precedent (G1's "if the harness can't do it economically, document it as
Python-unit-only, and file nothing"): **this contract is Python-unit-only.** It's pinned by
`app/backend/tests/test_rtmt.py::ClientLogControlAllowedTests` (the gate function itself, all four
precedence combinations) and `app/backend/tests/test_session_bootstrap.py::ClientLogControlGateTests`
(full end-to-end: dropped-with-warning under production defaults, still-works under
`CONFORMANCE_TEST_HOOKS=1`, still-works via the explicit config-flag opt-in with hooks off) — both
mutation-checked (removing either handler's gate check independently turns the corresponding
end-to-end test red; see the mutation table in the #53 commit). **A C# backend must implement the
same gate itself** (off by default, `CONFORMANCE_TEST_HOOKS`-or-explicit-opt-in to enable) — there is
no conformance scenario to hold it to, only this documented expectation.

