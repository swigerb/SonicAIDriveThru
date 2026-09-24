using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// #8: when GA rejects one of rtmt.py's own session.updates, exactly one minimal fallback
/// session.update is sent (instructions + tools + tool_choice only — see
/// app/backend/rtmt.py's <c>_FALLBACK_SESSION_KEYS</c>/<c>build_fallback_session_update</c>),
/// the browser never sees the rejection as an `error`, and the guard never loops (a fallback is
/// claimed at most once per original — <c>_SessionUpdateGuard.claim_fallback</c>). Unrelated
/// errors (anything that isn't a session.update rejection) must never trigger a fallback either.
///
/// The only rejection reachable black-box without a fake-scripting hook (this suite's fake
/// applies its <see cref="Conformance.Fakes.GaSessionValidator"/> built-in checks unconditionally,
/// before any <c>RealtimeScript</c> rule runs) is forcing `reasoning_model=true` onto a
/// gpt-realtime-1.5-named deployment (<see cref="Gpt15ForcedReasoningConformanceFixture"/>) — the
/// bootstrap's own `reasoning` is genuinely rejected by the fake with the exact wire shape GA uses
/// for this case (`invalid_value`, no `event_id` echoed — <see cref="Conformance.Fakes.GaSessionValidator"/>
/// lines ~137-149), which forces the guard's order-based (not event_id-based) correlation path.
/// This same fixture also exercises the "reasoning_model switch" bullet: it's the explicit
/// AZURE_OPENAI_REALTIME_REASONING_MODEL override beating the deployment-name check that puts a
/// non-reasoning-by-name deployment into this state in the first place.
/// </summary>
[Collection(Gpt15ForcedReasoningConformanceCollection.Name)]
public sealed class SessionUpdateFallbackTests(Gpt15ForcedReasoningConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Rejected_reasoning_bootstrap_recovers_via_exactly_one_minimal_fallback_with_no_error_reaching_the_browser() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        var bootstrap = await connection!.ReceivedFrames.WaitForAsync(f => f.Sequence == 0, FrameTimeout, ct);
        Assert.True(bootstrap is not null, "Bootstrap session.update never arrived.");
        Assert.True(bootstrap!.Json.GetProperty("session").TryGetProperty("reasoning", out _),
            "Precondition failed: this fixture forces reasoning_model=true, so the bootstrap must " +
            "have attempted to send `reasoning` for the fallback path below to mean anything.");

        // The fake rejects it unconditionally (real GA behaviour for a 1.5-named deployment) --
        // rtmt.py should recover with exactly one fallback session.update, with no browser-side
        // interaction needed at all.
        var fallback = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence > bootstrap.Sequence && f.Type == "session.update", FrameTimeout, ct);
        Assert.True(fallback is not null, "Expected a fallback session.update after the bootstrap's reasoning was rejected.");

        var fallbackSession = fallback!.Json.GetProperty("session");
        var fallbackKeys = fallbackSession.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(["instructions", "tool_choice", "tools", "type"], fallbackKeys);
        Assert.False(fallbackSession.TryGetProperty("reasoning", out _), "Fallback must not repeat the rejected `reasoning` field.");
        Assert.False(fallbackSession.TryGetProperty("audio", out _), "Fallback must be minimal (no audio/voice).");

        // PR #42 review item 4 (M2, "fallback sends tools: []", must fail here): "minimal" means
        // the FIELD SET shrinks, not that the carhop's persona/tools do -- the fallback's tools,
        // tool_choice and instructions must be the exact same content as the bootstrap's own
        // (both come from the same process-wide RTMiddleTier.tools/system_message), not merely
        // present-and-non-empty.
        var bootstrapSession = bootstrap.Json.GetProperty("session");
        var bootstrapToolNames = bootstrapSession.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var fallbackToolNames = fallbackSession.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(bootstrapToolNames);
        Assert.Equal(bootstrapToolNames, fallbackToolNames);

        Assert.Equal("auto", fallbackSession.GetProperty("tool_choice").GetString());
        Assert.Equal(bootstrapSession.GetProperty("tool_choice").GetString(), fallbackSession.GetProperty("tool_choice").GetString());

        var bootstrapInstructions = bootstrapSession.GetProperty("instructions").GetString();
        Assert.False(string.IsNullOrWhiteSpace(bootstrapInstructions), "Precondition failed: bootstrap instructions must be non-empty.");
        Assert.Equal(bootstrapInstructions, fallbackSession.GetProperty("instructions").GetString());

        // Once reasoning is rejected once, rtmt.py flips `_reasoning_rejected` process-wide, so
        // every later session.update (including the browser's own) must also omit it -- proving
        // this isn't a one-off fix-up of just the bootstrap's own payload.
        await browser.SendStartSessionAsync(cancellationToken: ct);
        var browserUpdate = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence > fallback.Sequence && f.Type == "session.update", FrameTimeout, ct);
        Assert.True(browserUpdate is not null, "The browser's session.update was never forwarded upstream.");
        Assert.False(browserUpdate!.Json.GetProperty("session").TryGetProperty("reasoning", out _),
            "Reasoning must stay switched off for the rest of the process once rejected once.");

        // PR #42 review item 7: a bare Assert.DoesNotContain(..., "error") right after the
        // upstream-side fallback frame proves nothing about the browser side -- a would-be
        // erroneous forward of the rejection is a separate async write to a different socket, not
        // ordered against the upstream fallback frame by anything. Wait for the greeting's own
        // extension.round_trip_token (a genuine browser-side sentinel: it can only be emitted
        // after the browser's session.update above was processed and its response completed) so
        // that a bug forwarding either rejection to the browser would provably have already
        // arrived in ReceivedFrames on the SAME socket, strictly before this sentinel, by the time
        // we snapshot it below.
        var roundTripToken = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(roundTripToken is not null, "extension.round_trip_token never reached the browser (greeting never completed).");

        // Neither the bootstrap's rejection nor the fallback recovery ever surfaces to the
        // browser as an `error` -- it's fully recovered upstream, invisible to useRealtime.tsx.
        Assert.DoesNotContain(browser.ReceivedFrames.Snapshot(), f => f.Type == "error");

        // No loop: exactly bootstrap + one fallback + the browser's own update -- never a second,
        // repeated fallback for the same rejection, and never an error frame anywhere in the log.
        var updateCount = connection.ReceivedFrames.Snapshot().Count(f => f.Type == "session.update");
        Assert.Equal(3, updateCount);
        Assert.DoesNotContain(connection.ReceivedFrames.Snapshot(), f => f.Type == "error" && f.Sequence > browserUpdate.Sequence);
    },
    // Exactly two deterministic backend ERROR-level log lines are this scenario's own subject
    // matter: "Upstream REJECTED session.update ..." (the rejection itself) and "Deployment ...
    // rejected reasoning-model options ..." (the process-wide reasoning-off flip it triggers).
    // Both are proven recovered above (no `error` frame reaches the browser, exactly one
    // fallback, the guard never loops) -- this is not an unexpected/unhandled error.
    allowedNewBackendErrors: 2);
}

/// <summary>
/// Errors unrelated to a session.update rejection must never be mistaken for one and trigger a
/// fallback — see <c>_SessionUpdateGuard.correlate</c>'s guard against an empty in-flight queue.
/// Runs on the plain Default deployment/profile: this is about the guard's own selectivity, not
/// about reasoning at all.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class UnrelatedErrorsDoNotTriggerFallbackTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Unrelated_response_cancel_error_never_triggers_a_session_update_fallback() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        await browser.SendStartSessionAsync(cancellationToken: ct);

        // Let the greeting round trip fully complete first -- both the bootstrap's and the
        // browser's session.updates are acknowledged by then, so the guard's in-flight queue is
        // provably empty before the unrelated error below is sent (the actual condition that
        // makes an unrelated error safe to ignore, not merely an unrelated `param`).
        var roundTripToken = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(roundTripToken is not null, "extension.round_trip_token never reached the browser (greeting never completed).");

        var updateCountBefore = connection!.ReceivedFrames.Snapshot().Count(f => f.Type == "session.update");

        // Nothing is active on this connection right now (the greeting's response already
        // completed) -- response.cancel here is rejected as an unrelated, ordinary error.
        await browser.SendResponseCancelAsync(cancellationToken: ct);
        var error = await browser.ReceivedFrames.WaitForAsync(f => f.Type == "error", FrameTimeout, ct);
        Assert.True(error is not null, "Expected the response.cancel-with-nothing-active error to reach the browser.");

        // Proves it was relayed straight through, not recovered/consumed as a session.update
        // rejection: no new session.update was ever sent upstream because of it.
        var updateCountAfter = connection.ReceivedFrames.Snapshot().Count(f => f.Type == "session.update");
        Assert.Equal(updateCountBefore, updateCountAfter);
    },
    // rtmt.py's "error" case logs this unrelated OpenAI Realtime API error at ERROR level before
    // relaying it -- deterministic, and proven above to be a plain relay (no fallback triggered),
    // not an unexpected/unhandled error.
    allowedNewBackendErrors: 1);
}

/// <summary>
/// #8 (PR #42 review item 3): the guard's no-second-fallback loop guard was previously untested
/// black-box -- the only reachable rejection (<see cref="SessionUpdateFallbackTests"/>, above) can
/// only ever reject once per connection, so a fallback that is ITSELF rejected (which is exactly
/// what should stop the guard from chaining a second, third, ... fallback forever) was never
/// exercised. <see cref="Conformance.Fakes.FakeRealtimeUpstreamServer.RejectNextSessionUpdates"/>
/// scripts an arbitrary rejection independent of GA's built-in rules, so both the bootstrap's
/// session.update AND its own fallback can be forced to fail here. Uses <c>echoEventId: true</c>
/// (the reasoning fixture's built-in rejection omits it) to additionally prove
/// <c>_SessionUpdateGuard.correlate</c>'s event_id-keyed path, not just its order-based fallback.
/// Uses <c>echoEventId: true</c>
/// (the reasoning fixture's built-in rejection omits it) to additionally prove
/// <c>_SessionUpdateGuard.correlate</c>'s event_id-keyed path, not just its order-based fallback.
/// Deliberately runs on <see cref="Gpt15ConformanceFixture"/> (never sends `reasoning`), not the
/// plain Default deployment: this is about the guard's loop protection alone, and the Default
/// deployment's bootstrap already sends `reasoning` by default, which would make the scripted
/// rejection here ALSO flip rtmt.py's process-wide `_reasoning_rejected` latch — a real,
/// observable side effect this test must not cause, since <see cref="ConformanceCollection"/> is
/// shared by every other Default-deployment scenario in the suite.
/// </summary>
[Collection(Gpt15ConformanceCollection.Name)]
public sealed class SecondSessionUpdateRejectionLoopGuardTests(Gpt15ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Rejecting_the_fallback_itself_sends_no_second_fallback_and_the_error_reaches_the_browser() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        // Reject the next TWO session.update frames this server sees, on whichever connection
        // sends them: the bootstrap (triggering the first, expected fallback) and then that very
        // fallback (which must NOT trigger a second one).
        fixture.Realtime.RejectNextSessionUpdates(2, code: "invalid_value", echoEventId: true);

        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        var bootstrap = await connection!.ReceivedFrames.WaitForAsync(f => f.Sequence == 0, FrameTimeout, ct);
        Assert.True(bootstrap is not null, "Bootstrap session.update never arrived.");

        var fallback = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence > bootstrap.Sequence && f.Type == "session.update", FrameTimeout, ct);
        Assert.True(fallback is not null, "Expected exactly one fallback after the bootstrap's scripted rejection.");

        // PR #42 review item 7: don't assert "no error reached the browser yet" here -- the
        // fallback frame above is an upstream-side milestone and proves nothing about whether a
        // buggy premature forward of the bootstrap's rejection has landed on the browser's
        // separate socket yet. The bootstrap's rejection recovering silently is instead proven
        // below by the browserErrorCount == 1 assertion, anchored on the sentinel round trip.

        // The fallback itself was ALSO scripted-rejected. If the guard looped (M1: "rejected
        // fallback triggers another fallback"), it would send a THIRD session.update instead of
        // ever letting this second rejection reach the browser -- assert the opposite of that.
        var error = await browser.ReceivedFrames.WaitForAsync(f => f.Type == "error", FrameTimeout, ct);
        Assert.True(error is not null, "Expected the fallback's own rejection to reach the browser once the guard refuses to loop.");
        Assert.Equal("invalid_value", error!.Json.GetProperty("error").GetProperty("code").GetString());

        // echoEventId: true was requested -- the fallback's own event_id must be the one echoed
        // back, proving the guard correlated this rejection to the fallback specifically (not
        // just "the oldest thing in flight", which the order-based path would also get right).
        var fallbackEventId = fallback!.Json.GetProperty("event_id").GetString();
        Assert.Equal(fallbackEventId, error.Json.GetProperty("error").GetProperty("event_id").GetString());

        // Sentinel round trip: our scripted-rejection budget (2) is now exhausted, so the
        // browser's OWN session.update (the third session.update frame overall) must be accepted
        // normally and the greeting must complete -- proving forward progress, and that nothing
        // async is still queued to send a belated extra fallback.
        await browser.SendStartSessionAsync(cancellationToken: ct);
        var roundTripToken = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(roundTripToken is not null, "extension.round_trip_token never reached the browser -- the greeting never completed after the scripted-rejection budget was exhausted.");

        // No loop: exactly bootstrap + one fallback + the browser's own update -- never a third,
        // looped session.update for the fallback's own rejection.
        var updateCount = connection.ReceivedFrames.Snapshot().Count(f => f.Type == "session.update");
        Assert.Equal(3, updateCount);

        // Exactly one `error` ever reached the browser (the fallback's own rejection) -- the
        // bootstrap's original rejection stayed fully invisible to it.
        var browserErrorCount = browser.ReceivedFrames.Snapshot().Count(f => f.Type == "error");
        Assert.Equal(1, browserErrorCount);
    },
    // Three deterministic backend ERROR-level log lines are this scenario's own subject matter:
    // "Upstream REJECTED session.update ..." (recovering the bootstrap's rejection), "Fallback
    // session.update ... was ALSO rejected ..." (refusing to loop), and the generic "OpenAI
    // Realtime API error: ..." fall-through log for the second rejection once it's let through to
    // the browser. All three are proven-expected above, not unhandled. (Unlike
    // SessionUpdateFallbackTests, gpt-realtime-1.5 never sends `reasoning`, so there's no fourth
    // "rejected reasoning-model options" log line here.)
    allowedNewBackendErrors: 3);
}
