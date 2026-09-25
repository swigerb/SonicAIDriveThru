using System.Linq;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// PR #50 review (second round): modifiers travel inside item_name itself (e.g.
/// "Tots (Extra Crispy)", tools.py's `update_order`), so every menuItems.json-based lookup
/// (combo slot, sundae, category, happy-hour eligibility) must strip them before classifying --
/// otherwise a customised item silently disagrees with its own base item. Rick measured this as a
/// real regression: Cheeseburger Combo (8.49) + "Chili Cheese Tots (Extra Cheese)" was absorbing
/// for free (total 8.49) instead of charging in full (total 12.28).
///
/// Covers end-to-end, against the live backend, exactly the cases Rick's review called out:
///   - "Chili Cheese Tots (Extra Cheese)" must be CHARGED alongside a combo, never absorbed.
///   - "Tots (Extra Crispy)" (an allow-listed side, customised) must still be ABSORBED.
///   - "Cherry Limeade (Extra Cherries)" must still get the happy-hour discount (kills Rick's Y4).
///   - A customised shake must obey `_SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED` exactly like its
///     plain counterpart -- the flag is the single switch, on-menu or off, plain or customised.
/// </summary>
public sealed class CustomisedItemMenuLookupTests
{
    private const string BaseComboName = "SONIC® Cheeseburger Combo";
    private const string BaseComboSize = "standard";
    private const decimal BaseComboPrice = 8.49m;

    [Collection(HappyHourJustBeforeOpenCollection.Name)]
    public sealed class ComboSlotTests(HappyHourJustBeforeOpenFixture fixture)
    {
        [Fact]
        public Task Customised_chili_cheese_tots_is_charged_in_full_alongside_a_combo_not_absorbed() =>
            fixture.RunAsync(async () =>
            {
                var ct = TestContext.Current.CancellationToken;
                const string item = "Chili Cheese Tots (Extra Cheese)";
                const decimal unitPrice = 3.79m; // app/frontend/src/data/menuItems.json, "Chili Cheese Tots" Medium

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [
                        ("add", BaseComboName, BaseComboSize, 1, BaseComboPrice),
                        ("add", item, "medium", 1, unitPrice),
                    ],
                    roundTripIndex, ct);

                OrderScenarioHelpers.AssertMoneyEqual(BaseComboPrice + unitPrice, OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!));
                Assert.Equal(2, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
            });

        [Fact]
        public Task Customised_chili_cheese_groovy_fries_is_charged_in_full_alongside_a_combo_not_absorbed() =>
            fixture.RunAsync(async () =>
            {
                var ct = TestContext.Current.CancellationToken;
                const string item = "Chili Cheese Groovy Fries (No Chili)";
                const decimal unitPrice = 3.79m; // app/frontend/src/data/menuItems.json, "Chili Cheese Groovy Fries" Medium

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [
                        ("add", BaseComboName, BaseComboSize, 1, BaseComboPrice),
                        ("add", item, "medium", 1, unitPrice),
                    ],
                    roundTripIndex, ct);

                OrderScenarioHelpers.AssertMoneyEqual(BaseComboPrice + unitPrice, OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!));
                Assert.Equal(2, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
            });

        [Fact]
        public Task Customised_plain_tots_still_absorbs_into_the_combo_side_slot() =>
            fixture.RunAsync(async () =>
            {
                var ct = TestContext.Current.CancellationToken;
                const string item = "Tots (Extra Crispy)";
                const decimal unitPrice = 2.79m; // app/frontend/src/data/menuItems.json, "Tots" Medium

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [
                        ("add", BaseComboName, BaseComboSize, 1, BaseComboPrice),
                        ("add", item, "medium", 1, unitPrice),
                    ],
                    roundTripIndex, ct);

                OrderScenarioHelpers.AssertMoneyEqual(BaseComboPrice, OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!));
                Assert.Equal(1, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
            });

        /// <summary>Brian's decision (2026-09-25, new issue #60): any spoken name-variant of
        /// PLAIN Tots absorbs into the combo side slot exactly like the real "Tots" menu item --
        /// an explicit, exact-match alias, never a substring check. The "must stay charged"
        /// regression net for real-but-different Tots items and off-menu near-misses already
        /// exists just below/above (<see cref="Customised_chili_cheese_tots_is_charged_in_full_alongside_a_combo_not_absorbed"/>,
        /// <see cref="Off_menu_side_like_item_is_charged_in_full_alongside_a_combo_not_absorbed"/>)
        /// and is untouched by this alias.</summary>
        [Theory]
        [InlineData("Tot")]
        [InlineData("Tots")]
        [InlineData("Tater Tot")]
        [InlineData("Tater Tots")]
        [InlineData("Tator Tots")] // common spoken misspelling
        [InlineData("Tater Tots (Extra Crispy)")] // customised alias -- modifier stripped before the alias lookup
        public Task Plain_tots_alias_absorbs_into_the_combo_side_slot(string item) =>
            fixture.RunAsync(async () =>
            {
                var ct = TestContext.Current.CancellationToken;
                const decimal unitPrice = 2.79m; // arbitrary -- update_order's price is caller-supplied
                                                  // and never menu-validated for an alias name; only
                                                  // comboSlot behaviour is under test.

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [
                        ("add", BaseComboName, BaseComboSize, 1, BaseComboPrice),
                        ("add", item, "medium", 1, unitPrice),
                    ],
                    roundTripIndex, ct);

                OrderScenarioHelpers.AssertMoneyEqual(
                    BaseComboPrice,
                    OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
                    $"'{item}' is a spoken alias of plain Tots (Brian's #60 decision) and must absorb into the combo side slot.");
                Assert.Equal(1, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
            });

        /// <summary>PR #61 review, must-fix 3: the one-word ("tatertot(s)", "tatortot(s)") and
        /// hyphenated ("tater-tot(s)", "tator-tot(s)") spoken forms are also aliases of plain
        /// Tots -- <c>_menu_key</c> does not collapse a hyphen to a space, so these needed their
        /// own explicit keys in <c>_TOTS_ALIASES</c>; they weren't already covered by the
        /// space-separated forms above.</summary>
        [Theory]
        [InlineData("tatertot")]
        [InlineData("tatertots")]
        [InlineData("tatortot")]
        [InlineData("tatortots")]
        [InlineData("tater-tot")]
        [InlineData("tater-tots")]
        [InlineData("tator-tot")]
        [InlineData("tator-tots")]
        [InlineData("Tater-Tots (Extra Crispy)")] // customised -- modifier stripped before the alias lookup
        public Task One_word_and_hyphenated_tots_alias_forms_absorb_into_the_combo_side_slot(string item) =>
            fixture.RunAsync(async () =>
            {
                var ct = TestContext.Current.CancellationToken;
                const decimal unitPrice = 2.79m; // arbitrary -- update_order's price is caller-supplied
                                                  // and never menu-validated for an alias name; only
                                                  // comboSlot behaviour is under test.

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [
                        ("add", BaseComboName, BaseComboSize, 1, BaseComboPrice),
                        ("add", item, "medium", 1, unitPrice),
                    ],
                    roundTripIndex, ct);

                OrderScenarioHelpers.AssertMoneyEqual(
                    BaseComboPrice,
                    OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
                    $"'{item}' is a spoken alias of plain Tots (PR #61 review) and must absorb into the combo side slot.");
                Assert.Equal(1, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
            });

        /// <summary>PR #61 review, must-fix 3: "Totts" (doubled-T typo) and "Tater Tot's" (stray
        /// apostrophe) are deliberately NOT in <c>_TOTS_ALIASES</c> -- they must stay charged in
        /// full exactly like any other off-menu near-miss (Rick's PR #50 revenue rule).</summary>
        [Theory]
        [InlineData("Totts")]
        [InlineData("Tater Tot's")]
        public Task Near_miss_tots_spellings_are_charged_in_full_alongside_a_combo_not_absorbed(string item) =>
            fixture.RunAsync(async () =>
            {
                var ct = TestContext.Current.CancellationToken;
                const decimal unitPrice = 2.79m;

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [
                        ("add", BaseComboName, BaseComboSize, 1, BaseComboPrice),
                        ("add", item, "medium", 1, unitPrice),
                    ],
                    roundTripIndex, ct);

                OrderScenarioHelpers.AssertMoneyEqual(
                    BaseComboPrice + unitPrice,
                    OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
                    $"'{item}' is not an exact-match Tots alias and must be charged in full.");
                Assert.Equal(2, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
            });

        /// <summary>PR #61 review, must-fix 5 -- no behaviour change, a pinned fail-safe
        /// contract. Size words embedded directly in the name text are not stripped by
        /// <c>strip_modifiers</c> (only a bracketed <c>(...)</c> modifier is), so "Large Tater
        /// Tots" (size word in the name text) is charged in full, while "Tater Tots (Large)"
        /// (size word as a bracketed modifier) still absorbs into the combo side slot -- the
        /// modifier is stripped before the alias lookup runs, exactly like any other
        /// modifier.
        ///
        /// PR #61 delta review: the original version of this test ordered BOTH items alongside
        /// one combo, so a single combo side slot absorbs at most one of them either way -- the
        /// total (combo + one unit price) was identical whether "Large Tater Tots" or "Tater Tots
        /// (Large)" was the one actually absorbed, so the test could not tell them apart and did
        /// not pin the fail-safe it claimed to. Split into two independent scenarios, each with
        /// the combo plus exactly one item, so the total unambiguously reveals whether that one
        /// item absorbed or not. Rick's S1 mutation (stripping a leading "large " token before the
        /// alias match, so "Large Tater Tots" would also resolve to the Tots alias) now fails the
        /// first scenario below.</summary>
        [Fact]
        public Task Size_word_in_the_name_is_charged_in_full_not_absorbed() =>
            fixture.RunAsync(async () =>
            {
                var ct = TestContext.Current.CancellationToken;
                const decimal unitPrice = 2.79m;

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [
                        ("add", BaseComboName, BaseComboSize, 1, BaseComboPrice),
                        ("add", "Large Tater Tots", "large", 1, unitPrice),
                    ],
                    roundTripIndex, ct);

                OrderScenarioHelpers.AssertMoneyEqual(
                    BaseComboPrice + unitPrice,
                    OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
                    "'Large Tater Tots' (size word in the name text) does not match the Tots " +
                    "alias and must be charged in full alongside the combo.");
                Assert.Equal(2, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
            });

        [Fact]
        public Task Size_word_as_a_bracketed_modifier_still_absorbs() =>
            fixture.RunAsync(async () =>
            {
                var ct = TestContext.Current.CancellationToken;
                const decimal unitPrice = 2.79m;

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [
                        ("add", BaseComboName, BaseComboSize, 1, BaseComboPrice),
                        ("add", "Tater Tots (Large)", "large", 1, unitPrice),
                    ],
                    roundTripIndex, ct);

                OrderScenarioHelpers.AssertMoneyEqual(
                    BaseComboPrice,
                    OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
                    "'Tater Tots (Large)' (size word as a bracketed modifier) is stripped before " +
                    "the alias lookup and absorbs into the combo side slot for free.");
                Assert.Equal(1, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
            });


        /// case here used either a real allow-listed side or a real non-side menu item -- never
        /// an off-menu name that merely LOOKS like a side. These three names are deliberately
        /// off-menu (not in menuItems.json at all, so category inference can't rescue them
        /// either) yet contain "tots"/"fries" substrings that the old, deleted fallback would
        /// have matched.</summary>
        [Theory]
        [InlineData("Loaded Tots Supreme")]
        [InlineData("Crispy Fries Basket")]
        [InlineData("chilli cheese tots")] // misspelling of "Chili Cheese Tots" -- still off-menu verbatim
        public Task Off_menu_side_like_item_is_charged_in_full_alongside_a_combo_not_absorbed(string item) =>
            fixture.RunAsync(async () =>
            {
                var ct = TestContext.Current.CancellationToken;
                const decimal unitPrice = 3.79m; // arbitrary placeholder -- update_order's price is
                                                  // caller-supplied and never menu-validated for an
                                                  // off-menu name; only comboSlot behaviour is under test.

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [
                        ("add", BaseComboName, BaseComboSize, 1, BaseComboPrice),
                        ("add", item, "medium", 1, unitPrice),
                    ],
                    roundTripIndex, ct);

                OrderScenarioHelpers.AssertMoneyEqual(
                    BaseComboPrice + unitPrice,
                    OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
                    $"'{item}' is off-menu and must never silently fill the combo side slot.");
                Assert.Equal(2, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
            });

        /// <summary>PR #50 review (third round): pins the OTHER side of the same fallback split
        /// -- the drink keyword fallback for genuinely off-menu fountain drinks is intentionally
        /// KEPT (unlike the deleted side fallback), so an off-menu fountain drink still fills a
        /// combo's drink slot for free. Deleting `_keyword_fallback_combo_drink` must fail this.</summary>
        [Fact]
        public Task Off_menu_fountain_drink_still_absorbs_into_the_combo_drink_slot() =>
            fixture.RunAsync(async () =>
            {
                var ct = TestContext.Current.CancellationToken;
                const string item = "Dr Pepper Zero"; // off-menu variant, not a literal menuItems.json entry
                const decimal unitPrice = 2.29m; // placeholder -- see comment above

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [
                        ("add", BaseComboName, BaseComboSize, 1, BaseComboPrice),
                        ("add", item, "medium", 1, unitPrice),
                    ],
                    roundTripIndex, ct);

                OrderScenarioHelpers.AssertMoneyEqual(BaseComboPrice, OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!));
                Assert.Equal(1, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
            });

        /// <summary>PR #50 review (round 5, "keyword over-correction"): the round-4 word-boundary
        /// fix over-corrected and broke these genuinely off-menu spoken variants (the real
        /// menuItems.json items are "... Classic Shake" and "... Slush", singular): a bare
        /// `\bshake\b` never matches "milkshake" (no word boundary between "milk" and "shake"),
        /// and the old `s?` suffix only allowed a single trailing "s", missing "-es"/"-ie". A
        /// mutation reverting either regex to its round-4 form must fail this Theory.</summary>
        [Theory]
        [InlineData("Chocolate Milkshake")]
        [InlineData("Cherry Slushes")]
        [InlineData("Blue Raspberry Slushie")]
        public Task Off_menu_spoken_shake_and_slush_variants_still_absorb_into_the_combo_drink_slot(string item) =>
            fixture.RunAsync(async () =>
            {
                var ct = TestContext.Current.CancellationToken;
                const decimal unitPrice = 4.69m; // placeholder -- see comment on the off-menu drink test above

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [
                        ("add", BaseComboName, BaseComboSize, 1, BaseComboPrice),
                        ("add", item, "medium", 1, unitPrice),
                    ],
                    roundTripIndex, ct);

                OrderScenarioHelpers.AssertMoneyEqual(
                    BaseComboPrice,
                    OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
                    $"'{item}' is an off-menu spoken shake/slush variant and must still absorb into the combo drink slot.");
                Assert.Equal(1, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
            });

        /// <summary>PR #61 review (must-fix 1): the combo-drink-slot question is unaffected by
        /// the keyword-precedence fix -- these names contain both a fountain word and a
        /// shake/blast word, and must still fill the combo drink slot regardless of which keyword
        /// "wins" the (separate) happy-hour-discount question. See the sibling Theory in
        /// <see cref="HappyHourDiscountTests"/> for the discount side of the same names.</summary>
        [Theory]
        [InlineData("Cherry Limeade Shake")]
        [InlineData("Strawberry Lemonade Shake")]
        [InlineData("Dr Pepper Shake")]
        [InlineData("Sweet Tea Blast")]
        public Task Off_menu_shake_or_blast_containing_a_fountain_keyword_still_absorbs_into_the_combo_drink_slot(string item) =>
            fixture.RunAsync(async () =>
            {
                var ct = TestContext.Current.CancellationToken;
                const decimal unitPrice = 4.69m; // placeholder -- see comment on the off-menu drink test above

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [
                        ("add", BaseComboName, BaseComboSize, 1, BaseComboPrice),
                        ("add", item, "medium", 1, unitPrice),
                    ],
                    roundTripIndex, ct);

                OrderScenarioHelpers.AssertMoneyEqual(
                    BaseComboPrice,
                    OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
                    $"'{item}' contains a fountain keyword but is a shake/blast and must still absorb into the combo drink slot.");
                Assert.Equal(1, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
            });

        /// <summary>PR #50 review (round 4): pins the word-boundary fix for the fountain-drink
        /// keyword fallback -- a plain substring check let "tea" match inside "steak", so this
        /// genuinely off-menu item (not in menuItems.json at all) was silently absorbed into a
        /// combo's drink slot for free. Word-boundary regex fixes this; a mutation removing the
        /// `\b` anchors must fail this test.</summary>
        [Fact]
        public Task Off_menu_steak_item_is_not_misclassified_as_a_tea_drink_and_charges_in_full() =>
            fixture.RunAsync(async () =>
            {
                var ct = TestContext.Current.CancellationToken;
                const string item = "Steak Sandwich"; // off-menu; contains "tea" as a substring of "steak"
                const decimal unitPrice = 5.49m; // placeholder -- see comment on the off-menu side test above

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [
                        ("add", BaseComboName, BaseComboSize, 1, BaseComboPrice),
                        ("add", item, "standard", 1, unitPrice),
                    ],
                    roundTripIndex, ct);

                OrderScenarioHelpers.AssertMoneyEqual(
                    BaseComboPrice + unitPrice,
                    OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
                    $"'{item}' must not match the bare substring 'tea' inside 'steak' and silently fill the combo drink slot.");
                Assert.Equal(2, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
            });

        /// <summary>Rick's Z2: a customised sundae must not silently fill a combo's drink slot
        /// even though "Shakes & Ice Cream" (its JSON category) is otherwise a combo-drink
        /// category -- Brian's #39 decision (sundaes aren't a drink) must survive customization,
        /// exactly like the plain-sundae case already pinned in
        /// <see cref="InferComboComponentGoldenCategoryTests"/> (Python) /
        /// <c>GoldenMenuComboSlotTheoryTests</c> (C#).</summary>
        [Fact]
        public Task Customised_sundae_is_charged_in_full_alongside_a_combo_not_absorbed_into_the_drink_slot() =>
            fixture.RunAsync(async () =>
            {
                var ct = TestContext.Current.CancellationToken;
                const string item = "Hot Fudge Sundae (Extra Fudge)";
                const decimal unitPrice = 3.19m; // app/frontend/src/data/menuItems.json, "Hot Fudge Sundae"

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [
                        ("add", BaseComboName, BaseComboSize, 1, BaseComboPrice),
                        ("add", item, "standard", 1, unitPrice),
                    ],
                    roundTripIndex, ct);

                OrderScenarioHelpers.AssertMoneyEqual(
                    BaseComboPrice + unitPrice,
                    OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
                    "A customised sundae must never fill a combo's drink slot -- Brian's #39 decision survives customization.");
                Assert.Equal(2, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
            });
    }

    [Collection(HappyHourAtOpenCollection.Name)]
    public sealed class HappyHourDiscountTests(HappyHourAtOpenFixture fixture)
    {
        [Fact]
        public Task Customised_cherry_limeade_still_gets_the_happy_hour_discount() =>
            fixture.RunAsync(async () =>
            {
                // Rick's Y4: a customised drink must not silently lose its happy-hour discount by
                // falling through to a keyword fallback that disagrees with its base item.
                var ct = TestContext.Current.CancellationToken;
                const string item = "Cherry Limeade (Extra Cherries)";
                const decimal unitPrice = 2.89m; // app/frontend/src/data/menuItems.json, "Cherry Limeade" Medium

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [("add", item, "medium", 1, unitPrice)],
                    roundTripIndex, ct);

                var rules = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot()).BusinessRules;
                var expectedTotal = unitPrice * rules.HappyHourDiscount * (1 + rules.TaxRate);
                OrderScenarioHelpers.AssertMoneyEqual(expectedTotal, OrderScenarioHelpers.GetOrderFinalTotal(result.ToolResultJson!));
            });

        [Fact]
        public Task Customised_shake_obeys_the_single_shakes_and_blasts_flag_exactly_like_its_plain_form() =>
            fixture.RunAsync(async () =>
            {
                // menu_utils._SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED is now False (Brian's
                // decision, 2026-09-25) -- both the plain and customised forms of the same shake
                // must agree: full price, not discounted.
                var ct = TestContext.Current.CancellationToken;
                const string item = "Vanilla Classic Shake (No Whip)";
                const decimal unitPrice = 4.69m; // app/frontend/src/data/menuItems.json, "Vanilla Classic Shake" Medium

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [("add", item, "medium", 1, unitPrice)],
                    roundTripIndex, ct);

                var golden = GoldenMenuCategoryData.Load(RepoPaths.FindRepoRoot());
                var baseItemCase = golden.Items.Single(c => c.Item == "Vanilla Classic Shake");
                Assert.False(baseItemCase.HappyHourDiscounted, "Sanity check: base item golden row must now be full price.");

                var rules = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot()).BusinessRules;
                var expectedTotal = unitPrice * (1 + rules.TaxRate); // NOT multiplied by HappyHourDiscount
                OrderScenarioHelpers.AssertMoneyEqual(expectedTotal, OrderScenarioHelpers.GetOrderFinalTotal(result.ToolResultJson!));
            });

        /// <summary>Rick's Z3: a customised sundae must never be happy-hour discounted, exactly
        /// like the plain sundae -- Brian's #39 decision (sundaes are full price during happy
        /// hour) must survive customization too, not just the on-menu, uncustomised case.</summary>
        [Fact]
        public Task Customised_sundae_is_not_happy_hour_discounted() =>
            fixture.RunAsync(async () =>
            {
                var ct = TestContext.Current.CancellationToken;
                const string item = "Hot Fudge Sundae (Extra Fudge)";
                const decimal unitPrice = 3.19m; // app/frontend/src/data/menuItems.json, "Hot Fudge Sundae"

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [("add", item, "standard", 1, unitPrice)],
                    roundTripIndex, ct);

                var rules = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot()).BusinessRules;
                var expectedTotal = unitPrice * (1 + rules.TaxRate); // NOT multiplied by HappyHourDiscount
                OrderScenarioHelpers.AssertMoneyEqual(
                    expectedTotal,
                    OrderScenarioHelpers.GetOrderFinalTotal(result.ToolResultJson!),
                    "A customised sundae must stay full price during happy hour -- Brian's #39 decision survives customization.");
            });

        /// <summary>Rick's Y4, re-pinned directly in conformance (previously only covered by a
        /// pytest): a genuinely off-menu fountain drink must still get the happy-hour discount via
        /// the keyword fallback, at the C# level too, not just Python's.</summary>
        [Fact]
        public Task Off_menu_fountain_drink_is_happy_hour_discounted() =>
            fixture.RunAsync(async () =>
            {
                var ct = TestContext.Current.CancellationToken;
                const string item = "Dr Pepper Zero"; // off-menu variant, not a literal menuItems.json entry
                const decimal unitPrice = 2.29m; // placeholder -- see comment on the off-menu drink test above

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [("add", item, "medium", 1, unitPrice)],
                    roundTripIndex, ct);

                var rules = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot()).BusinessRules;
                var expectedTotal = unitPrice * rules.HappyHourDiscount * (1 + rules.TaxRate);
                OrderScenarioHelpers.AssertMoneyEqual(expectedTotal, OrderScenarioHelpers.GetOrderFinalTotal(result.ToolResultJson!));
            });

        /// <summary>PR #50 review (round 5, "keyword over-correction"): pins the same two spoken
        /// off-menu fountain-drink variants as the combo-drink-slot Theory above, at the
        /// happy-hour-discount question this time -- the two slush spoken variants are always
        /// discounted like every other fountain drink. "Chocolate Milkshake" moved to
        /// <see cref="Off_menu_spoken_shake_variant_is_full_price_during_happy_hour"/> below
        /// (Brian's decision, 2026-09-25: Shakes & Blasts are full price during happy hour).</summary>
        [Theory]
        [InlineData("Cherry Slushes")]
        [InlineData("Blue Raspberry Slushie")]
        public Task Off_menu_spoken_shake_and_slush_variants_are_happy_hour_discounted(string item) =>
            fixture.RunAsync(async () =>
            {
                var ct = TestContext.Current.CancellationToken;
                const decimal unitPrice = 4.69m; // placeholder -- see comment on the off-menu drink test above

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [("add", item, "medium", 1, unitPrice)],
                    roundTripIndex, ct);

                var rules = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot()).BusinessRules;
                var expectedTotal = unitPrice * rules.HappyHourDiscount * (1 + rules.TaxRate);
                OrderScenarioHelpers.AssertMoneyEqual(
                    expectedTotal,
                    OrderScenarioHelpers.GetOrderFinalTotal(result.ToolResultJson!),
                    $"'{item}' is an off-menu spoken slush variant and must still get the happy-hour discount.");
            });

        /// <summary>Brian's decision (2026-09-25, #39 follow-up): Shakes & Blasts are full price
        /// during happy hour -- proves the off-menu keyword-fallback path
        /// (`_keyword_fallback_happy_hour_discounted`) obeys
        /// `_SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED` exactly like the on-menu, JSON-category path
        /// does (<see cref="Customised_shake_obeys_the_single_shakes_and_blasts_flag_exactly_like_its_plain_form"/>),
        /// not just some of the shake/blast surfaces.</summary>
        [Fact]
        public Task Off_menu_spoken_shake_variant_is_full_price_during_happy_hour() =>
            fixture.RunAsync(async () =>
            {
                var ct = TestContext.Current.CancellationToken;
                const string item = "Chocolate Milkshake"; // off-menu; the real item is "... Classic Shake"
                const decimal unitPrice = 4.69m; // placeholder -- see comment on the off-menu drink test above

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [("add", item, "medium", 1, unitPrice)],
                    roundTripIndex, ct);

                var rules = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot()).BusinessRules;
                var expectedTotal = unitPrice * (1 + rules.TaxRate); // NOT multiplied by HappyHourDiscount
                OrderScenarioHelpers.AssertMoneyEqual(
                    expectedTotal,
                    OrderScenarioHelpers.GetOrderFinalTotal(result.ToolResultJson!),
                    $"'{item}' is an off-menu spoken shake variant and must be full price during happy hour (Brian's decision).");
            });

        /// <summary>PR #61 review (must-fix 1): pins the keyword-precedence bug directly against
        /// the live backend, not just the pytest -- these off-menu names contain BOTH a
        /// fountain-drink word ("limeade"/"lemonade"/Dr Pepper/"tea") and a shake/blast word
        /// ("shake"/"blast"), and must resolve as a shake/blast for the DISCOUNT question (full
        /// price, obeying the flag) even though a fountain-drink-first check would have wrongly
        /// discounted them. Combo-drink-slot eligibility is unaffected either way -- see the
        /// sibling Theory in <see cref="ComboSlotTests"/>.</summary>
        [Theory]
        [InlineData("Cherry Limeade Shake")]
        [InlineData("Strawberry Lemonade Shake")]
        [InlineData("Dr Pepper Shake")]
        [InlineData("Sweet Tea Blast")]
        public Task Off_menu_shake_or_blast_containing_a_fountain_keyword_is_full_price_during_happy_hour(string item) =>
            fixture.RunAsync(async () =>
            {
                var ct = TestContext.Current.CancellationToken;
                const decimal unitPrice = 4.69m; // placeholder -- see comment on the off-menu drink test above

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [("add", item, "medium", 1, unitPrice)],
                    roundTripIndex, ct);

                var rules = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot()).BusinessRules;
                var expectedTotal = unitPrice * (1 + rules.TaxRate); // NOT multiplied by HappyHourDiscount
                OrderScenarioHelpers.AssertMoneyEqual(
                    expectedTotal,
                    OrderScenarioHelpers.GetOrderFinalTotal(result.ToolResultJson!),
                    $"'{item}' contains a fountain keyword but is a shake/blast and must be full price during happy hour (keyword precedence bug, PR #61).");
            });
    }

    /// <summary>PR #50 review (round 4): pins the exact `_menu_key()` paren-group-stripping
    /// algorithm end to end, not just via the doctests on `strip_modifiers`'s docstring --
    /// Rick's review explicitly asked for conformance cases covering two groups, a mid-string
    /// group, and a nested/unbalanced group (documented in the README's "exact `_menu_key()`
    /// normalisation algorithm" section).</summary>
    [Collection(HappyHourJustBeforeOpenCollection.Name)]
    public sealed class ParenGroupNormalisationTests(HappyHourJustBeforeOpenFixture fixture)
    {
        [Fact]
        public Task Two_parenthesized_modifier_groups_both_strip_and_the_item_still_absorbs_as_a_side() =>
            fixture.RunAsync(async () =>
            {
                // "Tots (Extra Crispy) (No Salt)" -- two separate `(...)` groups -- must strip to
                // the bare allow-listed key "tots" exactly like a single group would.
                var ct = TestContext.Current.CancellationToken;
                const string item = "Tots (Extra Crispy) (No Salt)";
                const decimal unitPrice = 2.79m; // app/frontend/src/data/menuItems.json, "Tots" Medium

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [
                        ("add", BaseComboName, BaseComboSize, 1, BaseComboPrice),
                        ("add", item, "medium", 1, unitPrice),
                    ],
                    roundTripIndex, ct);

                OrderScenarioHelpers.AssertMoneyEqual(
                    BaseComboPrice,
                    OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
                    "Two parenthesized modifier groups must both strip, same as a single group.");
                Assert.Equal(1, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
            });

        [Fact]
        public Task Mid_string_parenthesized_group_strips_to_a_different_real_menu_item_and_charges_in_full() =>
            fixture.RunAsync(async () =>
            {
                // "Chili Cheese (Extra Cheese) Tots" -- the `(...)` group sits in the MIDDLE of the
                // name, not at the end -- must still strip to "Chili Cheese Tots", a real
                // menuItems.json item that is NOT one of the two allow-listed sides, so it charges
                // in full exactly like its unparenthesized, differently-worded sibling would.
                var ct = TestContext.Current.CancellationToken;
                const string item = "Chili Cheese (Extra Cheese) Tots";
                const decimal unitPrice = 3.79m; // app/frontend/src/data/menuItems.json, "Chili Cheese Tots" Medium

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [
                        ("add", BaseComboName, BaseComboSize, 1, BaseComboPrice),
                        ("add", item, "medium", 1, unitPrice),
                    ],
                    roundTripIndex, ct);

                OrderScenarioHelpers.AssertMoneyEqual(
                    BaseComboPrice + unitPrice,
                    OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
                    "A mid-string parenthesized group must still strip correctly to a real, non-side menu item.");
                Assert.Equal(2, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
            });

        [Fact]
        public Task Nested_unbalanced_parenthesized_group_fails_safe_and_charges_in_full() =>
            fixture.RunAsync(async () =>
            {
                // "Tots (Extra (Really) Crispy)" -- a nested group -- cannot be fully stripped by
                // the single-level `\([^)]*\)` pattern (it can't cross the inner "("), leaving a
                // stray ")" in the normalised key ("tots crispy)"). This must match NO menu key and
                // fall through to full price -- a deliberate fail-safe, never a silent free side.
                var ct = TestContext.Current.CancellationToken;
                const string item = "Tots (Extra (Really) Crispy)";
                const decimal unitPrice = 2.79m; // placeholder -- see comment on the off-menu side test above

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [
                        ("add", BaseComboName, BaseComboSize, 1, BaseComboPrice),
                        ("add", item, "medium", 1, unitPrice),
                    ],
                    roundTripIndex, ct);

                OrderScenarioHelpers.AssertMoneyEqual(
                    BaseComboPrice + unitPrice,
                    OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
                    "A nested/unbalanced parenthesized group must fail safe to full price, never a silent free side.");
                Assert.Equal(2, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
            });
    }

    /// <summary>PR #50 review round 5, should-fix item 4: "™" and the curly apostrophe "’"
    /// (U+2019) must be normalised in `_menu_key()` exactly like "®" already is, or a spoken name
    /// that naturally omits an unspeakable symbol (or uses a plain apostrophe) misses its own
    /// `MENU_CATEGORY_MAP` entry -- exactly the OREO Blast's NBSP regression class from round 4.
    ///
    /// A Smasher is a "Burgers &amp; Sandwiches" item, so the miss is invisible through the
    /// combo-drink-slot / happy-hour paths this file otherwise exercises (neither the fountain nor
    /// the shake/blast/malt keyword fallback matches "smasher" either way). The one place the miss
    /// *is* observable end to end is `update_order`'s extras-eligibility check
    /// (`tools.py::update_order`, `ALLOWED_EXTRA_CATEGORIES`): it only allows an "extra" line item
    /// (e.g. "Add Bacon") when an existing order item's `infer_category()` resolves to an allowed
    /// category. "burgers &amp; sandwiches" is on that allow-list -- but only if the Smasher
    /// resolves via the map. If the map lookup misses (pre-fix), `infer_category` falls through to
    /// keyword guessing, which matches none of its keywords ("burger" is not a substring of
    /// "smasher"), returns "", and the extra is wrongly rejected with an apology even though a
    /// perfectly valid base item is already in the order.</summary>
    [Collection(HappyHourJustBeforeOpenCollection.Name)]
    public sealed class TrademarkAndCurlyApostropheNormalisationTests(HappyHourJustBeforeOpenFixture fixture)
    {
        [Fact]
        public Task Smasher_spoken_without_its_trademark_symbol_still_resolves_and_allows_an_extra() =>
            fixture.RunAsync(async () =>
            {
                var ct = TestContext.Current.CancellationToken;
                // menuItems.json's real name is "All-American SONIC Smasher™" -- spoken/transcribed
                // without the unspeakable "™" symbol, exactly as a guest's speech-to-text would.
                const string smasher = "All-American SONIC Smasher";
                const decimal smasherPrice = 5.79m; // app/frontend/src/data/menuItems.json
                const string extra = "Add Bacon"; // tools.py EXTRAS_KEYWORDS
                const decimal extraPrice = 0.99m;

                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
                await using var _ = browser;

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [
                        ("add", smasher, "standard", 1, smasherPrice),
                        ("add", extra, "standard", 1, extraPrice),
                    ],
                    roundTripIndex, ct);

                OrderScenarioHelpers.AssertMoneyEqual(
                    smasherPrice + extraPrice,
                    OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
                    "A Smasher spoken without its '™' must still resolve to 'burgers & sandwiches' via " +
                    "the map, so an extra ('Add Bacon') is allowed instead of wrongly rejected.");
                Assert.Equal(2, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
            });
    }
}
