using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// PR #50 review must-fix: a comprehensive data-driven Theory over ALL 60 rows of
/// tests/conformance/testdata/golden-menu-categories.json, added specifically to replace
/// "illustrative subset" coverage with an exhaustive regression net for the combo side-slot
/// pricing bug Rick caught in review (menu_utils.py's old wide "Extras & Sides"/"Hot Dogs & Tots"
/// category-to-"sides" mapping silently absorbed 9+ non-Tots/Fries items for free into a combo's
/// side slot -- e.g. Cheeseburger Combo + Crispy Tenders 5pc totalled $8.49 instead of $15.98).
///
/// For every golden row, adds one base combo ("SONIC® Cheeseburger Combo") then one unit of the
/// row's item, and asserts the total reflects whether ComboSlot says the item should be absorbed
/// for free ("sides"/"drinks") or charged in full ("none"). This must catch:
///   - Rick's X4 (the Corn Dog "hot dog entree" exception removed, so Corn Dog wrongly absorbs
///     into the combo side slot again)
///   - Rick's X6 (the "Extras & Sides"/"Hot Dogs & Tots" wide category-to-"sides" bucket
///     reintroduced, so e.g. Crispy Tenders/Onion Rings/Ched 'R' Peppers wrongly absorb again)
/// regardless of the underlying implementation detail, because it asserts the OBSERVABLE
/// end-to-end price for every real menu item, not an internal classification helper.
///
/// Only the TOTAL is asserted generically across all 60 rows -- not the line-item count -- because
/// one golden row (the base combo itself, "SONIC® Cheeseburger Combo") collides on item+size with
/// the fixture's base combo and merges into one line at quantity 2 by the pre-existing (unrelated)
/// duplicate-line-merge behavior in order_state.py, rather than creating a second line; the total
/// is identical either way (2x the combo price) and is the financially meaningful invariant Rick's
/// regression was actually about.
/// </summary>
[Collection(HappyHourJustBeforeOpenCollection.Name)]
public sealed class GoldenMenuComboSlotTheoryTests(HappyHourJustBeforeOpenFixture fixture)
{
    private const string BaseComboName = "SONIC® Cheeseburger Combo";
    private const string BaseComboSize = "standard";
    private const decimal BaseComboPrice = 8.49m;

    public static TheoryData<int> GoldenRowIndexes()
    {
        var golden = GoldenMenuCategoryData.Load(RepoPaths.FindRepoRoot());
        var data = new TheoryData<int>();
        for (var i = 0; i < golden.Items.Count; i++)
        {
            data.Add(i);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(GoldenRowIndexes))]
    public Task Golden_combo_slot_determines_whether_the_item_is_absorbed_or_charged_in_full(int rowIndex) =>
        fixture.RunAsync(async () =>
        {
            var ct = TestContext.Current.CancellationToken;
            var golden = GoldenMenuCategoryData.Load(RepoPaths.FindRepoRoot());
            Assert.Equal(60, golden.Items.Count);
            var row = golden.Items[rowIndex];

            var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
            await using var _ = browser;

            var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                connection, browser,
                [
                    ("add", BaseComboName, BaseComboSize, 1, BaseComboPrice),
                    ("add", row.Item, row.Size, 1, row.UnitPrice),
                ],
                roundTripIndex, ct);

            var isAbsorbed = row.ComboSlot is "sides" or "drinks";
            var expectedTotal = isAbsorbed ? BaseComboPrice : BaseComboPrice + row.UnitPrice;
            OrderScenarioHelpers.AssertMoneyEqual(
                expectedTotal,
                OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
                $"{row.Item} (comboSlot={row.ComboSlot}): expected total {expectedTotal} " +
                $"({(isAbsorbed ? "absorbed into the combo's slot" : "charged in full alongside the combo")}).");

            if (!isAbsorbed)
            {
                var expectedItemCount = row.Item == BaseComboName ? 1 : 2;
                Assert.Equal(expectedItemCount, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
            }
            else
            {
                Assert.Equal(1, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
            }
        });
}
