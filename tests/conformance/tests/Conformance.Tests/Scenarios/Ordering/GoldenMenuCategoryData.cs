using System.Text.Json;
using System.Text.Json.Serialization;
using Conformance.Harness;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Issue #39 / PR #50 review: typed loader for tests/conformance/testdata/golden-menu-categories.json,
/// the golden per-item table generated from app/backend/menu_utils.py::infer_combo_component
/// (ComboSlot) and ::is_happy_hour_discounted (HappyHourDiscounted) and consumed by
/// app/backend/tests/test_menu_utils.py. These are two DELIBERATELY SEPARATE columns (PR #50
/// review: don't derive one from the other), not one shared bucket. Kept as a separate
/// file/loader from GoldenOrderPricingData.cs (a different golden dataset, not part of the money
/// contract) so a future C# backend's own test suite can assert against the exact same 60-item
/// table without transcribing it a third time.
/// </summary>
public sealed record MenuCategoryCase(string Item, string Category, string ComboSlot, bool HappyHourDiscounted, string Size, decimal UnitPrice);

public sealed record GoldenMenuCategoryData(string Description, IReadOnlyList<MenuCategoryCase> Items)
{
    // PR #50 review cheap follow-up: unitPrice is now a quoted exact-decimal string in the golden
    // JSON (matching golden-order-pricing.json's money convention), so AllowReadingFromString lets
    // it deserialize straight into `decimal` with no intermediate `double` -- consistent with every
    // other golden money value in this test suite.
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static GoldenMenuCategoryData Load(string repoRoot)
    {
        var path = RepoPaths.GoldenMenuCategoriesJsonPath(repoRoot);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<GoldenMenuCategoryData>(json, Options)
            ?? throw new InvalidDataException($"Golden menu category dataset at '{path}' deserialized to null.");
    }
}
