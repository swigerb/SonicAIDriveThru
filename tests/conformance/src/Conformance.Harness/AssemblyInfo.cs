using System.Runtime.CompilerServices;

// PR #52 CI follow-up round 4 (swigerb/SonicAIDriveThru#28): exposes
// RealtimeBrowserClient.CreateForTesting(WebSocket) to Conformance.Tests, so a deterministic
// fault-injection test can wrap a fake WebSocket around it without needing a real connection or
// any production-facing public API surface.
[assembly: InternalsVisibleTo("Conformance.Tests")]
