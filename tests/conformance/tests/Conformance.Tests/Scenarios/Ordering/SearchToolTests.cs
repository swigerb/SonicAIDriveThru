using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Issue #9: the `search` tool against FakeSearchServer — the happy path (and confirmation that
/// search results, unlike update_order/get_order/reset_order, never reach the browser: see
/// app/backend/tools.py::search always returning ToolResultDirection.TO_SERVER), plus the
/// "Could not find a property named" field-name-mismatch fallback path (harness follow-up #23).
/// FakeSearchServer.RejectSelectFieldOnce is the additive harness hook this file exercises — see
/// its doc comment in tests/conformance/src/Conformance.Fakes/FakeSearchServer.cs.
///
/// The harness-side 400 simulation (RejectSelectFieldOnce) works correctly and was verified to
/// produce exactly the response tools.py's fallback branch matches on. However, exercising it
/// against the live backend uncovered a genuine Python bug: tools.py's try/except only wraps the
/// initial (lazy) `search_client.search(...)` call, not the `async for` iteration where the
/// azure-search-documents SDK actually performs the HTTP request and raises HttpResponseError —
/// so the fallback-retry branch can never fire in practice. See the Skip reasons on the two
/// affected facts below for the full empirical detail.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class SearchToolTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Search_returns_a_result_upstream_and_never_notifies_the_browser() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        // Unique query text (module-scope `_search_cache` in tools.py is process-wide) so this
        // test's request is guaranteed to actually reach FakeSearchServer rather than short-
        // circuiting on a cache hit from another test that searched the same text. The first
        // "word" is a nonce that deliberately matches nothing (keeping the whole query text
        // unique for the cache key) rather than a real menu term like "sonic" -- almost every
        // item's name is brand-prefixed with "SONIC(R)", so a broad term like that would match
        // (and, with no relevance ranking in the fake, bury "cherry"/"limeade" behind burgers
        // that happen to sort earlier in menuItems.json) far more of the catalog than intended,
        // defeating the point of asserting on a specific matched item.
        const string query = "conformance-issue9-search-happypath cherry limeade";
        var result = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "search",
            $$"""{"query":"{{query}}"}""",
            "call_search_happy_path", roundTripIndex, ct, toClient: false);

        Assert.False(string.IsNullOrWhiteSpace(result.FunctionCallOutputText));
        Assert.Null(result.ToolResultJson);

        // PR #38 review item 4 (Rick's M3): prove the function_call_output actually reflects
        // FakeSearchServer's matched document rather than a generic "no results" apology that
        // would pass regardless of what the fake search index returned. tools.py::search embeds
        // `f"Item: {item_name}, ..."` verbatim per matched document into the model-facing output
        // text (see FakeSearchServer.Filter's per-term case-insensitive Contains match against the
        // query), so a mutation that ignores the fake's results and always returns a fixed
        // placeholder string must fail this.
        Assert.Contains("Cherry Limeade", result.FunctionCallOutputText);

        // Belt-and-braces: prove no extension.middle_tier_tool_response for "search" ever arrived
        // on the browser at all (not just that we didn't wait for one) -- ToolResultDirection
        // .TO_SERVER means rtmt.py's `if result.destination in (TO_CLIENT, TO_BOTH)` guard is
        // never true for this tool.
        var anySearchToolResponse = browser.ReceivedFrames.Snapshot().Any(f =>
            f.Type == "extension.middle_tier_tool_response" &&
            f.Json.TryGetProperty("tool_name", out var toolName) &&
            toolName.GetString() == "search");
        Assert.False(anySearchToolResponse, "search must never emit extension.middle_tier_tool_response.");
    });

    [Fact(Skip = "Known Python bug (tracked in #37): app/backend/tools.py's search() wraps only the initial " +
        "`await search_client.search(...)` call in try/except HttpResponseError (lines 206-244) " +
        "expecting the field-mismatch 400 to raise there, but the azure-search-documents async " +
        "client is lazy -- `search_client.search(...)` returns immediately without making any " +
        "HTTP request, and the first-page fetch (and therefore any HttpResponseError, including " +
        "the \"Could not find a property named\" 400 this test injects) only happens later, " +
        "inside `async for record in search_results:` at tools.py line 251 -- OUTSIDE the " +
        "try/except block. This makes the fallback-retry branch (lines 218-229) dead code: any " +
        "select-field-mismatch 400 propagates as an unhandled HttpResponseError up through " +
        "rtmt.py's connection-wide catch-all (the same mechanism as " +
        "ToolErrorSessionSurvivesTests.Session_survives_an_unhandled_tool_exception), tearing " +
        "down the WebSocket connection instead of retrying. Empirically confirmed: the backend " +
        "log shows the fake 400 was received and parsed into `azure.core.exceptions." +
        "HttpResponseError: (InvalidRequestParameter) Could not find a property named 'sizes' " +
        "on type 'search.document'.` raised from tools.py:251 (via " +
        "azure/search/documents/aio/_operations/_patch.py's __anext__), then \"Session ... " +
        "detached (client close code=None)\" -- no retry request was ever sent, and no " +
        "function_call_output reached the upstream socket. app/backend must not be modified " +
        "from this stream; see the #9 report for details.",
        SkipWhen = nameof(BackendUnderTest.IsPython), SkipType = typeof(BackendUnderTest))]
    public Task Search_retries_with_a_minimal_select_after_the_field_name_fallback_400() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        const string query = "sonic issue9 search fallback select mismatch";
        const string rejectedField = "sizes"; // present in tools.py's normal select_fields, absent from its fallback select

        fixture.Search.RejectSelectFieldOnce = rejectedField;

        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "search",
            $$"""{"query":"{{query}}"}""",
            "call_search_fallback", roundTripIndex, ct, toClient: false);

        // The retry succeeded (a genuinely-failed search would instead return the TO_SERVER
        // apology text from tools.py's HttpResponseError-else branch), and the one-shot flag
        // auto-cleared so later requests are unaffected.
        Assert.False(string.IsNullOrWhiteSpace(result.FunctionCallOutputText));
        Assert.Null(fixture.Search.RejectSelectFieldOnce);

        var matchingRequests = fixture.Search.ReceivedRequests.Snapshot()
            .Where(f => f.Json.TryGetProperty("search", out var s) && s.GetString() == query)
            .ToList();
        Assert.Equal(2, matchingRequests.Count);

        var firstSelect = matchingRequests[0].Json.GetProperty("select").GetString()!;
        Assert.Contains(rejectedField, firstSelect.Split(',', StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

        var retrySelect = matchingRequests[1].Json.GetProperty("select").GetString()!;
        Assert.DoesNotContain(rejectedField, retrySelect.Split(',', StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
    });

    [Fact(Skip = "Known Python bug (tracked in #37): depends on the search field-name fallback retry actually " +
        "succeeding (see Search_retries_with_a_minimal_select_after_the_field_name_fallback_400's " +
        "Skip reason) -- the fallback's HttpResponseError propagates unhandled and tears down " +
        "the connection before a later update_order call could ever prove the session survives. " +
        "app/backend must not be modified from this stream; see the #9 report for details.",
        SkipWhen = nameof(BackendUnderTest.IsPython), SkipType = typeof(BackendUnderTest))]
    public Task Session_survives_the_search_fallback_and_a_later_update_order_call_still_works() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        const string query = "sonic issue9 search fallback then order still works";
        fixture.Search.RejectSelectFieldOnce = "sizes";

        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var searchResult = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "search",
            $$"""{"query":"{{query}}"}""",
            "call_search_then_order", roundTripIndex, ct, toClient: false);

        var orderResult = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", "Tots", "medium", 1, 2.79m)],
            searchResult.RoundTripIndex, ct);

        var order = JsonDocument.Parse(orderResult.ToolResultJson!).RootElement;
        Assert.Equal(1, order.GetProperty("items").GetArrayLength());
    });
}
