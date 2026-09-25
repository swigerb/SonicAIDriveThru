using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// PR #22 review item 10: the fake's `session.updated` echoes the full effective session (see
/// <see cref="FakeRealtimeScriptingModelTests.Session_updated_echoes_the_full_accumulated_effective_session_across_updates"/>
/// for that in isolation). This file additionally asks "does the *browser* ever see
/// `instructions`/`tools` in a forwarded `session.updated`?" against the real Python backend.
///
/// It used to leak both: `app/backend/rtmt.py`'s `_process_message_to_client` scrubbed
/// `instructions`/`tools` from the `session.created` frame it forwards to the browser, but had no
/// equivalent case for `session.updated` -- and `session.updated` is not in
/// `_PASSTHROUGH_SERVER_TYPES` either, so it fell through unmodified. Fixed in issue #27 by
/// factoring the scrub into `RTMiddleTier._scrub_session_for_client` and routing both
/// `session.created` and `session.updated` through it.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class SessionUpdatedClientVisibilityTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Browser_never_receives_instructions_or_tools_in_session_updated() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);

        var sessionUpdated = await browser.ReceivedFrames.WaitForAsync(f => f.Type == "session.updated", FrameTimeout, ct);
        Assert.True(sessionUpdated is not null, "Expected the browser to receive a session.updated frame (forwarded from the bootstrap upstream connection) within the timeout.");

        var session = sessionUpdated!.Json.GetProperty("session");

        var hasNonEmptyInstructions = session.TryGetProperty("instructions", out var instructions) &&
            instructions.ValueKind == System.Text.Json.JsonValueKind.String &&
            instructions.GetString() is { Length: > 0 };
        var hasNonEmptyTools = session.TryGetProperty("tools", out var tools) &&
            tools.ValueKind == System.Text.Json.JsonValueKind.Array &&
            tools.GetArrayLength() > 0;

        Assert.False(hasNonEmptyInstructions, "The browser must never receive the system prompt via session.updated.");
        Assert.False(hasNonEmptyTools, "The browser must never receive the tool schema via session.updated.");
    });
}
