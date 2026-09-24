using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// #8: rtmt.py's <c>_NON_REASONING_DEPLOYMENT_RE</c> classifies whether `reasoning` may be sent
/// upstream from the deployment name alone (when <c>model.reasoning_model</c> is "auto", the
/// config.yaml default) — see app/backend/rtmt.py's <c>deployment_supports_reasoning</c> and
/// app/backend/tests/test_session_bootstrap.py's ReasoningAndTranscriptionConfigTests for the
/// behaviour being ported. AZURE_OPENAI_REALTIME_DEPLOYMENT is read once at Python module-import
/// time, so — exactly like <see cref="BackendProfileFixtures"/>'s ShortTimers/FixedClock — each
/// distinct deployment name needs its own dedicated xUnit collection/backend process.
///
/// <see cref="BackendContract.DefaultDeployment"/> ("gpt-realtime-2.1-conformance") is already a
/// reasoning-capable name, so the plain <see cref="ConformanceFixture"/>/<see cref="ConformanceCollection"/>
/// already covers the "reasoning sent for gpt-realtime-2.1" case — no new fixture needed for it.
/// </summary>
public sealed class Gpt21DzConformanceFixture : ConformanceFixture
{
    protected override string? Deployment => "gpt-realtime-2.1-dz-conformance";
}

[CollectionDefinition(Name)]
public sealed class Gpt21DzConformanceCollection : ICollectionFixture<Gpt21DzConformanceFixture>
{
    public const string Name = "ConformanceGpt21Dz";
}

/// <summary>
/// gpt-realtime-1.5-style deployments are NOT reasoning-capable by name (they answer `reasoning`
/// with `invalid_value` and drop the whole session.update, tools included).
/// </summary>
public sealed class Gpt15ConformanceFixture : ConformanceFixture
{
    protected override string? Deployment => "gpt-realtime-1.5-conformance";
}

[CollectionDefinition(Name)]
public sealed class Gpt15ConformanceCollection : ICollectionFixture<Gpt15ConformanceFixture>
{
    public const string Name = "ConformanceGpt15";
}

/// <summary>
/// Forces `reasoning_model=true` via AZURE_OPENAI_REALTIME_REASONING_MODEL on an otherwise
/// non-reasoning-by-name (1.5) deployment — rtmt.py's <c>RTMiddleTier._reasoning_model()</c>
/// documents that an explicit `reasoning_model` switch always beats the deployment-name check.
/// Forcing reasoning onto a deployment that actually rejects it also naturally exercises the
/// session.update rejection → single minimal fallback path (see SessionUpdateFallbackTests.cs),
/// since this fake's <see cref="Conformance.Fakes.GaSessionValidator"/> rejects `reasoning` for
/// any deployment name containing "1.5", matching real gpt-realtime-1.5 behaviour.
/// </summary>
public sealed class Gpt15ForcedReasoningConformanceFixture : ConformanceFixture
{
    protected override string? Deployment => "gpt-realtime-1.5-conformance";

    protected override BackendProfile Profile { get; } = new(
        "Gpt15ForcedReasoning",
        new Dictionary<string, string> { ["AZURE_OPENAI_REALTIME_REASONING_MODEL"] = "true" });
}

[CollectionDefinition(Name)]
public sealed class Gpt15ForcedReasoningConformanceCollection : ICollectionFixture<Gpt15ForcedReasoningConformanceFixture>
{
    public const string Name = "ConformanceGpt15ForcedReasoning";
}

/// <summary>
/// PR #42 review item 10: the tri-state's third value (explicit `false`) had no coverage at all --
/// only "auto" (name-based, <see cref="ConformanceFixture"/>/<see cref="Gpt21DzConformanceFixture"/>
/// /<see cref="Gpt15ConformanceFixture"/>) and explicit `true` (<see cref="Gpt15ForcedReasoningConformanceFixture"/>)
/// were exercised. Forces AZURE_OPENAI_REALTIME_REASONING_MODEL=false onto
/// <see cref="BackendContract.DefaultDeployment"/> itself -- a reasoning-capable-by-name (2.1)
/// deployment that would otherwise send `reasoning` by the "auto" default -- proving the explicit
/// switch beats the deployment-name check in the OFF direction too, not just the ON direction.
/// </summary>
public sealed class Gpt21ReasoningSwitchOffConformanceFixture : ConformanceFixture
{
    protected override BackendProfile Profile { get; } = new(
        "Gpt21ReasoningSwitchOff",
        new Dictionary<string, string> { ["AZURE_OPENAI_REALTIME_REASONING_MODEL"] = "false" });
}

[CollectionDefinition(Name)]
public sealed class Gpt21ReasoningSwitchOffConformanceCollection : ICollectionFixture<Gpt21ReasoningSwitchOffConformanceFixture>
{
    public const string Name = "ConformanceGpt21ReasoningSwitchOff";
}
