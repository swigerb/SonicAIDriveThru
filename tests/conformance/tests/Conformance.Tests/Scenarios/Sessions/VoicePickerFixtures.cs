using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// #8 (PR #42 review item 6): <c>extension.set_voice</c> mutates rtmt.py's
/// <c>RTMiddleTier.voice_choice</c>, which is a *process-wide* attribute shared by every
/// connection the same backend process serves (rtmt.py's <c>extension.set_voice</c> handler
/// updates it unconditionally, with no per-connection or per-session scoping at all) — Rick
/// filed this as a genuine Python bug, #43 ("Voice picker is process-wide: one guest's voice
/// choice changes every other guest's voice"), not something this conformance suite should fix
/// or paper over.
///
/// That process-wide scope does mean any test that calls <c>extension.set_voice</c> permanently
/// pollutes every *other* test sharing the same backend process for the rest of the run — see
/// <see cref="SessionBootstrapGaShapeTests"/>'s and <see cref="VoiceLockTests"/>'s prior
/// "presence, not pinned value" comments, both a direct symptom of this. So the voice-picker
/// scenarios below need their own dedicated collection/backend process, exactly like the
/// reasoning-by-deployment fixtures in <see cref="ReasoningDeploymentFixtures"/>, to keep
/// <see cref="ConformanceCollection"/>'s <see cref="BackendContract.DefaultVoice"/> pinning
/// meaningful for every other scenario in the Default collection.
/// </summary>
public sealed class VoicePickerConformanceFixture : ConformanceFixture
{
}

[CollectionDefinition(Name)]
public sealed class VoicePickerConformanceCollection : ICollectionFixture<VoicePickerConformanceFixture>
{
    public const string Name = "ConformanceVoicePicker";
}
