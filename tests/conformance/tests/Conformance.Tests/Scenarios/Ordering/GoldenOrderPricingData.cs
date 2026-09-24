using System.Text.Json;
using System.Text.Json.Serialization;
using Conformance.Harness;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Issue #9: typed loader for tests/conformance/testdata/golden-order-pricing.json, the shared
/// golden pricing/tax/combo/Route-44 dataset ported from app/backend/tests/test_order_state.py,
/// test_tool_calling.py, and test_combo_orders.py. Kept in one file so both these S1-3 black-box
/// scenarios and a future C# backend's own test suite (S4) assert against the exact same
/// cent-accurate cases instead of two independently-transcribed (and potentially drifting) copies.
/// </summary>
public sealed record BusinessRules(
    double TaxRate,
    double HappyHourDiscount,
    int HappyHourStartHour,
    int HappyHourEndHour,
    int MaxItemQuantity,
    int MaxOrderItems,
    string StoreTimezone);

public sealed record HappyHourBoundaryCase(string Instant, bool ExpectedHappyHour, string Description);

public sealed record HappyHourBoundaryInstants(IReadOnlyList<HappyHourBoundaryCase> Cases);

public sealed record TaxCaseItem(string Item, string Size, int Quantity, double UnitPrice, bool IsDrink);

public sealed record TaxCase(
    string Description,
    IReadOnlyList<TaxCaseItem> Items,
    bool HappyHour,
    double ExpectedSubtotal,
    double ExpectedTax,
    double ExpectedFinalTotal);

public sealed record Route44Info(IReadOnlyList<string> Aliases, string ExpectedDisplayPrefix);

public sealed record SizeDisplayCase(string Item, string Size, string ExpectedDisplay);

public sealed record QuantityLimits(int MaxItemQuantity, int MaxOrderItems);

public sealed record ComboMenuItem(string Name, string Size, double Price, double ExpectedTax, double ExpectedFinalTotal);

public sealed record CombosSection(int ExpectedCount, IReadOnlyList<ComboMenuItem> Items);

public sealed record ComboStep(string Action, string Item, string Size, int Quantity, double Price);

public sealed record ComboAbsorptionScenario(
    string Description,
    IReadOnlyList<ComboStep> Steps,
    bool HappyHour,
    int ExpectedLineItemCount,
    double ExpectedTotal,
    bool ExpectedComboComplete);

public sealed record GoldenOrderPricingData(
    BusinessRules BusinessRules,
    HappyHourBoundaryInstants HappyHourBoundaryInstants,
    IReadOnlyList<TaxCase> TaxCases,
    Route44Info Route44,
    IReadOnlyList<SizeDisplayCase> SizeDisplayCases,
    QuantityLimits QuantityLimits,
    CombosSection Combos,
    IReadOnlyList<ComboAbsorptionScenario> ComboAbsorptionScenarios)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Loads and deserializes the golden dataset from its fixed repo-relative path.</summary>
    public static GoldenOrderPricingData Load(string repoRoot)
    {
        var path = RepoPaths.GoldenOrderPricingJsonPath(repoRoot);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<GoldenOrderPricingData>(json, Options)
            ?? throw new InvalidDataException($"Golden order pricing dataset at '{path}' deserialized to null.");
    }
}
