using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// #8: `reasoning` is sent in the bootstrap session.update for reasoning-capable deployment names
/// (gpt-realtime-2/2.1[-dz]) and never for gpt-realtime-1.5[-dz] — see
/// app/backend/rtmt.py's <c>_build_session</c>/<c>reasoning_enabled</c>/<c>deployment_supports_reasoning</c>
/// and app/backend/tests/test_session_bootstrap.py's ReasoningAndTranscriptionConfigTests, whose
/// golden values (config.yaml's <c>reasoning_effort: "low"</c>, <c>reasoning_model: "auto"</c>)
/// are ported here. Each deployment name gets its own dedicated fixture/collection (its own
/// Python process) — see Scenarios/Sessions/ReasoningDeploymentFixtures.cs.
/// </summary>
public static class ReasoningByDeploymentTestHelpers
{
    public static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    public static async Task<System.Text.Json.JsonElement> ConnectAndGetBootstrapSessionAsync(
        ConformanceFixture fixture, CancellationToken ct)
    {
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        var bootstrap = await connection!.ReceivedFrames.WaitForAsync(f => f.Sequence == 0, FrameTimeout, ct);
        Assert.True(bootstrap is not null, "Bootstrap session.update never arrived.");
        Assert.Equal("session.update", bootstrap!.Type);
        return bootstrap.Json.GetProperty("session");
    }
}

[Collection(ConformanceCollection.Name)]
public sealed class ReasoningSentForDefaultDeploymentTests(ConformanceFixture fixture)
{
    [Fact]
    public Task Reasoning_effort_is_sent_in_the_bootstrap_for_the_default_reasoning_capable_deployment() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var session = await ReasoningByDeploymentTestHelpers.ConnectAndGetBootstrapSessionAsync(fixture, ct);

        Assert.True(session.TryGetProperty("reasoning", out var reasoning),
            $"Expected `reasoning` on the bootstrap session.update for deployment " +
            $"'{BackendContract.DefaultDeployment}' (reasoning-capable by name).");
        Assert.Equal("low", reasoning.GetProperty("effort").GetString());
    });
}

[Collection(Gpt21DzConformanceCollection.Name)]
public sealed class ReasoningSentForDzDeploymentTests(Gpt21DzConformanceFixture fixture)
{
    [Fact]
    public Task Reasoning_effort_is_sent_in_the_bootstrap_for_a_gpt_realtime_2_1_dz_deployment() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var session = await ReasoningByDeploymentTestHelpers.ConnectAndGetBootstrapSessionAsync(fixture, ct);

        Assert.True(session.TryGetProperty("reasoning", out var reasoning),
            "Expected `reasoning` on the bootstrap session.update for a gpt-realtime-2.1-dz deployment.");
        Assert.Equal("low", reasoning.GetProperty("effort").GetString());
    });
}

[Collection(Gpt15ConformanceCollection.Name)]
public sealed class ReasoningNeverSentForGpt15DeploymentTests(Gpt15ConformanceFixture fixture)
{
    [Fact]
    public Task Reasoning_is_never_sent_in_the_bootstrap_for_a_gpt_realtime_1_5_deployment() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var session = await ReasoningByDeploymentTestHelpers.ConnectAndGetBootstrapSessionAsync(fixture, ct);

        Assert.False(session.TryGetProperty("reasoning", out _),
            "gpt-realtime-1.5 rejects `reasoning` and drops the whole session.update -- rtmt.py " +
            "must never send it for a 1.5-named deployment in the first place.");
    });
}
