using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// #8 (PR #42 review item 6): <c>extension.set_voice</c> used to mutate rtmt.py's
/// <c>RTMiddleTier.voice_choice</c>, a *process-wide* attribute shared by every connection the
/// same backend process serves. Rick filed this as #43 ("Voice picker is process-wide: one
/// guest's voice choice changes every other guest's voice"). PR #49 review round 5 ("S1") fixed
/// half of it (an already-open, unrelated connection no longer re-reads a shared field) but left
/// a process-wide <c>self._voice_override</c> field that every subsequent NEW connection's
/// bootstrap still read -- so guest A's pick still became guest B's, C's, ... default. Round 6
/// removes that field entirely: a pick is stored per session_id on <c>self._sessions</c> (never
/// on <c>RTMiddleTier</c>), read back only for that SAME session on resume (see
/// <c>Resumed_connection_restores_the_picked_voice</c> below), and never consulted for any other,
/// brand-new connection's bootstrap (see
/// <c>Voice_picked_after_lock_does_not_carry_to_a_brand_new_unrelated_connection</c>).
///
/// This dedicated collection/backend process is kept anyway: several scenarios below still pick
/// a voice and assert on bootstrap defaults, and running them alongside every other Default-
/// collection scenario (which pins <see cref="BackendContract.DefaultVoice"/> for its own
/// unrelated assertions) would make run order matter for no real benefit, exactly like the
/// reasoning-by-deployment fixtures in <see cref="ReasoningDeploymentFixtures"/>.
/// </summary>
public sealed class VoicePickerConformanceFixture : ConformanceFixture
{
}

[CollectionDefinition(Name)]
public sealed class VoicePickerConformanceCollection : ICollectionFixture<VoicePickerConformanceFixture>
{
    public const string Name = "ConformanceVoicePicker";
}
