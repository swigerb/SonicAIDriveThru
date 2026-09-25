using System.Net.WebSockets;
using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Security;

/// <summary>
/// PR #30 review "S2": every other test in this suite proves one leak path at a time (a single
/// event type, a single scenario step). Rick's review pointed out that none of them prove the
/// *whole* session — end to end, every frame type, across a reconnect — is actually free of
/// operator-only text, and specifically that the greeting item (unlike the rehydration/nudge
/// items) is `role: "user"`, not `"system"`, so it is invisible to any test that only checks the
/// `role == "system"` backstop. This scenario drives one full, realistic session lifecycle --
/// bootstrap, browser handshake, a voice change, a tool round trip, a disconnect + resume, and a
/// silence nudge (<see cref="BackendProfiles.ResumeMargin"/>) -- while dynamically capturing the
/// four pieces of text the middle tier authors and sends only to the *upstream* socket
/// (`session.instructions` from the bootstrap `session.update`, the greeting item's text
/// (`session_manager.py`'s `greeting_msg` / prompt_loader's real greeting -- `role: "user"`), the
/// resume rehydration item's text (`build_rehydration_item` -- `role: "system"`), and the silence
/// nudge item's text (`build_nudge_item` -- `role: "system"`)). It then asserts that **no** frame
/// of **any** type, on **either** browser connection, contains a &gt;=32-char substring of any of
/// them -- not just the exact `conversation.item.*` frames the narrower tests already cover, so a
/// leak via a frame type nobody thought to check (or a future GA event this suite doesn't
/// enumerate by name) would also be caught.
///
/// PR #30 review round 3, item 3 ("S2a") added a fifth secret source: every bootstrap tool's
/// `description` and its whole serialized `parameters` object, since `_scrub_session_for_client`
/// drops them from the browser-bound session echo via `session["tools"] = []` -- a different
/// line than the conversation-item drop logic the other four secrets exercise.
///
/// Deliberately captured text is limited to the operator-only secrets above, and only their
/// truly operator-only prose: the tool round trip's `function_call_output` content is *not*
/// added to the secret set, because its result legitimately reaches the browser via
/// `extension.middle_tier_tool_response` today (by design, not a leak; scrubbing
/// `response.done`'s embedded function_call args is tracked separately as out-of-scope "F2").
/// The rehydration item's embedded order JSON / recent-conversation history duplicates data the
/// browser already legitimately has (its own order state, its own earlier conversation.item /
/// transcript frames) -- rather than hand-splitting that text at a backend-specific marker
/// string, PR #30 review round 3, item 5 ("S2c") captures the *full* rehydration text and
/// subtracts only the windows that also appear in a real frame of a type the browser is
/// genuinely meant to receive (<see cref="IsLegitimateBrowserFrameType"/>), proving the overlap
/// is legitimate from this run's actual traffic instead of assuming it from wording.
///
/// Mutation: disabling PR #30 follow-up "M1" (the `conversation.item.done` /
/// `conversation.item.retrieved` drop case in `rtmt.py`) makes this fail because the rehydration
/// and/or nudge item's text leaks via its `.done` echo. Disabling "S1" (the `sonic_mt_`-prefixed
/// `item.id` authorship check in `_drop_from_client`, leaving only the `role == "system"`
/// backstop) makes this fail specifically via the greeting item, since it is the one middle-tier
/// item that is `role: "user"` and therefore not caught by the backstop alone -- the one proof no
/// other test in this suite can provide.
///
/// Runs under <see cref="BackendProfiles.ResumeMargin"/> (PR #52 CI follow-up) so the nudge timer
/// stays a fast ~1s (vs the 30s production default) while idle timeout and grace get real margin
/// (5s vs the 300s / 120s production defaults) above what this scenario's own pre-detach setup
/// (bootstrap, voice change, and a full tool round trip, on top of the shared handshake/greeting)
/// should ever take, even on a loaded CI runner — see that profile's doc comment for why the
/// original, equal 1s/1s <see cref="BackendProfiles.ShortTimers"/> raced CI run 36085091969's
/// "holding order for 0s" detach.
/// </summary>
[Collection(ResumeMarginConformanceCollection.Name)]
public sealed class WholeSessionLeakTests(ResumeMarginConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Any run of 32 or more identical characters is treated as a leaked secret; shorter
    /// overlaps (word fragments, punctuation, etc.) are allowed to avoid false positives.</summary>
    private const int MinLeakSubstringLength = 32;

    [Fact]
    public Task Browser_never_receives_any_substring_of_operator_only_text_across_the_whole_session() =>
        fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var secretWindows = new HashSet<string>(StringComparer.Ordinal);

        // ── Bootstrap + browser handshake (first connection). ──
        var firstConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        var first = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var firstConnection = await firstConnectionTask;
        Assert.True(firstConnection is not null, "No upstream connection was accepted for the first browser socket.");

        var bootstrap = await firstConnection!.ReceivedFrames.WaitForAsync(f => f.Sequence == 0, FrameTimeout, ct);
        Assert.True(bootstrap is not null, "Bootstrap session.update never arrived upstream.");
        var instructions = bootstrap!.Json.GetProperty("session").GetProperty("instructions").GetString();
        Assert.False(string.IsNullOrEmpty(instructions), "Bootstrap session.update must carry non-empty instructions.");
        AddSecretWindows(instructions, secretWindows);

        // ── PR #30 review round 3, item 3 ("S2a"): every tool's description and its whole
        // serialized parameters object are just as operator-only as `instructions` -- they are
        // dropped from the browser-bound session echo by `_scrub_session_for_client` setting
        // `session["tools"] = []`, not by anything role- or authorship-based, so this is the one
        // proof in the suite that specifically exercises *that* line rather than the
        // conversation-item drop logic. ──
        var bootstrapTools = bootstrap.Json.GetProperty("session").GetProperty("tools");
        Assert.True(bootstrapTools.GetArrayLength() > 0, "Bootstrap session.update must carry at least one tool.");
        foreach (var tool in bootstrapTools.EnumerateArray())
        {
            var toolDescription = tool.TryGetProperty("description", out var descProp) ? descProp.GetString() : null;
            AddSecretWindows(toolDescription, secretWindows);
            if (tool.TryGetProperty("parameters", out var parameters))
            {
                AddSecretWindows(parameters.GetRawText(), secretWindows);
            }
        }

        // ── Voice change: sent as the very first client frame (before SendStartSessionAsync),
        // so it lands before any assistant audio and isn't silently ignored as voice-locked. ──
        await first.SendExtensionSetVoiceAsync("marin", ct);
        var voiceUpdate = await firstConnection.ReceivedFrames.WaitForAsync(
            f => f.Sequence > 0 && f.Type == "session.update" &&
                 f.Json.GetProperty("session").TryGetProperty("audio", out var audio) &&
                 audio.TryGetProperty("output", out var output) &&
                 output.TryGetProperty("voice", out var voice) &&
                 voice.GetString() == "marin",
            FrameTimeout, ct);
        Assert.True(voiceUpdate is not null, "Expected the voice-change session.update to reach the fake upstream.");

        var metadata = await first.ReceivedFrames.WaitForAsync(f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(metadata is not null, "Expected extension.session_metadata on the first connection.");
        var resumeId = metadata!.Json.GetProperty("resumeId").GetString();
        Assert.False(string.IsNullOrEmpty(resumeId), "extension.session_metadata must carry a non-empty resumeId.");

        // ── Starting the session fires the greeting: a client-authored (role: "user")
        // conversation.item.create sent straight to the upstream socket, never meant for the
        // browser. Its text is the one secret the role=="system" backstop alone can't catch. ──
        await first.SendStartSessionAsync(cancellationToken: ct);

        var greetingItem = await firstConnection.ReceivedFrames.WaitForAsync(
            f => f.Type == "conversation.item.create" &&
                 f.Json.TryGetProperty("item", out var item) &&
                 item.TryGetProperty("role", out var role) &&
                 role.GetString() == "user",
            FrameTimeout, ct);
        Assert.True(greetingItem is not null, "Expected the greeting item to reach the fake upstream.");
        AddSecretWindows(GreetingText(greetingItem!.Json.GetProperty("item")), secretWindows);

        var greetingRoundTrip = await first.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");

        // ── Tool round trip: realistic mid-conversation traffic, per UpdateOrderToolCallTests.
        // Its function_call_output text is intentionally *not* added to secretWindows -- see the
        // class doc comment. ──
        const string callId = "call_whole_session_leak_1";
        firstConnection.Script.Enqueue(new ResponseScript([
            new FunctionCallEvent(
                Name: "update_order",
                ArgumentsJson: """{"action":"add","item_name":"Small Fries","size":"Small","quantity":1,"price":2.49}""",
                CallId: callId),
            new DoneEvent(),
        ]));
        await first.SendResponseCreateAsync(ct);

        var functionCallOutput = await firstConnection.ReceivedFrames.WaitForAsync(
            f => f.Type == "conversation.item.create" &&
                 f.Json.TryGetProperty("item", out var item) &&
                 item.TryGetProperty("type", out var itemType) &&
                 itemType.GetString() == "function_call_output" &&
                 item.TryGetProperty("call_id", out var respondedCallId) &&
                 respondedCallId.GetString() == callId,
            FrameTimeout, ct);
        Assert.True(functionCallOutput is not null,
            $"Expected a conversation.item.create(function_call_output) for call_id={callId} upstream.");

        var toolResponse = await first.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.middle_tier_tool_response" &&
                 f.Json.TryGetProperty("tool_name", out var toolName) &&
                 toolName.GetString() == "update_order",
            FrameTimeout, ct);
        Assert.True(toolResponse is not null, "Expected extension.middle_tier_tool_response for update_order on the browser.");

        // ── Search tool round trip (PR #30 review round 3, item 4, "S2b"): unlike
        // update_order's, `search`'s result is model-only -- `tools.py`'s `search()` always
        // returns `ToolResultDirection.TO_SERVER` (never `TO_BOTH`), so it has no legitimate
        // browser-visible echo and its `function_call_output.output` belongs in the secret set.
        // Uses the S2c generic helper (not a hand-picked substring) so any future legitimate
        // overlap -- e.g. the model reading a matched item's name back to the guest in a
        // transcript -- is excluded by proof against this run's real frames, not assumed away. ──
        const string searchCallId = "call_whole_session_leak_search";
        firstConnection.Script.Enqueue(new ResponseScript([
            new FunctionCallEvent(
                Name: "search",
                ArgumentsJson: """{"query":"Angus"}""",
                CallId: searchCallId),
            new DoneEvent(),
        ]));
        await first.SendResponseCreateAsync(ct);

        var searchFunctionCallOutput = await firstConnection.ReceivedFrames.WaitForAsync(
            f => f.Type == "conversation.item.create" &&
                 f.Json.TryGetProperty("item", out var item) &&
                 item.TryGetProperty("type", out var itemType) &&
                 itemType.GetString() == "function_call_output" &&
                 item.TryGetProperty("call_id", out var respondedCallId) &&
                 respondedCallId.GetString() == searchCallId,
            FrameTimeout, ct);
        Assert.True(searchFunctionCallOutput is not null,
            $"Expected a conversation.item.create(function_call_output) for call_id={searchCallId} upstream.");
        var searchOutput = searchFunctionCallOutput!.Json.GetProperty("item").GetProperty("output").GetString();
        Assert.False(string.IsNullOrEmpty(searchOutput), "Expected FakeSearchServer to return non-empty search results.");
        AddSecretWindowsExcludingLegitimateOverlap(searchOutput, first.ReceivedFrames.Snapshot(), secretWindows);

        // ── Disconnect; the session detaches and is held for the (shortened) grace period. ──
        await first.CloseAsync(cancellationToken: ct);
        await first.WaitForCloseAsync(FrameTimeout, ct);
        await first.DisposeAsync();

        // ── Resume on a fresh socket within the grace hold. ──
        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var second = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var secondConnection = await secondConnectionTask;
        Assert.True(secondConnection is not null, "No upstream connection was accepted for the resumed browser socket.");

        await second.SendExtensionResumeAsync(resumeId!, ct);

        var resumed = await second.ReceivedFrames.WaitForAsync(f => f.Type == "extension.session_resumed", FrameTimeout, ct);
        Assert.True(resumed is not null, "Expected extension.session_resumed on the resumed connection.");

        var rehydrationItem = await secondConnection!.ReceivedFrames.WaitForAsync(
            f => f.Type == "conversation.item.create" &&
                 f.Json.TryGetProperty("item", out var item) &&
                 item.TryGetProperty("role", out var role) &&
                 role.GetString() == "system",
            FrameTimeout, ct);
        Assert.True(rehydrationItem is not null,
            "Precondition failed: the backend never sent the rehydration item upstream on the resumed connection.");
        // PR #30 review round 3, item 5 ("S2c"): capture the *full* rehydration text (preamble +
        // embedded order JSON + recent-conversation history), then subtract only the windows that
        // also legitimately appear in frame types the browser is genuinely meant to receive --
        // rather than the old approach of hand-splitting off a literal marker string and keeping
        // only the part before it. That hard-coded a specific backend wording (the
        // "Current order (JSON):" marker) as the boundary between operator-only prose and
        // legitimate duplicate data instead of proving the actual overlap is legitimate, and
        // wouldn't generalize to another secret (S2b's search result) with a differently-shaped
        // legitimate-overlap risk.
        var legitimateBrowserFrames = first.ReceivedFrames.Snapshot().Concat(second.ReceivedFrames.Snapshot());
        AddSecretWindowsExcludingLegitimateOverlap(
            GreetingText(rehydrationItem!.Json.GetProperty("item")), legitimateBrowserFrames, secretWindows);

        // ── Wait for the silence nudge (ResumeMargin's ~1s nudge timer) with a keepalive so the
        // idle sweep (which now has real margin, but the keepalive still resets it defensively)
        // doesn't close the session first -- same race and same fix as
        // ResumeRehydrationClientVisibilityTests (PR #30 review "S3"). ──
        using var keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var keepAliveTask = Task.Run(async () =>
        {
            try
            {
                while (!keepAliveCts.IsCancellationRequested)
                {
                    await second.SendExtensionSetVerboseLoggingAsync(false, keepAliveCts.Token);
                    await Task.Delay(TimeSpan.FromMilliseconds(200), keepAliveCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected once the wait below cancels the keepalive loop.
            }
            catch (WebSocketException)
            {
                // PR #52 CI follow-up (swigerb/SonicAIDriveThru#28 N10 aftermath): same race and
                // same fix as ResumeRehydrationClientVisibilityTests -- this loop's only job is
                // best-effort activity to stop the idle sweep beating the nudge, so a socket
                // that's already gone (the backend closed/aborted it) has nothing left to keep
                // alive. Swallowing it here lets the real assertions below report what actually
                // happened instead of this unrelated send exception pre-empting them.
            }
        }, CancellationToken.None);

        var nudgeItem = await secondConnection.ReceivedFrames.WaitForAsync(
            f => f.Type == "conversation.item.create" &&
                 f.Sequence > rehydrationItem.Sequence &&
                 f.Json.TryGetProperty("item", out var item) &&
                 item.TryGetProperty("role", out var role) &&
                 role.GetString() == "system",
            FrameTimeout, ct);

        var nudgeResponseCreated = await second.ReceivedFrames.WaitForAsync(
            f => f.Type == "response.created", FrameTimeout, ct);

        keepAliveCts.Cancel();
        await keepAliveTask;

        Assert.True(nudgeItem is not null, "Expected the silence nudge item to reach the fake upstream.");
        Assert.True(nudgeResponseCreated is not null,
            "Expected the silence nudge's response.created to reach the browser after the resume.");
        AddSecretWindows(GreetingText(nudgeItem!.Json.GetProperty("item")), secretWindows);

        Assert.True(secretWindows.Count > 0,
            "Test bug: no operator-only secret text was captured, so the leak check below would prove nothing.");

        // ── PR #30 review "G1"/item 2: GA rejects a conversation.item.create that reuses an id
        // already used in the conversation (live-verified against gpt-realtime-2.1 /
        // gpt-realtime-2.1-dz) with item_create_duplicate_item_id, so the backend must never
        // send one twice. Checked per connection, not across both, since a resume opens a brand
        // new upstream conversation with its own id namespace -- the greeting (connection 1) and
        // the rehydration/nudge items (connection 2) are never at risk of colliding with each
        // other, only with something else on their *own* connection. ──
        AssertNoRepeatedItemId(firstConnection);
        AssertNoRepeatedItemId(secondConnection);

        // ── The actual assertion: across every frame of every type on both browser connections,
        // no >=32-char substring of any of the four secrets ever reached the browser. ──
        var allBrowserFrames = first.ReceivedFrames.Snapshot().Concat(second.ReceivedFrames.Snapshot());
        RecordedFrame? leaked = null;
        foreach (var frame in allBrowserFrames)
        {
            if (ContainsAnySecretWindow(frame.Json, secretWindows))
            {
                leaked = frame;
                break;
            }
        }
        Assert.True(leaked is null,
            $"The browser must never receive any {MinLeakSubstringLength}+ char substring of operator-only text " +
            $"(instructions, greeting, rehydration or nudge), but frame type '{leaked?.Type}' did: {leaked?.Json.GetRawText()}");
    });

    /// <summary>PR #30 review "G1"/item 2: asserts every `conversation.item.create` the backend
    /// sent on this upstream connection used a distinct `item.id` -- GA rejects a repeat within
    /// one conversation with `item_create_duplicate_item_id` (live-verified against
    /// gpt-realtime-2.1 / gpt-realtime-2.1-dz for PR #30), so a collision here would mean a
    /// browser-visible request would actually fail against the real service. Mutation: reverting
    /// `new_middle_tier_item_id()` to a fixed string (rather than a fresh `secrets.token_hex(6)`
    /// per call) makes this fail on connection 2, since the rehydration and nudge items would
    /// then share one id -- the greeting itself can't be used as the mutation trigger here
    /// because <c>send_greeting_once</c>'s own `greeting_sent` guard means it is only ever
    /// invoked once per upstream connection regardless of whether its id is fresh or cached, so
    /// item 1's "stamp a fresh id per send" fix has no *second* same-connection greeting send to
    /// prove reachable black-box today; item 1's Python unit test
    /// (`test_greeting_msg_gets_a_fresh_id_on_every_call`) remains its sole regression guard.</summary>
    private static void AssertNoRepeatedItemId(FakeRealtimeConnection connection)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var frame in connection.ReceivedFrames.Snapshot())
        {
            if (frame.Type != "conversation.item.create" ||
                !frame.Json.TryGetProperty("item", out var item) ||
                !item.TryGetProperty("id", out var idProp) ||
                idProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            var id = idProp.GetString()!;
            Assert.True(seen.Add(id),
                $"The backend sent conversation.item.create with id '{id}' more than once on the same " +
                "upstream connection -- GA would reject the second one with item_create_duplicate_item_id.");
        }
    }

    /// <summary>Extracts the plain `content[0].text` GA/legacy conversation items of this shape
    /// (greeting, rehydration and nudge items all use it) carry -- named generically because the
    /// same extraction applies to all three.</summary>
    private static string? GreetingText(JsonElement item) =>
        item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array &&
        content.GetArrayLength() > 0 && content[0].TryGetProperty("text", out var text)
            ? text.GetString()
            : null;

    /// <summary>The frame types the browser is genuinely meant to receive text through, so a
    /// secret window that also shows up in one of these is legitimate duplicate data (the
    /// guest's own order, its own earlier transcript), not a leak. Used by
    /// <see cref="AddSecretWindowsExcludingLegitimateOverlap"/> to decide what to subtract from a
    /// candidate secret's windows, rather than hand-splitting the secret's own text by a
    /// backend-specific marker string. Does *not* include `extension.middle_tier_tool_response`
    /// -- see <see cref="IsLegitimateBrowserFrame"/>, which needs the tool name too.</summary>
    private static bool IsLegitimateBrowserFrameType(string type) =>
        type is "extension.session_resumed" or "conversation.item.input_audio_transcription.completed" ||
        type.StartsWith("response.audio_transcript.", StringComparison.Ordinal);

    /// <summary>Tools whose result is genuinely meant to reach the browser today (`tools.py`'s
    /// `ToolResultDirection.TO_BOTH`), so their `extension.middle_tier_tool_response` frame is
    /// legitimate duplicate data for overlap purposes -- e.g. `update_order`'s JSON order
    /// summary duplicating the rehydration item's embedded order snapshot. `search` is
    /// deliberately absent: its result is model-only (`TO_SERVER`), so it must never legitimize
    /// overlap through this frame type -- if it ever appeared here, that *is* the leak "S2b"
    /// exists to catch, not something to explain away.</summary>
    private static readonly HashSet<string> ToolsWithLegitimateBrowserEcho = new(StringComparer.Ordinal)
    {
        "update_order", "get_order", "reset_order",
    };

    /// <summary>Frame-aware companion to <see cref="IsLegitimateBrowserFrameType"/>: an
    /// `extension.middle_tier_tool_response` frame only counts as legitimate overlap when its
    /// `tool_name` is one the middle tier genuinely echoes to the browser
    /// (<see cref="ToolsWithLegitimateBrowserEcho"/>) -- scoping by *frame type alone* would let
    /// a leaked `search` result (routed onto the same frame type by a hypothetical regression)
    /// silently launder itself as "legitimate" just because *some* tool's response uses that
    /// type this run.</summary>
    private static bool IsLegitimateBrowserFrame(RecordedFrame frame) =>
        IsLegitimateBrowserFrameType(frame.Type) ||
        (frame.Type == "extension.middle_tier_tool_response" &&
         frame.Json.TryGetProperty("tool_name", out var toolName) &&
         toolName.GetString() is string name &&
         ToolsWithLegitimateBrowserEcho.Contains(name));

    /// <summary>Generic replacement for hand-narrowing a secret's text to only its "safe" prose
    /// (PR #30 review round 3, item 5, "S2c"): takes every 32-char window of the *full* secret
    /// text, then removes any window that also appears in a real captured frame of a frame type
    /// the browser is genuinely meant to receive (<see cref="IsLegitimateBrowserFrame"/>) --
    /// i.e. legitimate overlap is proven from what the browser actually got this run, not assumed
    /// from where a marker string happens to sit in the backend's wording. What's left after
    /// subtraction is added to <paramref name="windows"/> as usual.</summary>
    private static void AddSecretWindowsExcludingLegitimateOverlap(
        string? fullText, IEnumerable<RecordedFrame> browserFrames, HashSet<string> windows)
    {
        Assert.False(string.IsNullOrEmpty(fullText), "Test bug: expected non-empty operator-only text to capture as a secret.");
        var candidateWindows = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i <= fullText!.Length - MinLeakSubstringLength; i++)
        {
            candidateWindows.Add(fullText.Substring(i, MinLeakSubstringLength));
        }

        var legitimateWindows = new HashSet<string>(StringComparer.Ordinal);
        foreach (var frame in browserFrames)
        {
            if (IsLegitimateBrowserFrame(frame))
            {
                CollectStringWindows(frame.Json, legitimateWindows);
            }
        }

        candidateWindows.ExceptWith(legitimateWindows);
        windows.UnionWith(candidateWindows);
    }


    /// <summary>Recursively collects every 32-char window of every string leaf in a frame's JSON
    /// -- the same walk <see cref="ContainsAnySecretWindow"/> does for matching, reused here to
    /// build the legitimate-overlap exclusion set from real frames instead of matching against
    /// precomputed secrets.</summary>
    private static void CollectStringWindows(JsonElement element, HashSet<string> windows)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }
                for (var i = 0; i <= text.Length - MinLeakSubstringLength; i++)
                {
                    windows.Add(text.Substring(i, MinLeakSubstringLength));
                }
                return;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectStringWindows(property.Value, windows);
                }
                return;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectStringWindows(item, windows);
                }
                return;
        }
    }

    private static void AddSecretWindows(string? text, HashSet<string> windows)
    {
        Assert.False(string.IsNullOrEmpty(text), "Test bug: expected non-empty operator-only text to capture as a secret.");
        for (var i = 0; i <= text!.Length - MinLeakSubstringLength; i++)
        {
            windows.Add(text.Substring(i, MinLeakSubstringLength));
        }
    }

    /// <summary>Recursively walks every string leaf in a frame's JSON looking for any substring
    /// that matches one of the precomputed secret windows.</summary>
    private static bool ContainsAnySecretWindow(JsonElement element, HashSet<string> secretWindows)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                if (string.IsNullOrEmpty(text) || text.Length < MinLeakSubstringLength)
                {
                    return false;
                }
                for (var i = 0; i <= text.Length - MinLeakSubstringLength; i++)
                {
                    if (secretWindows.Contains(text.Substring(i, MinLeakSubstringLength)))
                    {
                        return true;
                    }
                }
                return false;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (ContainsAnySecretWindow(property.Value, secretWindows))
                    {
                        return true;
                    }
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsAnySecretWindow(item, secretWindows))
                    {
                        return true;
                    }
                }
                return false;
            default:
                return false;
        }
    }
}
