using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace RelistenUserService.Mcp;

/// <summary>Calls the public v4 origin without forwarding the listener's token or ID.</summary>
internal sealed class McpCatalogClient(HttpClient client, McpReviewSearchBudget reviewBudget)
{
    public Task<JsonElement> Get(
        string path,
        CancellationToken cancellationToken,
        params (string Name, string? Value)[] query
    ) => Send(GetRequest(path, query), cancellationToken);

    public Task<JsonElement> SearchReviews(
        string artist,
        UserId? account,
        CancellationToken cancellationToken,
        params (string Name, string? Value)[] query
    )
    {
        var key = account is { } signedIn ? reviewBudget.Acquire(signedIn) : null;
        var request = GetRequest($"artists/{Segment(artist)}/reviews/search", query);
        if (key is not null)
        {
            request.Headers.Add("X-Relisten-Mcp-Review-Key", key);
        }
        return Send(request, cancellationToken);
    }

    private static HttpRequestMessage GetRequest(
        string path,
        (string Name, string? Value)[] query
    ) =>
        new(
            HttpMethod.Get,
            QueryHelpers.AddQueryString(
                "api/v4/" + path,
                query
                    .Where(pair => pair.Value is not null)
                    .ToDictionary(pair => pair.Name, pair => pair.Value)
            )
        );

    public async Task<string> GetArtistSlug(string artist, CancellationToken cancellationToken)
    {
        var resource = await Get($"artists/{Segment(artist)}", cancellationToken);
        return resource.GetProperty("slug").GetString()!;
    }

    public Task<JsonElement> ResolveTracks(string[] ids, CancellationToken cancellationToken) =>
        Send(
            new(HttpMethod.Post, "api/v4/catalog/resolve")
            {
                Content = JsonContent.Create(
                    new
                    {
                        references = ids.Select(id => new
                        {
                            kind = "track",
                            scheme = "v4",
                            id,
                        }),
                    }
                ),
            },
            cancellationToken
        );

    public static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value is "." or "..")
        {
            throw new McpCatalogException("Use a valid catalog identifier.");
        }
        return Uri.EscapeDataString(value);
    }

    private async Task<JsonElement> Send(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        using var message = request;
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        }
        var error = response.StatusCode switch
        {
            HttpStatusCode.NotFound or HttpStatusCode.Gone => "The catalog item was not found.",
            HttpStatusCode.UnprocessableEntity =>
                "The catalog could not use this query. Check the IDs or narrow the search by song, year, tour, or show.",
            HttpStatusCode.TooManyRequests =>
                "Relisten's catalog is limiting requests. Try again shortly.",
            _ => "Relisten's catalog is temporarily unavailable.",
        };
        throw new McpCatalogException(error);
    }
}

internal sealed class McpCatalogException(string message) : Exception(message);
