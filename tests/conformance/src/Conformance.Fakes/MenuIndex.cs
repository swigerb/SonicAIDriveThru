using System.Text;
using System.Text.Json;

namespace Conformance.Fakes;

/// <summary>One flattened Azure AI Search document, mirroring the shape `tools.py` expects back.</summary>
public sealed record MenuDocument(string Id, string Name, string Category, string Description, string SizesJson);

/// <summary>
/// Loads `app/frontend/src/data/menuItems.json` (the single source of menu data for both the
/// real Azure AI Search index and this fake) and flattens it into search documents the same
/// shape `tools.py` selects from: id, name, category, description, and sizes as a JSON string
/// (tools.py does `json.loads()` on the sizes field, so it must round-trip as a string, not a
/// nested array).
/// </summary>
public static class MenuIndex
{
    public static IReadOnlyList<MenuDocument> Load(string menuItemsJsonPath)
    {
        using var stream = File.OpenRead(menuItemsJsonPath);
        using var document = JsonDocument.Parse(stream);

        var documents = new List<MenuDocument>();
        foreach (var category in document.RootElement.GetProperty("menuItems").EnumerateArray())
        {
            var categoryName = category.GetProperty("category").GetString() ?? "";
            foreach (var item in category.GetProperty("items").EnumerateArray())
            {
                var name = item.GetProperty("name").GetString() ?? "";
                var description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                var sizesJson = item.TryGetProperty("sizes", out var sizes)
                    ? sizes.GetRawText()
                    : "[]";
                documents.Add(new MenuDocument(Id: Slugify(categoryName, name), Name: name, Category: categoryName,
                    Description: description, SizesJson: sizesJson));
            }
        }

        return documents;
    }

    private static string Slugify(string category, string name)
    {
        var combined = $"{category}-{name}";
        var builder = new StringBuilder(combined.Length);
        foreach (var c in combined.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }
        return builder.ToString().Trim('-');
    }
}
