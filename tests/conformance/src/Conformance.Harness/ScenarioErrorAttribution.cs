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
/// Two calls per scenario, always in this order — <c>ConformanceFixture.RunAsync</c> makes each
/// pair a single read/write via <see cref="BeginScenario"/> and <see cref="EndScenario"/> (PR #66
/// re-review, R2), which internally drive the same two lower-level members:
/// <list type="number">
/// <item><see cref="ChargeStrandedErrorsToPreviousScenario"/> (via <see cref="BeginScenario"/>) —
/// right after THIS scenario's own pre-body quiescence drain, using the SAME single count read
/// that becomes this scenario's own baseline. Anything that arrived since the PREVIOUS scenario's
/// own post-body check is charged against that scenario's leftover allowance instead of being
/// silently folded into this scenario's baseline.</item>
/// <item><see cref="RecordScenarioChecked"/> (via <see cref="EndScenario"/>) — after THIS
/// scenario's own post-body check (whether it passed or the scenario failed for any reason), using
/// the SAME single count read that check was compared against, so the NEXT scenario's step 1 call
/// has an accurate watermark and allowance to charge against.</item>
/// </list>
///
/// Before this existed, <c>ConformanceFixture.RunAsync</c> drained for quiescence only once, right
/// before capturing each scenario's baseline — which closed the common case (PR #66's own #62 fix)
/// but meant a line landing so late it missed even that drain's idle window would be silently
/// absorbed into whichever scenario ran next's baseline, with nobody ever reporting it (Rick's PR
/// #66 review, M1 — exactly the "silently hide a real new backend error" hole PR #28 N13 closed
/// for the dump-buffer-wraparound case). The first revision of that M1 fix then re-read the count a
/// second time at each of the two checkpoints above instead of reusing the first read, and didn't
/// consume what it charged when the charge failed — see the re-review notes on
/// <see cref="ChargeStrandedErrorsToPreviousScenario"/>, <see cref="BeginScenario"/>, and
/// <see cref="EndScenario"/> for what that broke and how it's fixed here.
/// </summary>
public sealed class ScenarioErrorAttribution
{
    /// <summary>
    /// Pseudo-scenario name <see cref="Conformance.Tests.ConformanceFixture.InitializeAsync"/>
    /// seeds this attribution with, once, right after the shared backend process finishes
    /// starting and before any real scenario runs (PR #66 re-review, R1(c)) — so a stray error
    /// line landing in the narrow gap between backend-ready and the very first scenario's own
    /// pre-body charge is attributed to a clearly labelled pseudo scenario instead of the generic
    /// <see cref="UnknownScenarioName"/> fallback below. Combined with
    /// <see cref="ChargeStrandedErrorsToPreviousScenario"/> now consuming what it charges (R1(a)),
    /// this fails at most once — it never cascades into every scenario in the collection the way
    /// an un-seeded, un-consumed startup-time charge used to (Rick's PR #66 re-review, R1: "A
    /// single backend-startup error line would fail every scenario as '&lt;unknown scenario&gt;'").
    /// </summary>
    public const string StartupScenarioName = "<backend startup, before any scenario ran>";

    /// <summary>
    /// Name used when a stranded error is charged against a scenario boundary that was never
    /// actually recorded by name — should not be reachable in production once
    /// <see cref="Conformance.Tests.ConformanceFixture"/> always seeds via
    /// <see cref="StartupScenarioName"/> before the first scenario runs, but kept as a defensive
    /// fallback for any other caller of this class that doesn't.
    /// </summary>
    private const string UnknownScenarioName = "<unknown scenario>";

    private int _lastCheckedCount;
    private int _lastUnusedAllowance;
    private string? _lastScenarioName;
    private bool _hasHistory;

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
        if (!_hasHistory)
        {
            // #66 re-review, R1(c): nothing has EVER been recorded on this instance — there is no
            // real "previous scenario" to charge anything to yet. A backend that already logged a
            // nonzero unhandled-error count by the time this first-ever call happens would
            // otherwise look "stranded since the beginning of time" against the default zero
            // watermark and misfire, naming the meaningless <see cref="UnknownScenarioName"/> —
            // silently adopt whatever is observed now as the starting watermark instead, exactly
            // once. In production this branch is normally pre-empted by ConformanceFixture's own
            // explicit <see cref="StartupScenarioName"/> seed (which itself sets `_hasHistory`),
            // so this is a defense-in-depth fallback for this class used any other way.
            _lastCheckedCount = currentCount;
            _hasHistory = true;
            return null;
        }

        var stranded = currentCount - _lastCheckedCount;
        if (stranded <= 0)
        {
            return null;
        }

        // #66 re-review, R1(a): consume what we charge in EVERY branch below, whether it's fully
        // absorbed by the previous scenario's leftover allowance or not — otherwise the NEXT
        // scenario's own charge would see this exact same stranded delta all over again and
        // re-charge (or re-fail against) the very same already-reported line(s), cascading one
        // late error into a failure for every remaining scenario in the collection.
        _lastCheckedCount = currentCount;

        if (stranded <= _lastUnusedAllowance)
        {
            _lastUnusedAllowance -= stranded;
            return null;
        }

        var absorbedByAllowance = _lastUnusedAllowance;
        var unaccountedFor = stranded - absorbedByAllowance;
        var previousScenario = _lastScenarioName ?? UnknownScenarioName;
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
        _hasHistory = true;
    }

    /// <summary>
    /// #66 re-review, R2: begins a scenario boundary by reading the current unhandled-error count
    /// via <paramref name="readUnhandledErrorCount"/> EXACTLY ONCE, then reuses that single value
    /// both to charge anything stranded since the previous scenario's own check
    /// (<see cref="ChargeStrandedErrorsToPreviousScenario"/>) and as this scenario's own returned
    /// baseline — so a caller can never accidentally take a second, possibly different, read for
    /// the baseline the way the pre-fix <c>ConformanceFixture.RunAsync</c> did (the charge read
    /// the count once, then the baseline re-read it immediately after — a line landing in that gap
    /// was neither charged to the previous scenario nor counted in this scenario's own baseline,
    /// silently lost).
    /// </summary>
    public (int Baseline, string? StrandedMessage) BeginScenario(Func<int> readUnhandledErrorCount)
    {
        var currentCount = readUnhandledErrorCount();
        return (currentCount, ChargeStrandedErrorsToPreviousScenario(currentCount));
    }

    /// <summary>
    /// #66 re-review, R2: ends a scenario boundary the same way — one read via
    /// <paramref name="readUnhandledErrorCount"/>, reused both to check <paramref name="baseline"/>
    /// plus <paramref name="allowedNewBackendErrors"/> against it (the returned
    /// <c>WithinBound</c>) and to record the watermark/unused allowance the NEXT scenario's own
    /// <see cref="BeginScenario"/> charges against, via <see cref="RecordScenarioChecked"/>. The
    /// pre-fix code re-read the count a second time in a <c>finally</c> block for exactly this
    /// record, which could observe one more stray line than the check just above it saw and
    /// silently drop it — neither charged nor checked. Only call this once the scenario's body,
    /// connection teardown, and handler-fault check have already completed without throwing —
    /// a scenario that never reaches that point has nothing to read here and must be recorded via
    /// <see cref="RecordScenarioChecked"/> directly instead (see <c>ConformanceFixture.RunAsync</c>'s
    /// own <c>finally</c>).
    /// </summary>
    public (int Actual, bool WithinBound) EndScenario(
        string scenarioName, Func<int> readUnhandledErrorCount, int baseline, int allowedNewBackendErrors)
    {
        var actual = readUnhandledErrorCount();
        var withinBound = actual <= baseline + allowedNewBackendErrors;
        var unusedAllowance = withinBound ? allowedNewBackendErrors - (actual - baseline) : 0;
        RecordScenarioChecked(scenarioName, actual, unusedAllowance);
        return (actual, withinBound);
    }

    /// <summary>
    /// Records a scenario that never reached its own <see cref="EndScenario"/> call at all — it
    /// threw before then (its own body, connection teardown, or handler-fault check), or its
    /// <see cref="BeginScenario"/> charge itself failed. Takes exactly ONE read via
    /// <paramref name="readUnhandledErrorCount"/> — there is nothing else to reuse it against in
    /// this branch, so there is no double-read to avoid here, unlike <see cref="EndScenario"/>,
    /// which the caller must not ALSO call for the same scenario — and records it with zero unused
    /// allowance, since this scenario never got far enough to know how much of its own allowance
    /// it would have used.
    /// </summary>
    public int RecordScenarioFailed(string scenarioName, Func<int> readUnhandledErrorCount)
    {
        var countNow = readUnhandledErrorCount();
        RecordScenarioChecked(scenarioName, countNow, unusedAllowance: 0);
        return countNow;
    }
}
