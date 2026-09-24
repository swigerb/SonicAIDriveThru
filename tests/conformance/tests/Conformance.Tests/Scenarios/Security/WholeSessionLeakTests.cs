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
/// silence nudge (<see cref="BackendProfiles.ShortTimers"/>) -- while dynamically capturing the
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
/// Deliberately captured text is limited to the four operator-only secrets above, and only their
/// truly operator-only prose: the tool round trip's `function_call_output` content is *not*
/// added to the secret set, because its result legitimately reaches the browser via
/// `extension.middle_tier_tool_response` today (by design, not a leak; scrubbing
/// `response.done`'s embedded function_call args is tracked separately as out-of-scope "F2");
/// and the rehydration item's embedded order JSON / recent-conversation history is excluded for
/// the same reason -- both duplicate data the browser already legitimately has (its own order
/// state, its own earlier conversation.item frames) -- only its preamble prose is captured.
///
/// Mutation: disabling PR #30 follow-up "M1" (the `conversation.item.done` /
/// `conversation.item.retrieved` drop case in `rtmt.py`) makes this fail because the rehydration
/// and/or nudge item's text leaks via its `.done` echo. Disabling "S1" (the `sonic_mt_`-prefixed
/// `item.id` authorship check in `_drop_from_client`, leaving only the `role == "system"`
/// backstop) makes this fail specifically via the greeting item, since it is the one middle-tier
/// item that is `role: "user"` and therefore not caught by the backstop alone -- the one proof no
/// other test in this suite can provide.
///
/// Runs under <see cref="BackendProfiles.ShortTimers"/> so the resume grace hold and nudge timer
/// fit in a fast test.
/// </summary>
[Collection(ShortTimersConformanceCollection.Name)]
public sealed class WholeSessionLeakTests(ShortTimersConformanceFixture fixture)
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
        // Only the preamble prose is captured as a secret -- build_rehydration_item also embeds
        // the guest's own current order (JSON) and recent transcript, both of which are
        // legitimate duplicates of data the browser already has via its own order state and
        // earlier conversation.item frames, so including them here would make this test flag
        // that expected (non-leak) overlap as a false positive.
        AddSecretWindows(RehydrationPreamble(GreetingText(rehydrationItem!.Json.GetProperty("item"))), secretWindows);

        // ── Wait for the silence nudge (ShortTimers' ~1s timer) with a keepalive so the idle
        // sweep (pinned to the same ~1s under ShortTimers) doesn't close the session first --
        // same race and same fix as ResumeRehydrationClientVisibilityTests (PR #30 review "S3"). ──
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

    /// <summary>Extracts the plain `content[0].text` GA/legacy conversation items of this shape
    /// (greeting, rehydration and nudge items all use it) carry -- named generically because the
    /// same extraction applies to all three.</summary>
    private static string? GreetingText(JsonElement item) =>
        item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array &&
        content.GetArrayLength() > 0 && content[0].TryGetProperty("text", out var text)
            ? text.GetString()
            : null;

    /// <summary>`build_rehydration_item` (session_manager.py) is
    /// `"{preamble}\n\nCurrent order (JSON): {order_json}\n\nRecent conversation ...: {history}"`.
    /// Only the preamble is operator-only prose; the order JSON and recent-conversation history
    /// are legitimate duplicates of data the browser already has (its own order state, and its
    /// own earlier conversation.item frames), so they're excluded from the secret set here to
    /// avoid flagging that expected overlap as a false positive.</summary>
    private static string? RehydrationPreamble(string? fullText)
    {
        if (string.IsNullOrEmpty(fullText))
        {
            return fullText;
        }
        const string orderMarker = "\n\nCurrent order (JSON):";
        var index = fullText.IndexOf(orderMarker, StringComparison.Ordinal);
        return index >= 0 ? fullText[..index] : fullText;
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
