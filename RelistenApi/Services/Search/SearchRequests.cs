using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Relisten.Search;

internal static class SearchRequests
{
    private const string Fields =
        "id,kind,label,name,aliases,artist,artist_label,artist_slug,artist_slugs,date,day_key,place,run,country,city_label,venue_label,venue_id,show_id,tour_label,song_id,preview,soundboard,soundcheck,source_id,source_upstream,matrix,track_plays,review_mentions,longest,popularity";
    private const string Relevance = "_text_match:desc,popularity:desc";
    private const string Chronology = "date:desc," + Relevance;

    public static IReadOnlyList<SearchRequest> Build(
        SearchQuery query,
        SearchScope scope,
        bool typing,
        IReadOnlyCollection<string>? venueIds = null,
        int page = 1
    )
    {
        var requests = new List<SearchRequest>(4);
        if (scope is not (SearchScope.Sources or SearchScope.Reviews))
        {
            const string collection = SearchIndexSchema.Entities;
            var filters = Constraints(query, collection);
            if (EntityKindFilter(query, scope) is { } kind)
            {
                filters.Add(kind);
            }

            requests.Add(Create(query, collection, filters, page));
        }

        if (query.Terms.Length > 0 && scope is (SearchScope.All or SearchScope.Shows))
        {
            requests.Add(Performance(query, typing, venueIds, page));
        }

        // All typeahead omits evidence. A selected evidence scope still searches it.
        var allEvidence =
            scope == SearchScope.All && !typing && (query.Terms.Length > 0 || query.Matrix);
        if (allEvidence || scope == SearchScope.Sources)
        {
            const string collection = SearchIndexSchema.Recordings;
            requests.Add(Create(query, collection, Constraints(query, collection), page));
        }

        if (allEvidence || scope == SearchScope.Reviews)
        {
            const string collection = SearchIndexSchema.Reviews;
            requests.Add(Create(query, collection, Constraints(query, collection), page));
        }

        return requests;
    }

    private static SearchRequest Performance(
        SearchQuery query,
        bool typing,
        IReadOnlyCollection<string>? venueIds,
        int page
    )
    {
        const string collection = SearchIndexSchema.Performances;
        var filters = Constraints(query, collection);
        var terms = query.Terms;
        if (FindSongAlias(query) is { } alias)
        {
            // Whole terms only. A nickname can accompany a place or date.
            terms = Regex.Replace(terms, WholeTerm(alias.Alias), alias.Title);
            filters.Add($"artist_slug:={Literal(alias.Artist)}");
        }

        if (venueIds is { Count: > 0 })
        {
            filters.Add(Venues(venueIds));
        }

        var finalSpace = terms.LastIndexOf(' ');
        var exactLetter =
            typing && finalSpace >= 0 && terms.Length == finalSpace + 2 && char.IsLetter(terms[^1]);
        return Create(query, collection, filters, page) with
        {
            Q = terms,
            Prefix = exactLetter ? false : null,
        };
    }

    public static SearchRequest ScopedTracks(
        SearchQuery query,
        string? artist,
        IReadOnlyCollection<string> venueIds
    )
    {
        const string collection = SearchIndexSchema.Performances;
        var filters = Constraints(query with { SelectedArtist = artist }, collection);
        if (venueIds.Count > 0)
        {
            filters.Add(Venues(venueIds));
        }

        filters.Add("soundcheck:=false");
        return new SearchRequest(
            collection,
            "*",
            "name",
            Fields,
            "none",
            query.Chronological ? "date:desc,popularity:desc" : "popularity:desc,date:desc",
            6
        )
        {
            GroupBy = "show_id",
            GroupLimit = 1,
            FilterBy = JoinFilters(filters),
        };
    }

    public static SearchRunFallback? NamedRun(
        SearchQuery query,
        IReadOnlyList<SearchRequest> plan,
        IReadOnlyCollection<string>? venueIds = null
    )
    {
        if (
            query.HasDate
            || query.Month.HasValue
            || query.Tour != null
            || query.Country != null
            || query.Soundboard
            || query.Matrix
            || query.Terms.Length == 0
            || plan.FirstOrDefault(request => request.Collection == SearchIndexSchema.Performances)
                is not { } performance
        )
        {
            return null;
        }

        SearchRunFallback? candidate = null;
        foreach (var (artist, run, start, end) in SearchVocabularyRules.Runs)
        {
            if (
                (query.Artist != null && query.Artist != artist)
                || !query.Terms.EndsWith(" " + run, StringComparison.Ordinal)
            )
            {
                continue;
            }

            var wanted = query.Terms[..^(run.Length + 1)].Split(' ');
            if (wanted.Length is < 2 or > 5 || wanted[^1].Length < 3)
            {
                continue;
            }

            if (candidate != null)
            {
                return null;
            }

            var filters = new List<string>
            {
                $"artist_slug:={Literal(artist)}",
                DateRange(start, end),
            };
            if (venueIds is { Count: > 0 })
            {
                filters.Add(Venues(venueIds));
            }

            // Require both the run text and its dates before the ranker considers a correction.
            var request = performance with
            {
                Q = wanted[^1] + " " + run,
                FilterBy = JoinFilters(filters),
                NumTypos = 0,
                Prefix = false,
                PerPage = 10,
                Page = null,
            };
            candidate = new SearchRunFallback(request, run, wanted);
        }

        return candidate;
    }

    public static IReadOnlyList<SearchReply> Decode(
        JsonDocument response,
        IReadOnlyList<SearchRequest> plan
    )
    {
        try
        {
            return DecodeReplies(response, plan);
        }
        catch (Exception error)
            when (error
                    is InvalidOperationException
                        or KeyNotFoundException
                        or FormatException
                        or ArgumentException
            )
        {
            throw new InvalidDataException("Typesense returned an invalid search response.", error);
        }
    }

    private static IReadOnlyList<SearchReply> DecodeReplies(
        JsonDocument response,
        IReadOnlyList<SearchRequest> plan
    )
    {
        var results = response.RootElement.GetProperty("results");
        if (results.GetArrayLength() != plan.Count)
        {
            throw new InvalidDataException(
                "Typesense returned a different number of search results than requested."
            );
        }

        var replies = new SearchReply[plan.Count];
        for (var index = 0; index < plan.Count; index++)
        {
            var result = results[index];
            var collection = plan[index].Collection;
            if (result.TryGetProperty("code", out var code))
            {
                throw new InvalidDataException(
                    $"Typesense search of {collection} failed with code {code}: {result.GetProperty("error").GetString()}"
                );
            }

            var documents = SearchHits(result).Select(hit => DecodeHit(hit, collection)).ToArray();
            replies[index] = new SearchReply(
                collection,
                result.GetProperty("found").GetInt32(),
                documents
            );
        }

        return replies;
    }

    private static IEnumerable<JsonElement> SearchHits(JsonElement result)
    {
        if (result.TryGetProperty("grouped_hits", out var groups))
        {
            foreach (var group in groups.EnumerateArray())
            {
                foreach (var hit in group.GetProperty("hits").EnumerateArray())
                {
                    yield return hit;
                }
            }
        }
        else if (result.TryGetProperty("hits", out var hits))
        {
            foreach (var hit in hits.EnumerateArray())
            {
                yield return hit;
            }
        }
    }

    private static SearchDocument DecodeHit(JsonElement hit, string collection)
    {
        var document =
            hit.GetProperty("document").Deserialize<SearchDocument>(TypesenseClient.Json)
            ?? throw new InvalidDataException("Typesense returned an empty search document.");
        var highlights = new Dictionary<string, string>(StringComparer.Ordinal);
        if (hit.TryGetProperty("highlights", out var values))
        {
            foreach (var highlight in values.EnumerateArray())
            {
                var field = highlight.GetProperty("field").GetString()!;
                var snippet = highlight.TryGetProperty("snippet", out var value)
                    ? value.GetString()
                    : null;
                highlights[field] = snippet ?? "";
            }
        }

        return document with
        {
            Collection = collection,
            Score = hit.TryGetProperty("text_match", out var score) ? score.GetInt64() : 0,
            Highlights = highlights,
        };
    }

    private static SearchRequest Create(
        SearchQuery query,
        string collection,
        List<string> filters,
        int page
    )
    {
        var (fields, weights, highlights, size, group) = collection switch
        {
            SearchIndexSchema.Recordings => (
                "name,credits,notes,artist,place,run",
                "13,12,2,4,5,2",
                "notes,credits",
                12,
                true
            ),
            SearchIndexSchema.Reviews => (
                "name,review_text,artist,place,run",
                "8,3,3,4,2",
                "review_text",
                12,
                true
            ),
            SearchIndexSchema.Performances => (
                "name,aliases,artist,place,run",
                "12,10,3,4,4",
                "none",
                15,
                true
            ),
            _ => ("name,aliases,artist,place,run", "12,10,3,4,4", "none", 18, false),
        };

        var lastWordLength = query.Terms.Length - query.Terms.LastIndexOf(' ') - 1;
        var primary = collection is SearchIndexSchema.Entities or SearchIndexSchema.Performances;
        return new SearchRequest(
            collection,
            query.Terms.Length == 0 ? "*" : query.Terms,
            fields,
            Fields,
            highlights,
            SortBy(query, collection),
            size
        )
        {
            QueryByWeights = weights,
            DropTokensThreshold = 0,
            NumTypos = 2,
            EnableTyposForNumericalTokens = false,
            MinLen1typo = 4,
            MinLen2typo = 7,
            TypoTokensThreshold = 1,
            MaxCandidates = primary && lastWordLength is >= 2 and <= 4 ? 24 : 8,
            PrioritizeExactMatch = true,
            GroupBy = group ? "show_id" : null,
            GroupLimit = group ? 2 : null,
            FilterBy = JoinFilters(filters),
            Page = page > 1 ? page : null,
        };
    }

    private static string? EntityKindFilter(SearchQuery query, SearchScope scope) =>
        scope switch
        {
            SearchScope.Artists => "kind:=artist",
            SearchScope.Songs => "kind:=song",
            SearchScope.Venues => "kind:=venue",
            SearchScope.All or SearchScope.Shows when query.Terms.Length == 0 && query.HasDate =>
                "kind:=show",
            SearchScope.Shows => "kind:=[show,tour]",
            _ => null,
        };

    private static List<string> Constraints(SearchQuery query, string collection)
    {
        var filters = new List<string>();
        if (query.Artist is { Length: > 0 } artist)
        {
            var slug = Literal(artist);
            filters.Add(
                collection == SearchIndexSchema.Entities
                    ? $"(artist_slug:={slug} || artist_slugs:={slug})"
                    : $"artist_slug:={slug}"
            );
        }

        if (query.Country is { Length: > 0 } country)
        {
            filters.Add($"country:={Literal(country)}");
        }

        if (query.Date is { } date)
        {
            filters.Add("date:=" + date.ToString(CultureInfo.InvariantCulture));
        }
        else if (query.Year is { } year)
        {
            var firstMonth = query.Month ?? 1;
            var lastMonth = query.Month ?? 12;
            var start = year * 10_000 + firstMonth * 100 + 1;
            var end = year * 10_000 + lastMonth * 100 + DateTime.DaysInMonth(year, lastMonth);
            filters.Add(DateRange(start, end));
        }
        else if (query.Month is { } month)
        {
            // Removing a year chip can leave its month selected across years.
            filters.Add(
                FormattableString.Invariant($"day_key:[{month * 100 + 1}..{month * 100 + 31}]")
            );
        }

        if (query.DayKey is { } day)
        {
            filters.Add("day_key:=" + day.ToString(CultureInfo.InvariantCulture));
        }

        if (
            query.Soundboard
            && collection is SearchIndexSchema.Performances or SearchIndexSchema.Recordings
        )
        {
            filters.Add("soundboard:=true");
        }

        if (query.Matrix && collection == SearchIndexSchema.Recordings)
        {
            filters.Add("matrix:=true");
        }

        if (query.Tour is { Length: > 0 } tour)
        {
            filters.Add($"run:={Literal(tour)}");
        }

        return filters;
    }

    private static string SortBy(SearchQuery query, string collection)
    {
        if (
            query.Chronological
            && collection is SearchIndexSchema.Entities or SearchIndexSchema.Performances
        )
        {
            return Chronology;
        }

        if (collection == SearchIndexSchema.Performances)
        {
            var first = query.Order switch
            {
                "longest" => "longest",
                "last" => "date",
                "popular" => "track_plays",
                _ => null,
            };
            if (first != null)
            {
                return $"{first}:desc,{Relevance}";
            }
        }

        if (query.Terms.Length > 0)
        {
            return Relevance;
        }

        return query.DayKey.HasValue ? "date:desc,popularity:desc" : "popularity:desc";
    }

    private static (string Artist, string Alias, string Title)? FindSongAlias(SearchQuery query)
    {
        (string Artist, string Alias, string Title)? result = null;
        foreach (var alias in SearchVocabularyRules.SongAliases)
        {
            if (
                (query.Artist != null && query.Artist != alias.Artist)
                || Regex.IsMatch(query.Terms, WholeTerm(alias.Title))
                || !Regex.IsMatch(query.Terms, WholeTerm(alias.Alias))
            )
            {
                continue;
            }

            if (result != null)
            {
                return null;
            }

            result = alias;
        }

        return result;
    }

    private static string WholeTerm(string term) => $@"\b{Regex.Escape(term)}\b";

    private static string DateRange(int start, int end) =>
        FormattableString.Invariant($"date:[{start}..{end}]");

    private static string Literal(string value) => "`" + value + "`";

    private static string Venues(IEnumerable<string> ids) =>
        $"venue_id:=[{string.Join(',', ids.Order(StringComparer.Ordinal).Select(Literal))}]";

    private static string? JoinFilters(List<string> filters) =>
        filters.Count == 0 ? null : string.Join(" && ", filters);
}

internal sealed record SearchReply(
    string Collection,
    int Found,
    IReadOnlyList<SearchDocument> Documents
);

internal sealed record SearchRunFallback(SearchRequest Request, string Run, string[] Wanted);

internal sealed record SearchRequest(
    string Collection,
    string Q,
    string QueryBy,
    string IncludeFields,
    string HighlightFields,
    string SortBy,
    int PerPage
)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QueryByWeights { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DropTokensThreshold { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NumTypos { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableTyposForNumericalTokens { get; init; }

    [JsonPropertyName("min_len_1typo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinLen1typo { get; init; }

    [JsonPropertyName("min_len_2typo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinLen2typo { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TypoTokensThreshold { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxCandidates { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PrioritizeExactMatch { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GroupBy { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? GroupLimit { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FilterBy { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Prefix { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Page { get; init; }
}
