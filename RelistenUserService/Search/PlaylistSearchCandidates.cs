using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Relisten.Accounts.Contracts.Search;

namespace RelistenUserService.Search;

internal sealed class PlaylistSearchCandidates(HttpClient client, IConfiguration configuration)
{
    internal const int PageSize = 20;

    public async Task<PlaylistCandidate[]> Find(
        MergedSearchRequest request,
        UserId? viewer,
        CancellationToken cancellationToken
    )
    {
        var query = request.Q?.Trim();
        if (string.IsNullOrEmpty(query) || query == "*")
        {
            return [];
        }

        const string publiclyVisible = "(published:=true && archived:=false && taken_down:=false)";
        var filter = viewer is null
            ? publiclyVisible
            : $"({publiclyVisible} || owner_id:={viewer.Value})";
        if (request.ArtistId is not null)
        {
            filter += $" && artist_ids:={request.ArtistId}";
        }

        var alias = configuration["Search:PlaylistAlias"] ?? PlaylistSearchJob.DefaultAlias;
        // A playlist's own text is stronger evidence than an artist or song on one track.
        var path = QueryHelpers.AddQueryString(
            $"collections/{Uri.EscapeDataString(alias)}/documents/search",
            new Dictionary<string, string?>
            {
                ["q"] = query,
                ["query_by"] = "name,description,curator,entry_notes,guide_text,artists,songs",
                ["query_by_weights"] = "8,5,4,3,3,2,2",
                ["text_match_type"] = "max_weight",
                ["drop_tokens_threshold"] = "0",
                ["filter_by"] = filter,
                ["include_fields"] =
                    "id,history_generation,revision,curator_version,published,archived,taken_down",
                ["highlight_fields"] = "none",
                ["sort_by"] = "_text_match:desc,follow_count:desc,updated_at:desc",
                ["page"] = request.Page.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["per_page"] = request.Scope.Equals("all", StringComparison.OrdinalIgnoreCase)
                    ? "6"
                    : "20",
            }
        );
        var result = await client.GetFromJsonAsync<PlaylistHits>(path, Json, cancellationToken);
        if (result?.Hits is null)
        {
            throw new JsonException("Typesense returned no playlist hits property.");
        }

        return result.Hits.Select(hit => hit.Document).OfType<PlaylistCandidate>().ToArray();
    }

    private sealed record PlaylistHits(PlaylistHit[]? Hits);

    private sealed record PlaylistHit(PlaylistCandidate? Document);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}

internal sealed record PlaylistCandidate(
    string Id,
    string HistoryGeneration,
    long Revision,
    long CuratorVersion,
    bool Published,
    bool Archived,
    bool TakenDown
);
