# Beth — History

## 2026-09-23 — Joined the Sonic Squad

- Added by the coordinator for the C# / .NET 11 port (issue #4), with Brian's approval of the direction: keep the Python backend, add a feature-equivalent C# backend on .NET 11 RC 1, validate both with one shared conformance suite, deploy them side by side, and later unify the Sonic, McDonald's and Dunkin personas in both languages.
- Toolchain verified: SDK `11.0.100-rc.1.26425.128`, installed per-user at `%LOCALAPPDATA%\Microsoft\dotnet11` (not on PATH, so call it by full path). NuGet restores only through `packagefeedproxy.microsoft.io`.
- Key design rule inherited from the analysis: Python's asyncio gives each session single-threaded semantics that the code relies on implicitly. The C# port preserves them with one sequential event loop per session (a `Channel<T>` consumed by one loop), not locks.

## 2026-09-23 — feat/conformance-harness (#7, #11)

- Implemented `tests/conformance/` (Central Package Management via `Directory.Packages.props`, restoring only through `packagefeedproxy.microsoft.io`) with `Nullable=enable` + warnings-as-errors in every suite project.
- Confirmed xUnit v3 APIs assumed during scaffolding actually exist and work on `xunit.v3` against SDK `11.0.100-rc.1.26425.128`: `Assert.Skip(string)`, `Xunit.TestContext.Current.CancellationToken`, `IAsyncLifetime` with `ValueTask`.
- Found and fixed a WebSocket server-side close-handshake bug in `FakeRealtimeUpstreamServer`: `socket.State` is `CloseReceived` (not `Open`) right after receiving a client close frame, so a naive `state == Open` guard skipped completing the handshake and the client's `CloseAsync()` threw. Fixed by accepting `Open or CloseReceived` and catching `WebSocketException` best-effort.
- Fixed `HealthEndpointTests` to parse JSON instead of raw-substring-matching `"status":"healthy"` (the real payload has spaces).
- Cross-platform fix for CI (#11): `RepoPaths.PythonExecutable()` was hardcoded to `.venv/Scripts/python.exe`; branched on `OperatingSystem.IsWindows()` since GitHub-hosted Linux runners use `.venv/bin/python`. No regression: 8/8 green ×3 on Windows after the change.
- `dotnet test tests/conformance` green 3× in a row at multiple checkpoints; `.gitignore` correctly excludes `bin/`/`obj/`.
- Commits: `817f487` (harness + smoke scenario), `9f8188a` (cross-platform fix).
- Decision logged: `.squad/decisions/inbox/beth-dotnet-harness-implementation.md`.
