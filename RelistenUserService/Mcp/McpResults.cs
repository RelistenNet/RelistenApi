using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace RelistenUserService.Mcp;

internal static class McpResults
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static CallToolResult Data<T>(T value)
    {
        var json = JsonSerializer.SerializeToElement(value, Json);
        return new()
        {
            StructuredContent = json,
            Content = [new TextContentBlock { Text = json.GetRawText() }],
        };
    }

    public static CallToolResult Error(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
