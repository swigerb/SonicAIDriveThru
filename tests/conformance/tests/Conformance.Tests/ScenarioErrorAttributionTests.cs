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
        // No RecordScenarioChecked has ever run on either instance below -- _lastCheckedCount
        // defaults to 0. The trivial currentCount: 0 case always trivially returned null even
        // before PR #66's re-review (0 - 0 = 0 stranded, nothing to charge) -- it is the NONZERO
        // case that actually exercises the fix and previously misfired (Rick's PR #66 re-review,
        // R1(c) -- this test "claims a nonzero starting count must not misfire, but it only
        // asserts count 0"): a backend that logged even one unhandled error during its own
        // startup, before any scenario existed to charge it to, would look "stranded since the
        // beginning of time" against the default zero watermark and fail, naming the meaningless
        // "<unknown scenario>". A fresh instance per case, since a real charge call establishes
        // history and would no longer exercise the "very first" branch on a second call.
        Assert.Null(new ScenarioErrorAttribution().ChargeStrandedErrorsToPreviousScenario(currentCount: 0));
        Assert.Null(new ScenarioErrorAttribution().ChargeStrandedErrorsToPreviousScenario(currentCount: 3));
    }

    [Fact]
    public void ChargeStrandedErrorsToPreviousScenario_adopts_a_nonzero_first_reading_as_its_watermark_not_just_ignoring_it()
    {
        // Companion to the test above: proves the nonzero first reading isn't merely tolerated
        // once, but genuinely becomes the new watermark going forward -- a scenario that runs next
        // and observes NO further errors must not be charged for the 3 that already existed before
        // it ran.
        var attribution = new ScenarioErrorAttribution();
        Assert.Null(attribution.ChargeStrandedErrorsToPreviousScenario(currentCount: 3));

        attribution.RecordScenarioChecked("ScenarioA", checkedAgainstCount: 3, unusedAllowance: 0);

        Assert.Null(attribution.ChargeStrandedErrorsToPreviousScenario(currentCount: 3));
    }

    /// <summary>
    /// PR #66 re-review, R1(a)/R1(b) -- the actual cascade bug: when the charge for scenario B
    /// fails (naming A), <c>ConformanceFixture.RunAsync</c>'s own <c>Assert.Fail</c> used to run
    /// BEFORE its <c>try</c>/<c>finally</c>, so B was never recorded either. B's own late
    /// pre-body charge also never advanced its watermark past the stranded line it had just
    /// reported. The next scenario (C) would then see the exact same stranded delta all over
    /// again and re-fail, naming A a second time -- and so on for every remaining scenario in the
    /// collection. This asserts the fixed sequence -- charge consumes what it charges, and
    /// <c>ConformanceFixture</c>'s own <c>finally</c> now always calls
    /// <see cref="ScenarioErrorAttribution.RecordScenarioChecked"/> (simulated here by the
    /// explicit call after each charge) -- so only B ever fails, and C/D afterward are
    /// unaffected.
    /// </summary>
    [Fact]
    public void ChargeStrandedErrorsToPreviousScenario_late_error_from_A_fails_only_the_charging_scenario_B_and_C_and_D_are_unaffected()
    {
        var attribution = new ScenarioErrorAttribution();
        attribution.RecordScenarioChecked("ScenarioA", checkedAgainstCount: 5, unusedAllowance: 0);

        // Scenario B's own pre-body charge is the first to observe the late line from A -- it
        // fails, naming A.
        var messageForB = attribution.ChargeStrandedErrorsToPreviousScenario(currentCount: 6);
        Assert.NotNull(messageForB);
        Assert.Contains("ScenarioA", messageForB);

        // ConformanceFixture's own `finally` always runs now (R1(b)), even though B's own charge
        // failed before B's body ever ran -- so B is still recorded, with no new errors of its
        // own.
        attribution.RecordScenarioChecked("ScenarioB", checkedAgainstCount: 6, unusedAllowance: 0);

        // Scenario C must be completely unaffected: R1(a)'s fix means B's own charge already
        // consumed (advanced past) the one stranded line, so there is nothing left for C to
        // re-discover.
        Assert.Null(attribution.ChargeStrandedErrorsToPreviousScenario(currentCount: 6));
        attribution.RecordScenarioChecked("ScenarioC", checkedAgainstCount: 6, unusedAllowance: 0);

        // Scenario D, too.
        Assert.Null(attribution.ChargeStrandedErrorsToPreviousScenario(currentCount: 6));
    }

    /// <summary>
    /// Narrower companion to the test above, isolating R1(a) alone: proves
    /// <see cref="ScenarioErrorAttribution.ChargeStrandedErrorsToPreviousScenario"/> itself never
    /// re-reports the same stranded delta, even with NO
    /// <see cref="ScenarioErrorAttribution.RecordScenarioChecked"/> call in between -- i.e. even
    /// in the exact shape of the original bug, where <c>ConformanceFixture</c>'s own
    /// <c>Assert.Fail</c> ran before its <c>try</c>/<c>finally</c> and the next scenario's
    /// `finally` (if it too failed before reaching its `EndScenario` call) also never recorded.
    /// </summary>
    [Fact]
    public void ChargeStrandedErrorsToPreviousScenario_does_not_recharge_the_same_stranded_errors_when_called_again_with_no_record_between()
    {
        var attribution = new ScenarioErrorAttribution();
        attribution.RecordScenarioChecked("ScenarioA", checkedAgainstCount: 5, unusedAllowance: 0);

        var firstCharge = attribution.ChargeStrandedErrorsToPreviousScenario(currentCount: 6);
        Assert.NotNull(firstCharge);
        Assert.Contains("ScenarioA", firstCharge);

        // Even with NO RecordScenarioChecked call in between, a second (and third) charge against
        // the SAME currentCount must not re-report the same one stranded line all over again.
        Assert.Null(attribution.ChargeStrandedErrorsToPreviousScenario(currentCount: 6));
        Assert.Null(attribution.ChargeStrandedErrorsToPreviousScenario(currentCount: 6));
    }

    /// <summary>
    /// PR #66 re-review, R1(c): a stray error landing in the narrow gap between
    /// <c>ConformanceFixture.InitializeAsync</c>'s own startup seed and the very first scenario's
    /// own pre-body charge must be attributed to the clearly-named startup pseudo-scenario, not
    /// the generic "&lt;unknown scenario&gt;" fallback -- and, like any other charge, must fail at
    /// most once, never cascading to later scenarios.
    /// </summary>
    [Fact]
    public void ChargeStrandedErrorsToPreviousScenario_names_the_backend_startup_pseudo_scenario_and_does_not_cascade()
    {
        var attribution = new ScenarioErrorAttribution();
        // Simulates ConformanceFixture.InitializeAsync's own seed call: the backend logged 1
        // unhandled error during its own startup, before any real scenario existed to charge it
        // to.
        attribution.RecordScenarioChecked(ScenarioErrorAttribution.StartupScenarioName, checkedAgainstCount: 1, unusedAllowance: 0);

        // One more stray line lands in the narrow gap between the seed and scenario A's own
        // pre-body charge -- this must fail, clearly naming the startup pseudo-scenario, not the
        // "<unknown scenario>" fallback (which would mean nobody ever recorded any history at
        // all).
        var messageForA = attribution.ChargeStrandedErrorsToPreviousScenario(currentCount: 2);
        Assert.NotNull(messageForA);
        Assert.Contains(ScenarioErrorAttribution.StartupScenarioName, messageForA);
        Assert.DoesNotContain("<unknown scenario>", messageForA);

        // ConformanceFixture's own `finally` always records scenario A even though its own charge
        // failed (R1(b)), with no further errors of its own.
        attribution.RecordScenarioChecked("ScenarioA", checkedAgainstCount: 2, unusedAllowance: 0);

        // Scenarios B and C must be completely unaffected -- the single startup-time error fails
        // scenario A exactly once; it does not cascade.
        Assert.Null(attribution.ChargeStrandedErrorsToPreviousScenario(currentCount: 2));
        attribution.RecordScenarioChecked("ScenarioB", checkedAgainstCount: 2, unusedAllowance: 0);
        Assert.Null(attribution.ChargeStrandedErrorsToPreviousScenario(currentCount: 2));
    }

    /// <summary>
    /// PR #66 re-review, R2: proves <see cref="ScenarioErrorAttribution.BeginScenario"/> takes
    /// exactly one reading of the unhandled-error count, not two -- a fake, incrementing count
    /// source (standing in for a real, still-live async stderr capture callback) would return a
    /// DIFFERENT value on a second call, which either this call-count assertion or the returned
    /// baseline would catch immediately if a regression reintroduced a second read.
    /// </summary>
    [Fact]
    public void BeginScenario_reads_the_unhandled_error_count_exactly_once()
    {
        var attribution = new ScenarioErrorAttribution();
        var callCount = 0;
        int ReadCount()
        {
            callCount++;
            return 10 + callCount;
        }

        var (baseline, strandedMessage) = attribution.BeginScenario(ReadCount);

        Assert.Equal(1, callCount);
        Assert.Equal(11, baseline); // the one (and only) call's own return value
        Assert.Null(strandedMessage); // no prior history to charge against
    }

    /// <summary>
    /// Same proof for <see cref="ScenarioErrorAttribution.EndScenario"/>: exactly one read, reused
    /// for both the bound check and the watermark it records.
    /// </summary>
    [Fact]
    public void EndScenario_reads_the_unhandled_error_count_exactly_once()
    {
        var attribution = new ScenarioErrorAttribution();
        var callCount = 0;
        int ReadCount()
        {
            callCount++;
            return 5 + callCount;
        }

        var (actual, withinBound) = attribution.EndScenario(
            "ScenarioA", ReadCount, baseline: 5, allowedNewBackendErrors: 1);

        Assert.Equal(1, callCount);
        Assert.Equal(6, actual); // the one (and only) call's own return value
        Assert.True(withinBound); // 6 <= 5 + 1

        // The SAME single read (6) is what gets recorded as the watermark -- not some other,
        // later value a second read might have returned.
        Assert.Null(attribution.ChargeStrandedErrorsToPreviousScenario(currentCount: 6));
    }

    /// <summary>
    /// PR #66 re-review, R2's own worked example of the hazard it closes: "a line landing between
    /// the paired reads is lost." Here a fake unhandled-error count source is advanced by the test
    /// itself, under its own control, to simulate one genuine new backend error line landing in
    /// the gap BETWEEN scenario A's own post-body check and scenario B's own pre-body charge --
    /// exactly the checkpoint boundary R2 is about. It must be caught and charged to A (not
    /// silently folded into B's own baseline as if it always existed, and not lost outright), and
    /// it must not cascade to scenario C afterward.
    /// </summary>
    [Fact]
    public void BeginScenario_and_EndScenario_together_attribute_a_line_landing_in_the_gap_between_scenarios_not_lose_it()
    {
        var attribution = new ScenarioErrorAttribution();
        var count = 0;
        int ReadCount() => count;

        // Scenario A: begins and ends with no errors at all.
        var (baselineA, strandedBeforeA) = attribution.BeginScenario(ReadCount);
        Assert.Null(strandedBeforeA);
        var (actualA, withinBoundA) = attribution.EndScenario(
            "ScenarioA", ReadCount, baselineA, allowedNewBackendErrors: 0);
        Assert.True(withinBoundA);

        // One genuine new backend error lands in the gap between A's own post-body check and B's
        // own pre-body charge.
        count = 1;

        // Scenario B's own BeginScenario is the FIRST thing to read the count after that line
        // landed -- it must see it and charge it to A.
        var (baselineB, strandedBeforeB) = attribution.BeginScenario(ReadCount);
        Assert.NotNull(strandedBeforeB);
        Assert.Contains("ScenarioA", strandedBeforeB);
        Assert.Equal(1, baselineB); // B's own baseline still reflects the real, current count --
                                     // charging it to A does not also hide it from B.

        attribution.RecordScenarioChecked("ScenarioB", checkedAgainstCount: 1, unusedAllowance: 0);

        // Scenario C must be completely unaffected -- B's own BeginScenario already consumed
        // (advanced past) the one stranded line; it must not cascade.
        var (_, strandedBeforeC) = attribution.BeginScenario(ReadCount);
        Assert.Null(strandedBeforeC);
    }
}
