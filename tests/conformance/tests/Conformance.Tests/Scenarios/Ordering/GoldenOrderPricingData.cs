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
///
/// MONEY CONTRACT (PR #38 review item 1): every money-typed property below is `decimal`, not
/// `double`. The golden JSON stores each money value as a quoted, exact decimal string (see the
/// dataset's own top-level `description` for the full contract), and `JsonNumberHandling
/// .AllowReadingFromString` (set on <see cref="Options"/>) lets System.Text.Json deserialize a
/// quoted JSON string directly into a `decimal` property with no intermediate `double` — the same
/// guarantee scenario code gets from `JsonElement.GetDecimal()` when parsing the live backend's
/// wire responses (see OrderScenarioHelpers.cs). Never re-introduce a `double` money property here
/// without re-introducing exactly the float noise this whole exercise removed.
/// </summary>
public sealed record BusinessRules(
    decimal TaxRate,
    decimal HappyHourDiscount,
    int HappyHourStartHour,
    int HappyHourEndHour,
    int MaxItemQuantity,
    int MaxOrderItems,
    string StoreTimezone);

public sealed record HappyHourBoundaryCase(string Instant, bool ExpectedHappyHour, string Description);

public sealed record HappyHourBoundaryInstants(string Note, IReadOnlyList<HappyHourBoundaryCase> Cases);

public sealed record TaxCaseItem(string Item, string Size, int Quantity, decimal UnitPrice, bool IsDrink);

public sealed record TaxCase(
    string Description,
    IReadOnlyList<TaxCaseItem> Items,
    bool HappyHour,
    decimal ExpectedSubtotal,
    decimal ExpectedTax,
    decimal ExpectedFinalTotal);

public sealed record Route44Info(IReadOnlyList<string> Aliases, string ExpectedDisplayPrefix);

public sealed record SizeDisplayCase(string Item, string Size, string ExpectedDisplay);

public sealed record QuantityLimits(int MaxItemQuantity, int MaxOrderItems);

public sealed record ComboMenuItem(string Name, string Size, decimal Price, decimal ExpectedTax, decimal ExpectedFinalTotal);

public sealed record CombosSection(int ExpectedCount, IReadOnlyList<ComboMenuItem> Items);

public sealed record ComboStep(string Action, string Item, string Size, int Quantity, decimal Price);

public sealed record ComboAbsorptionScenario(
    string Description,
    IReadOnlyList<ComboStep> Steps,
    bool HappyHour,
    int ExpectedLineItemCount,
    decimal ExpectedTotal,
    bool ExpectedComboComplete,
    int? ExpectedStandaloneQuantityAfterConversion = null);

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
