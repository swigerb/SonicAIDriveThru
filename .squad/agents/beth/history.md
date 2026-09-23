# Beth — History

## 2026-09-23 — Joined the Sonic Squad

- Added by the coordinator for the C# / .NET 11 port (issue #4), with Brian's approval of the direction: keep the Python backend, add a feature-equivalent C# backend on .NET 11 RC 1, validate both with one shared conformance suite, deploy them side by side, and later unify the Sonic, McDonald's and Dunkin personas in both languages.
- Toolchain verified: SDK `11.0.100-rc.1.26425.128`, installed per-user at `%LOCALAPPDATA%\Microsoft\dotnet11` (not on PATH, so call it by full path). NuGet restores only through `packagefeedproxy.microsoft.io`.
- Key design rule inherited from the analysis: Python's asyncio gives each session single-threaded semantics that the code relies on implicitly. The C# port preserves them with one sequential event loop per session (a `Channel<T>` consumed by one loop), not locks.
