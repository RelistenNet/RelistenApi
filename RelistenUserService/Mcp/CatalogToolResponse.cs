using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace RelistenUserService.Mcp;

/// <summary>Projects v4 wire responses into bounded catalog tool results.</summary>
internal static class CatalogToolResponse
{
    public static async Task<CallToolResult> Read<T>(Func<Task<T>> read)
    {
        try
        {
            var value = await read();
            return value is CallToolResult result ? result : McpResults.Data(value);
        }
        catch (McpCatalogException exception)
        {
            return McpResults.Error(exception.Message);
        }
        catch (HttpRequestException)
        {
            return McpResults.Error("Relisten's catalog is temporarily unavailable.");
        }
    }

    public static object Page(object[] rows, int cursor, int limit) =>
        new
        {
            items = rows.Skip(cursor).Take(limit),
            next_cursor = (long)cursor + limit < rows.Length ? (int?)(cursor + limit) : null,
        };

    public static object ShowSummary(JsonElement show) =>
        Fields(
            show,
            "id",
            "artist_id",
            "date",
            "display_date",
            "has_soundboard_source",
            "source_count",
            "avg_rating",
            "artist_venue",
            "imported_venue"
        );

    public static string? ShowLink(string slug, JsonElement show)
    {
        var date = (String(show, "display_date") ?? String(show, "date")?.Split('T')[0])?.Split(
            '-'
        );
        return date is { Length: 3 }
            ? $"https://relisten.net/{Uri.EscapeDataString(slug)}/"
                + string.Join("/", date.Select(Uri.EscapeDataString))
            : null;
    }

    public static JsonElement? Object(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Object
            ? property
            : null;

    public static int ShowYear(JsonElement show) =>
        DateOnly.TryParseExact(
            String(show, "date")?.Split('T')[0],
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date
        )
            ? date.Year
            : 0;

    public static IEnumerable<JsonElement> Rows(JsonElement value, string name) =>
        value.TryGetProperty(name, out var rows) && rows.ValueKind == JsonValueKind.Array
            ? rows.EnumerateArray()
            : [];

    public static Dictionary<string, JsonElement> Fields(
        JsonElement value,
        params string[] names
    ) =>
        value
            .EnumerateObject()
            .Where(field => names.Contains(field.Name))
            .ToDictionary(field => field.Name, field => field.Value);

    public static string? String(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    public static string? NestedText(JsonElement value, string objectName, string name) =>
        value.TryGetProperty(objectName, out var property)
        && property.ValueKind == JsonValueKind.Object
            ? String(property, name)
            : null;
}
