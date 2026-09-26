namespace Conformance.Harness;

/// <summary>
/// Attributes unhandled-backend-error counts to the scenario that actually caused them, even when
/// the count only crosses because a stderr line lands asynchronously after that scenario's own
/// post-body check already ran (PR #66 M1). Extracted out of
/// <see cref="Conformance.Tests.ConformanceFixture.RunAsync(Func{Task}, int)"/> as a small, pure,
/// independently-testable bookkeeping seam — everything it needs is passed in as plain ints and a
/// name, so it never has to spin up (or race) a real backend process to be exercised
/// deterministically.
///
/// Two calls per scenario, always in this order:
/// <list type="number">
/// <item><see cref="ChargeStrandedErrorsToPreviousScenario"/> — right after THIS scenario's own
/// pre-body quiescence drain, before capturing its baseline. Anything that arrived since the
/// PREVIOUS scenario's own post-body check is charged against that scenario's leftover allowance
/// instead of being silently folded into this scenario's baseline.</item>
/// <item><see cref="RecordScenarioChecked"/> — after THIS scenario's own post-body check (whether
/// it passed or the scenario failed for any reason), so the NEXT scenario's step 1 call has an
/// accurate watermark and allowance to charge against.</item>
/// </list>
///
/// Before this existed, <c>ConformanceFixture.RunAsync</c> drained for quiescence only once, right
/// before capturing each scenario's baseline — which closed the common case (PR #66's own #62 fix)
/// but meant a line landing so late it missed even that drain's idle window would be silently
/// absorbed into whichever scenario ran next's baseline, with nobody ever reporting it (Rick's PR
/// #66 review, M1 — exactly the "silently hide a real new backend error" hole PR #28 N13 closed
/// for the dump-buffer-wraparound case).
/// </summary>
public sealed class ScenarioErrorAttribution
{
    private int _lastCheckedCount;
    private int _lastUnusedAllowance;
    private string? _lastScenarioName;

    /// <summary>
    /// <paramref name="currentCount"/> is the unhandled-error count observed right after this
    /// scenario's own pre-body quiescence drain. Returns null when there is nothing to charge (no
    /// new errors landed since the previous scenario's own check, or they fit inside its leftover
    /// allowance — which this call consumes, so a THIRD scenario can't charge the same stranded
    /// line(s) again); otherwise returns a ready-to-surface failure message naming the previous
    /// scenario and the exact counts involved, and consumes the rest of that scenario's allowance
    /// too (there is nothing left over for it to protect against a second, later charge).
    /// </summary>
    public string? ChargeStrandedErrorsToPreviousScenario(int currentCount)
    {
        var stranded = currentCount - _lastCheckedCount;
        if (stranded <= 0)
        {
            return null;
        }

        if (stranded <= _lastUnusedAllowance)
        {
            _lastUnusedAllowance -= stranded;
            return null;
        }

        var absorbedByAllowance = _lastUnusedAllowance;
        var unaccountedFor = stranded - absorbedByAllowance;
        var previousScenario = _lastScenarioName ?? "<unknown scenario>";
        _lastUnusedAllowance = 0;
        return $"{stranded} unhandled backend error(s) landed after scenario '{previousScenario}' " +
               $"finished its own check ({absorbedByAllowance} of its unused allowance covers some " +
               $"of that, leaving {unaccountedFor} unaccounted for) -- attributing them to " +
               $"'{previousScenario}' instead of silently folding them into whatever scenario runs " +
               "next.";
    }

    /// <summary>
    /// Called once per scenario, after its own post-body check (whether that check passed or the
    /// scenario failed for any other reason). <paramref name="checkedAgainstCount"/> is the exact
    /// unhandled-error count this scenario's own check was compared against;
    /// <paramref name="unusedAllowance"/> is how much of its <c>allowedNewBackendErrors</c> budget
    /// it did NOT end up using (0 or negative if the scenario failed before establishing that, or
    /// its own check failed — clamped to 0, meaning nothing is left over for a later scenario to
    /// draw on).
    /// </summary>
    public void RecordScenarioChecked(string scenarioName, int checkedAgainstCount, int unusedAllowance)
    {
        _lastScenarioName = scenarioName;
        _lastCheckedCount = checkedAgainstCount;
        _lastUnusedAllowance = Math.Max(0, unusedAllowance);
    }
}
