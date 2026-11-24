using System.Text.Json;

namespace Relisten.Search;

public static class SearchJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}

public sealed record SearchChip(string Key, string Label, string Value);

public sealed record SearchReading(string Id, IReadOnlyList<SearchChip> Chips);

public sealed record SearchReadings(
    SearchReading Applied,
    IReadOnlyList<SearchReading> Alternatives
)
{
    public static readonly SearchReadings Empty = new(new("query", []), []);
}

public sealed record SearchAvailability(string Catalog, string Playlists);

public record SearchPage(
    string Q,
    string? RequestId,
    string Scope,
    SearchAvailability Availability,
    IReadOnlyDictionary<string, int> Counts,
    SearchReadings Readings,
    int? NextPage,
    string[] MoreScopes
)
{
    /// <summary>Minimum totals for tabs whose complete result count was unavailable.</summary>
    public IReadOnlyDictionary<string, int> CountLowerBounds { get; init; } =
        new Dictionary<string, int>();
}

public sealed record SearchResponse : SearchPage
{
    public SearchResponse(
        string q,
        string? requestId,
        string scope,
        SearchAvailability availability,
        IReadOnlyDictionary<string, int> counts,
        SearchReadings readings,
        int? nextPage,
        string[] moreScopes,
        SearchResult[] results
    )
        : base(q, requestId, scope, availability, counts, readings, nextPage, moreScopes)
    {
        Results = results;
    }

    public SearchResult[] Results { get; init; }

    public static SearchResponse From(SearchPage page, SearchResult[] results) =>
        new(
            page.Q,
            page.RequestId,
            page.Scope,
            page.Availability,
            page.Counts,
            page.Readings,
            page.NextPage,
            page.MoreScopes,
            results
        )
        {
            CountLowerBounds = page.CountLowerBounds,
        };
}

/// <summary>Validated catalog constraints that also affect playlist search.</summary>
/// <param name="ArtistId">The explicitly selected catalog artist, if any.</param>
/// <param name="Date">An enforced full date in yyyyMMdd form.</param>
/// <param name="PlaceSelected">Whether an applied reading restricts the results to a place.</param>
public sealed record SearchConstraints(string? ArtistId, int? Date, bool PlaceSelected);

public sealed record SearchCandidate(SearchResult Result, SearchOrder Order);

/// <param name="Page">The public page metadata.</param>
/// <param name="Constraints">Validated constraints for the playlist half of search.</param>
/// <param name="Candidates">Renderable catalog rows in catalog ranking order.</param>
/// <param name="PageLimit">The number of rows the User Service may return after merging All results.</param>
public sealed record CatalogSearchEnvelope(
    SearchPage Page,
    SearchConstraints Constraints,
    SearchCandidate[] Candidates,
    int PageLimit
);

/// <summary>Internal ranking exchange between the Catalog API and User Service.</summary>
public static class CatalogSearchExchange
{
    public const string Path = "/internal/search/candidates";
    public const string Header = "X-Relisten-Catalog-Key";
    public const string KeySetting = "CATALOG_SERVICE_SEARCH_KEY";
    public const string DevelopmentKey = "local_dev_search_exchange";
}
