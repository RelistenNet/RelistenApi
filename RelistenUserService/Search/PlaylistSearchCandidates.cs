using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Relisten.Accounts.Contracts.Search;
using Relisten.Search;

namespace RelistenUserService.Search;

internal sealed class PlaylistSearchCandidates(HttpClient client, IConfiguration configuration)
{
    internal const int PageSize = 20;
    internal const int AllPageSize = 20;
    private const string CandidateFields =
        "id,history_generation,revision,published,archived,taken_down";

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
        var path = $"collections/{Uri.EscapeDataString(alias)}/documents/search";
        var parameters = new Dictionary<string, string?>
        {
            ["q"] = query,
            ["query_by"] = "name,description,curator,entry_notes,guide_text,artists,songs",
            ["query_by_weights"] = "8,5,4,3,3,2,2",
            ["text_match_type"] = "max_weight",
            ["drop_tokens_threshold"] = "0",
            ["filter_by"] = filter,
            ["include_fields"] = CandidateFields + ",popularity_percentile",
            ["highlight_fields"] = "description,curator,entry_notes,guide_text,artists,songs",
            ["sort_by"] = "_text_match:desc,plays_year:desc,follow_count:desc",
            ["page"] = request.Page.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["per_page"] = (request.IsAll ? AllPageSize : PageSize).ToString(
                System.Globalization.CultureInfo.InvariantCulture
            ),
        };
        var result = await ReadHits(path, parameters, cancellationToken);
        if (result?.Hits is null)
        {
            throw new JsonException("Typesense returned no playlist hits property.");
        }

        return result
            .Hits.Where(hit => hit.Document != null)
            .Select(hit =>
                hit.Document! with
                {
                    SupportingMatch = SupportingMatch(query, hit.Highlights ?? [])
                        .WithTypoCoverage(query == "*" ? null : hit.TextMatchInfo, weight: .45),
                }
            )
            .ToArray();
    }

    private async Task<PlaylistHits?> ReadHits(
        string path,
        Dictionary<string, string?> parameters,
        CancellationToken ct
    )
    {
        using var response = await client.GetAsync(
            QueryHelpers.AddQueryString(path, parameters),
            ct
        );
        if (
            response.StatusCode is (HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
            && (await response.Content.ReadAsStringAsync(ct)).Contains(
                "Could not find a field named `plays_year` in the schema",
                StringComparison.Ordinal
            )
        )
        {
            // An older alias can still serve text and follower ordering until its next rebuild.
            parameters["sort_by"] = "_text_match:desc,follow_count:desc";
            // Old percentiles measured follows. Only new generations carry a play percentile.
            parameters["include_fields"] = CandidateFields;
            return await client.GetFromJsonAsync<PlaylistHits>(
                QueryHelpers.AddQueryString(path, parameters),
                Json,
                ct
            );
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PlaylistHits>(Json, ct);
    }

    private static MatchEvidence SupportingMatch(string query, PlaylistHighlight[] highlights)
    {
        var fields = highlights
            .Where(highlight =>
                highlight.Field
                    is "description"
                        or "curator"
                        or "entry_notes"
                        or "guide_text"
                        or "artists"
                        or "songs"
            )
            .Select(highlight =>
                (
                    highlight.Field,
                    (string?)
                        string.Join(
                            ' ',
                            highlight.Snippets
                                ?? (highlight.Snippet == null ? [] : [highlight.Snippet])
                        ),
                    highlight.Field == "description" ? .45 : .32
                )
            )
            .ToArray();
        return MatchEvidence.Read(query, fields);
    }

    private sealed record PlaylistHits(PlaylistHit[]? Hits);

    private sealed record PlaylistHit(
        PlaylistCandidate? Document,
        PlaylistHighlight[]? Highlights,
        SearchTextMatch? TextMatchInfo
    );

    private sealed record PlaylistHighlight(string Field, string? Snippet, string[]? Snippets);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}

internal sealed record PlaylistCandidate(
    string Id,
    string HistoryGeneration,
    long Revision,
    bool Published,
    bool Archived,
    bool TakenDown,
    double PopularityPercentile = 0,
    MatchEvidence? SupportingMatch = null
);
