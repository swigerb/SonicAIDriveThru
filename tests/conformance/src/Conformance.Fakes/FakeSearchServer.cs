using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Conformance.Fakes;

/// <summary>
/// A Kestrel-hosted fake of the Azure AI Search REST surface the backend's
/// `azure-search-documents` client calls (POST /indexes('{name}')/docs/search.post.search).
/// Answers from `app/frontend/src/data/menuItems.json` (via <see cref="MenuIndex"/>) rather than
/// a real index, and accepts the exact body shape the SDK sends: `search`, `queryType`,
/// `semanticConfiguration`, `select` (comma-joined string), `top`, and `vectorQueries`.
/// </summary>
public sealed class FakeSearchServer : IAsyncDisposable
{
    private readonly string _menuItemsJsonPath;
    private WebApplication? _app;
    private IReadOnlyList<MenuDocument> _documents = [];

    public FrameLog ReceivedRequests { get; } = new();

    public Uri BaseUri { get; private set; } = new("http://127.0.0.1:0");

    public string? LastApiKeyHeader { get; private set; }

    /// <summary>
    /// Issue #9 / harness follow-up #23: opt-in, one-shot field-name-mismatch simulation. When
    /// set to a field name (e.g. "sizes"), the *next* request whose `select` list contains that
    /// field is answered with HTTP 400 and an Azure-AI-Search-shaped error body whose message
    /// contains "Could not find a property named '&lt;field&gt;'" — the exact substring
    /// app/backend/tools.py's `search()` matches on to trigger its fallback retry with a minimal
    /// `select`. The flag clears itself immediately after firing once, so the retry (which asks
    /// for a different, always-present field set) and every other unrelated request/scenario
    /// succeed normally. Defaults to null (inert) — no existing scenario's behavior changes.
    /// </summary>
    public string? RejectSelectFieldOnce { get; set; }

    public FakeSearchServer(string menuItemsJsonPath)
    {
        _menuItemsJsonPath = menuItemsJsonPath;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default, int? fixedPort = null)
    {
        _documents = MenuIndex.Load(_menuItemsJsonPath);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{fixedPort?.ToString() ?? "0"}");
        var app = builder.Build();
        app.MapGet("/", () => Results.Ok());
        app.MapPost("/indexes('{indexName}')/docs/search.post.search", HandleSearchAsync);

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;
        BaseUri = new Uri(app.Urls.First());
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleSearchAsync(HttpContext context, string indexName)
    {
        LastApiKeyHeader = context.Request.Headers["api-key"];
        if (string.IsNullOrEmpty(LastApiKeyHeader))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var requestDoc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted)
            .ConfigureAwait(false);
        var recorded = ReceivedRequests.Add(requestDoc.RootElement.Clone());

        var root = recorded.Json;
        var searchText = root.TryGetProperty("search", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        var top = root.TryGetProperty("top", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 50;
        var selectFields = root.TryGetProperty("select", out var sel) && sel.ValueKind == JsonValueKind.String
            ? sel.GetString()!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : null;

        var rejectField = RejectSelectFieldOnce;
        if (rejectField is not null && selectFields is not null &&
            selectFields.Contains(rejectField, StringComparer.OrdinalIgnoreCase))
        {
            RejectSelectFieldOnce = null; // one-shot: only this request is rejected
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json;odata.metadata=none";
            var errorBody = new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["code"] = "InvalidRequestParameter",
                    ["message"] = $"Could not find a property named '{rejectField}' on type 'search.document'.",
                },
            };
            await context.Response.WriteAsync(errorBody.ToJsonString(), context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var matches = Filter(searchText).Take(top);

        var values = new JsonArray();
        foreach (var doc in matches)
        {
            values.Add(ProjectDocument(doc, selectFields));
        }

        var response = new JsonObject { ["value"] = values };
        context.Response.ContentType = "application/json;odata.metadata=none";
        await context.Response.WriteAsync(response.ToJsonString(), context.RequestAborted).ConfigureAwait(false);
    }

    private IEnumerable<MenuDocument> Filter(string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText) || searchText == "*")
        {
            return _documents;
        }

        var terms = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matched = _documents.Where(d => terms.Any(term =>
            d.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            d.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            d.Category.Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();

        // A real semantic/vector search never returns zero rows for a plausible menu question;
        // fall back to the full catalog so tools.py's happy path always has something to reason
        // over, matching how the real ranked/semantic index behaves for near-miss queries.
        return matched.Count > 0 ? matched : _documents;
    }

    private static JsonObject ProjectDocument(MenuDocument doc, string[]? selectFields)
    {
        var all = new JsonObject
        {
            ["@search.score"] = 1.0,
            ["id"] = doc.Id,
            ["name"] = doc.Name,
            ["category"] = doc.Category,
            ["description"] = doc.Description,
            ["sizes"] = doc.SizesJson,
        };

        if (selectFields is null)
        {
            return all;
        }

        var projected = new JsonObject { ["@search.score"] = 1.0 };
        foreach (var field in selectFields)
        {
            if (all.TryGetPropertyValue(field, out var value))
            {
                projected[field] = value?.DeepClone();
            }
        }
        return projected;
    }
}
