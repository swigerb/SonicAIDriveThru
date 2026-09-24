namespace Conformance.Fakes;

/// <summary>
/// Thrown by <see cref="FakeRealtimeConnection.SendAsync"/> when this connection's socket isn't
/// (or is no longer) open — either <see cref="FakeRealtimeConnection.AttachSocket"/> hasn't run
/// yet, or the connection is already closing/closed. A browser dropping mid-response is a normal,
/// expected occurrence (it happens on every disconnect), not a bug — but it used to throw a
/// generic <see cref="InvalidOperationException"/>, indistinguishable from a real scripting bug
/// throwing the same type. This dedicated type lets <see cref="FakeRealtimeUpstreamServer"/>'s
/// handler-fault tracking tell "a handler tried to send after the socket was already torn down"
/// (expected teardown) apart from every other, genuinely unexpected exception a scripted rule or
/// built-in dispatch case might throw (PR #22 review item N3). Direct test sends still see it —
/// a test that calls <see cref="FakeRealtimeConnection.SendAsync"/> itself on a closed connection
/// gets the same clear, typed failure as before, just under a more specific type.
/// </summary>
public sealed class FakeConnectionClosedException(string message) : Exception(message);
