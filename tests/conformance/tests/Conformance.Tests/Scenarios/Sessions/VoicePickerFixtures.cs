using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// #8 (PR #42 review item 6): <c>extension.set_voice</c> used to mutate rtmt.py's
/// <c>RTMiddleTier.voice_choice</c>, a *process-wide* attribute shared by every connection the
/// same backend process serves. Rick filed this as #43 ("Voice picker is process-wide: one
/// guest's voice choice changes every other guest's voice") and it is now fixed (PR #49 review
/// round 5, "S1"): <c>voice_choice</c> (the config-level default) is never mutated again after
/// construction; a pick instead lands in <c>self._voice_override</c> (the sticky default for
/// future NEW connections only) plus that connection's own frozen <c>voice</c> local, which an
/// already-open, unrelated connection never re-reads.
///
/// Even so, a picked voice still changes what every *later, brand-new* connection's bootstrap
/// uses (by design -- see <c>Voice_picked_after_lock_carries_to_the_next_conversation</c> below),
/// so any test that calls <c>extension.set_voice</c> still pollutes every *other* test sharing
/// the same backend process for the rest of the run — see
/// <see cref="SessionBootstrapGaShapeTests"/>'s and <see cref="VoiceLockTests"/>'s prior
/// "presence, not pinned value" comments, both a direct symptom of this. So the voice-picker
/// scenarios below still need their own dedicated collection/backend process, exactly like the
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
