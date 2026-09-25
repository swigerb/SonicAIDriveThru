using System.Text.Json;
using Conformance.Harness;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Issue #39: typed loader for tests/conformance/testdata/golden-menu-categories.json, the golden
/// combo-slot/happy-hour-bucket table generated from app/backend/menu_utils.py
/// ::infer_combo_component and consumed by app/backend/tests/test_menu_utils.py. Kept as a
/// separate file/loader from GoldenOrderPricingData.cs (a different golden dataset, not part of
/// the money contract) so a future C# backend's own test suite can assert against the exact same
/// 60-item table without transcribing it a third time.
/// </summary>
public sealed record MenuCategoryCase(string Item, string Category, string Bucket);

public sealed record GoldenMenuCategoryData(string Description, IReadOnlyList<MenuCategoryCase> Items)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static GoldenMenuCategoryData Load(string repoRoot)
    {
        var path = RepoPaths.GoldenMenuCategoriesJsonPath(repoRoot);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<GoldenMenuCategoryData>(json, Options)
            ?? throw new InvalidDataException($"Golden menu category dataset at '{path}' deserialized to null.");
    }
}
