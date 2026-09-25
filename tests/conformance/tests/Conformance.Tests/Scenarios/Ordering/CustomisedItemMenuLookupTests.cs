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

        /// <summary>PR #50 review (third round): pins that an unrecognised/off-menu side-like
        /// name NEVER fills the combo side slot -- this is the shared-suite gap Rick's mutation
        /// (b) exposed. Reintroducing a substring fallback ("if 'tots' in name or 'fries' in
        /// name: return 'sides'") ahead of/instead of the deleted one makes Python's own unit
        /// test fail, but nothing in this C# suite noticed, because every existing conformance
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
                // menu_utils._SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED is currently True (pending
                // Brian) -- both the plain and customised forms of the same shake must agree.
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
                Assert.True(baseItemCase.HappyHourDiscounted, "Sanity check: base item golden row must currently be discounted.");

                var rules = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot()).BusinessRules;
                var expectedTotal = unitPrice * rules.HappyHourDiscount * (1 + rules.TaxRate);
                OrderScenarioHelpers.AssertMoneyEqual(expectedTotal, OrderScenarioHelpers.GetOrderFinalTotal(result.ToolResultJson!));
            });
    }
}
