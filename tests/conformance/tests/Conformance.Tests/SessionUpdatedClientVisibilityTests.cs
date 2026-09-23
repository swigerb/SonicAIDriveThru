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
/// It does not. `app/backend/rtmt.py`'s `_process_message_to_client` explicitly scrubs
/// `instructions`/`tools` from the `session.created` frame it forwards to the browser (search
/// for "Hide the instructions, tools and max tokens from clients" in rtmt.py), but has no
/// equivalent case for `session.updated` -- and `session.updated` is not in
/// `_PASSTHROUGH_SERVER_TYPES` either, so it falls through unmodified. The upstream (real GA or
/// this fake)'s `session.updated` therefore reaches the browser carrying the system prompt and
/// the full tool schema verbatim.
///
/// Per the coordinator's explicit instruction ("if Python fails it, that's a real finding: don't
/// fix Python here"), this is NOT fixed in app/backend -- the test below is marked
/// [Fact(Skip=...)] referencing a new issue the coordinator will file, and the finding is
/// reported prominently instead.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class SessionUpdatedClientVisibilityTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact(Skip = "Known finding from PR #22 review item 10, reported to the coordinator: " +
        "app/backend/rtmt.py forwards session.updated to the browser unscrubbed -- instructions " +
        "and the full tool schema leak to the client. rtmt.py's _process_message_to_client only " +
        "scrubs session.created (see 'Hide the instructions, tools and max tokens from clients'), " +
        "not session.updated, which is also not in _PASSTHROUGH_SERVER_TYPES so it falls through " +
        "unmodified. Empirically confirmed by temporarily un-skipping this test: it fails with " +
        "'The browser must never receive the system prompt via session.updated.' Not fixed here " +
        "per the coordinator's instruction not to change Python in this harness PR -- tracked for " +
        "a follow-up issue the coordinator will file. Un-skip once fixed.")]
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
