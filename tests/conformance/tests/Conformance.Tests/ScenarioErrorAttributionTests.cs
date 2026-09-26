using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Pure, no-process unit tests for <see cref="ScenarioErrorAttribution"/> (PR #66 M1/M2) --
/// everything it needs is plain ints and a name, so these can be deterministic and instantaneous
/// without spinning up a real backend process. See <see cref="CapturedProcessOutputTests"/> and
/// the new <c>CapturedProcessOutputWaitTests</c> for the process-driven fixture-level coverage
/// that wires this class up against the real async stderr-capture path.
/// </summary>
public sealed class ScenarioErrorAttributionTests
{
    [Fact]
    public void ChargeStrandedErrorsToPreviousScenario_returns_null_when_nothing_new_landed()
    {
        var attribution = new ScenarioErrorAttribution();
        attribution.RecordScenarioChecked("ScenarioA", checkedAgainstCount: 5, unusedAllowance: 0);

        Assert.Null(attribution.ChargeStrandedErrorsToPreviousScenario(currentCount: 5));
    }

    [Fact]
    public void ChargeStrandedErrorsToPreviousScenario_silently_absorbs_within_the_previous_scenarios_unused_allowance()
    {
        var attribution = new ScenarioErrorAttribution();
        // ScenarioA passed with allowedNewBackendErrors: 2 but only used 1 of them -- 1 unused
        // allowance left over.
        attribution.RecordScenarioChecked("ScenarioA", checkedAgainstCount: 5, unusedAllowance: 1);

        // One more error landed late, but it fits inside ScenarioA's leftover allowance.
        Assert.Null(attribution.ChargeStrandedErrorsToPreviousScenario(currentCount: 6));
    }

    [Fact]
    public void ChargeStrandedErrorsToPreviousScenario_fails_naming_the_previous_scenario_when_allowance_is_exhausted()
    {
        var attribution = new ScenarioErrorAttribution();
        attribution.RecordScenarioChecked("GreetingTimeoutFallbackTests.Greeting_still_fires", checkedAgainstCount: 5, unusedAllowance: 0);

        var message = attribution.ChargeStrandedErrorsToPreviousScenario(currentCount: 6);

        Assert.NotNull(message);
        Assert.Contains("GreetingTimeoutFallbackTests.Greeting_still_fires", message);
        Assert.Contains("1", message); // the 1 stranded error
    }

    [Fact]
    public void ChargeStrandedErrorsToPreviousScenario_reports_the_unaccounted_for_remainder_when_allowance_only_partially_covers_it()
    {
        var attribution = new ScenarioErrorAttribution();
        attribution.RecordScenarioChecked("ScenarioA", checkedAgainstCount: 10, unusedAllowance: 1);

        // 3 stranded errors landed; only 1 is covered by ScenarioA's leftover allowance.
        var message = attribution.ChargeStrandedErrorsToPreviousScenario(currentCount: 13);

        Assert.NotNull(message);
        Assert.Contains("ScenarioA", message);
        Assert.Contains("2", message); // 2 unaccounted for after the 1-error allowance is spent
    }

    [Fact]
    public void ChargeStrandedErrorsToPreviousScenario_never_double_charges_the_same_stranded_errors_twice()
    {
        var attribution = new ScenarioErrorAttribution();
        attribution.RecordScenarioChecked("ScenarioA", checkedAgainstCount: 5, unusedAllowance: 2);

        // ScenarioB's own pre-body charge absorbs 1 stranded error into ScenarioA's allowance.
        Assert.Null(attribution.ChargeStrandedErrorsToPreviousScenario(currentCount: 6));

        // ScenarioB finishes, itself unrelated, with no leftover allowance of its own.
        attribution.RecordScenarioChecked("ScenarioB", checkedAgainstCount: 6, unusedAllowance: 0);

        // ScenarioC's own charge must NOT re-charge the count-6 error against ScenarioA again --
        // nothing new landed since ScenarioB's own check, so this must be null.
        Assert.Null(attribution.ChargeStrandedErrorsToPreviousScenario(currentCount: 6));
    }

    [Fact]
    public void ChargeStrandedErrorsToPreviousScenario_returns_null_on_the_very_first_scenario_with_no_prior_history()
    {
        var attribution = new ScenarioErrorAttribution();

        // No RecordScenarioChecked has ever run -- _lastCheckedCount defaults to 0. A backend
        // that starts with a nonzero error count would previously have looked "stranded" against
        // an empty history; this must not misfire for the very first scenario in a collection.
        Assert.Null(attribution.ChargeStrandedErrorsToPreviousScenario(currentCount: 0));
    }
}
