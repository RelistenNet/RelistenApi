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
        "id,kind,label,name,aliases,artist,artist_label,artist_slug,artist_slugs,date,day_key,place,run,country,city_label,location_label,venue_label,venue_id,show_id,tour_label,song_id,preview,soundboard,soundcheck,source_id,source_upstream,taper_label,transferrer_label,matrix,track_plays,avg_rating,num_reviews,review_mentions,longest,popularity,popularity_percentile";
    private const string Relevance = "_text_match:desc,popularity:desc";
    private const string Chronology = "date:desc," + Relevance;
    private const int ReviewExcerptLength = 240;

    public static IReadOnlyList<SearchRequest> Build(
        SearchQuery query,
        SearchScope scope,
        bool typing,
        IReadOnlyCollection<string>? venueIds = null,
        int page = 1,
        bool includeAllEvidence = true,
        IReadOnlyCollection<string>? reviewVenueIds = null
    )
    {
        var requests = new List<SearchRequest>(4);
        if (scope is not (SearchScope.Sources or SearchScope.Reviews))
        {
            const string collection = SearchIndexSchema.Entities;
            var filters = Constraints(query, collection);
            if (query.Reading is ("selected_place" or "place_alias") && venueIds is { Count: > 0 })
            {
                filters.Add(Venues(venueIds));
            }
            if (EntityKindFilter(query, scope) is { } kind)
            {
                filters.Add(kind);
            }

            requests.Add(Create(query, collection, filters, page));
        }

        if (query.Terms.Length > 0 && scope is (SearchScope.All or SearchScope.Shows))
        {
            requests.Add(
                Performance(query, typing, venueIds, page, titleOnly: scope == SearchScope.Shows)
            );
        }

        // Artist browsing and typeahead omit recording and review evidence.
        // A selected evidence scope still searches it.
        var allEvidence =
            scope == SearchScope.All
            && !typing
            && includeAllEvidence
            && (query.Terms.Length > 0 || query.Matrix);
        if (allEvidence || scope == SearchScope.Sources)
        {
            const string collection = SearchIndexSchema.Recordings;
            var filters = Constraints(query, collection);
            if (query.Reading is ("selected_place" or "place_alias") && venueIds is { Count: > 0 })
            {
                filters.Add(Venues(venueIds));
            }
            var recordings = Create(query, collection, filters, page);
            // All renders one show per matched recording group.
            requests.Add(
                scope == SearchScope.Sources
                    ? recordings with
                    {
                        GroupBy = null,
                        GroupLimit = null,
                    }
                    : recordings with
                    {
                        GroupLimit = 1,
                    }
            );
        }

        // Artist and date may narrow review search, but browsing a show does
        // not establish that any of its reviews match the query.
        if (query.Terms.Length > 0 && (allEvidence || scope == SearchScope.Reviews))
        {
            const string collection = SearchIndexSchema.Reviews;
            var filters = Constraints(query, collection);
            var reviewVenues =
                reviewVenueIds ?? (query.Reading == "selected_place" ? venueIds : null);
            if (reviewVenues is { Count: > 0 })
            {
                // Cross-show mentions can displace the venue's own reviews.
                // Filter before applying the result limit.
                filters.Add(Venues(reviewVenues));
            }

            requests.Add(Create(query, collection, filters, page));
        }

        return requests;
    }

    public static SearchRequest PopularArtistShows(SearchQuery query, string artist) =>
        PopularShows(query with { Terms = "", SelectedArtist = artist }, [], 6);

    public static IEnumerable<SearchRequest> ReviewedSong(
        SearchInput input,
        SearchQuery query,
        ReviewedSongReading reading
    )
    {
        if (
            input.Scope is (SearchScope.All or SearchScope.Songs)
            && (!query.HasDate || reading.ReplacesYear)
            && (reading.Venues.Count == 0 || input.Scope == SearchScope.Songs)
        )
        {
            // For a named place, a performance answers where the song was
            // played. A standalone song card cannot establish that match.
            var ids = reading
                .Targets.Select(song => song.EntityId)
                .OfType<string>()
                .Distinct()
                .ToArray();
            if (ids.Length > 0)
            {
                yield return Create(
                    query with
                    {
                        Terms = "",
                    },
                    SearchIndexSchema.Entities,
                    [$"kind:=song && id:=[{string.Join(',', ids.Select(Literal))}]"],
                    input.Page
                ) with
                {
                    Optional = true,
                };
            }
        }

        if (input.Scope is SearchScope.All or SearchScope.Shows)
        {
            var performance = reading.ReplacesYear ? query with { Year = null } : query;
            var filters = Constraints(performance, SearchIndexSchema.Performances);
            filters.Add(ReviewedPerformances(reading.Targets));
            if (reading.Venues.Count > 0)
            {
                filters.Add(Venues(reading.Venues));
            }
            yield return Create(
                performance with
                {
                    Terms = "",
                },
                SearchIndexSchema.Performances,
                filters,
                input.Page
            ) with
            {
                SortBy = SortBy(performance, SearchIndexSchema.Performances),
                PerPage = input.AddTracks && !input.Typing ? 40 : 15,
                Optional = true,
            };
        }
    }

    private static string ReviewedPerformances(SearchReviewedNicknames.SongTarget[] targets)
    {
        var ids = targets
            .Where(song => !song.MatchTrackTitle)
            .Select(song => song.EntityId!)
            .Distinct()
            .ToArray();
        var titles = targets
            .Where(song => song.MatchTrackTitle)
            .GroupBy(song => song.Title)
            .Select(group =>
                $"(name:={Literal(group.Key)} && artist_slug:=[{string.Join(',', group.Select(song => song.Artist).Distinct().Select(Literal))}])"
            );
        var matches =
            ids.Length == 0
                ? titles
                : titles.Prepend($"song_id:=[{string.Join(',', ids.Select(Literal))}]");
        return $"({string.Join(" || ", matches)})";
    }

    public static SearchRequest ShowsById(IReadOnlyCollection<string> showIds) =>
        new(
            SearchIndexSchema.Entities,
            "*",
            "name",
            Fields,
            "none",
            "popularity:desc",
            showIds.Count
        )
        {
            FilterBy = $"kind:=show && id:=[{string.Join(',', showIds.Select(Literal))}]",
            Optional = true,
        };

    public static SearchRequest PopularPlaceShows(
        SearchQuery query,
        IReadOnlyCollection<string> venues
    ) => PopularShows(query with { Terms = "" }, venues, 8);

    public static SearchRequest DatedShowsAtNamedVenue(SearchQuery query, string venue)
    {
        var filters = Constraints(query, SearchIndexSchema.Entities);
        filters.Add("kind:=show");
        // An imported show can use a different venue ID for the same indexed name.
        filters.Add($"name:={Literal(venue)}");
        return Create(query with { Terms = "" }, SearchIndexSchema.Entities, filters, 1) with
        {
            QueryBy = "name",
            QueryByWeights = null,
            Prefix = false,
            NumTypos = 0,
            PerPage = 8,
            Optional = true,
        };
    }

    public static SearchRequest? PopularRunShows(SearchQuery query, SearchVocabulary vocabulary)
    {
        foreach (var (artist, run, first, last) in SearchReviewedNamedShows.Runs)
        {
            if (query.Terms == run && (query.Artist == null || query.Artist == artist))
            {
                return PopularShows(
                    query with
                    {
                        Terms = "",
                        SelectedArtist = artist,
                    },
                    [],
                    6,
                    DateRange(first, last)
                );
            }
        }

        var tour = query.Tour ?? query.Terms;
        return
            (query.Tour == null || query.Terms.Length == 0) && vocabulary.TourNames.Contains(tour)
            ? PopularShows(query with { Terms = "", Tour = tour }, [], 6)
            : null;
    }

    private static SearchRequest PopularShows(
        SearchQuery query,
        IReadOnlyCollection<string> venues,
        int limit,
        string? runDates = null
    )
    {
        const string collection = SearchIndexSchema.Entities;
        var filters = Constraints(query, collection);
        filters.Add("kind:=show");
        if (venues.Count > 0)
        {
            filters.Add(Venues(venues));
        }
        if (runDates != null)
        {
            filters.Add(runDates);
        }

        return Create(query, collection, filters, page: 1) with
        {
            SortBy = "popularity:desc,date:desc",
            PerPage = limit,
            Optional = true,
        };
    }

    public static SearchRequest? WidenEmptyPerformancePrefix(
        SearchInput input,
        IReadOnlyList<SearchRequest> primary
    )
    {
        if (!input.Typing || input.Scope != SearchScope.All || input.Page != 1)
        {
            return null;
        }

        // A short suffix has many possible completions across the catalog. When
        // none joined the other words, try more completions with the same filters.
        var performance = primary.FirstOrDefault(request =>
            request.Collection == SearchIndexSchema.Performances
        );
        if (performance is not { Prefix: not false, Q: var text })
        {
            return null;
        }

        var space = text.LastIndexOf(' ');
        return space >= 0 && text.Length - space - 1 is >= 2 and <= 4
            ? performance with
            {
                MaxCandidates = 256,
            }
            : null;
    }

    private static SearchRequest Performance(
        SearchQuery query,
        bool typing,
        IReadOnlyCollection<string>? venueIds,
        int page,
        bool titleOnly
    )
    {
        const string collection = SearchIndexSchema.Performances;
        var filters = Constraints(query, collection);
        var terms = query.Terms;
        if (query.Reading != "literal" && FindSongAlias(query) is { } alias)
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
            QueryBy = titleOnly ? "name,aliases" : "name,aliases,artist,place,run",
            QueryByWeights = titleOnly ? "12,10" : "12,10,3,4,4",
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

    public static PlaceSongMatch? ReadPlaceSong(
        SearchInput input,
        SearchQuery query,
        SearchVocabulary vocabulary
    )
    {
        if (
            (input.Scope != SearchScope.Reviews && input is not { Scope: SearchScope.All, Page: 1 })
            || input.Ignore?.Contains("venue") == true
            || input.ReadingId == "literal"
            || query.Order != "regular"
            || query.Chronological
            || query.Matrix
            || query.Reading
                is ("artist_identity" or "artist_name_prefix" or "verified_passage_with_place")
        )
        {
            return null;
        }

        return vocabulary.ExactPlaceSong(query.Terms, query.Artist)
            ?? (
                input.Scope == SearchScope.All && !query.HasDate
                    ? vocabulary.PlaceSongPrefix(query.Terms, query.Artist)
                    : null
            );
    }

    public static SearchRequest PlaceSongPerformances(SearchQuery query, PlaceSongMatch match)
    {
        var filters = Constraints(query, SearchIndexSchema.Performances);
        filters.Add($"song_id:=[{string.Join(',', match.SongIds.Select(Literal))}]");
        filters.Add(Venues(match.VenueIds));
        filters.Add("soundcheck:=false");
        return new SearchRequest(
            SearchIndexSchema.Performances,
            "*",
            "name",
            Fields,
            "none",
            "track_plays:desc,popularity:desc",
            3
        )
        {
            FilterBy = JoinFilters(filters),
            GroupBy = "show_id",
            GroupLimit = 1,
            Optional = true,
        };
    }

    public static SearchRequest RelatedSongVersions(SearchQuery query, PlaceSongMatch match)
    {
        // Keep the direct show ahead of these versions. A few hits per artist
        // give other bands' takes a chance to appear after the direct matches.
        var related = query with
        {
            Terms = $"{match.Title} {match.Place}",
            Year = null,
            Month = null,
            DayKey = null,
        };
        var filters = Constraints(related, SearchIndexSchema.Performances);
        filters.Add("soundcheck:=false");
        return Create(related, SearchIndexSchema.Performances, filters, page: 1) with
        {
            QueryBy = "name,aliases",
            QueryByWeights = "12,10",
            DropTokensThreshold = 8,
            NumTypos = 0,
            Prefix = false,
            PerPage = 8,
            GroupBy = "artist_slug",
            GroupLimit = 2,
            Optional = true,
        };
    }

    public static SearchRequest SongAtRecordingShows(
        SearchQuery query,
        string songId,
        IReadOnlyList<string> showIds
    )
    {
        var filters = Constraints(query, SearchIndexSchema.Performances);
        filters.Add($"song_id:={Literal(songId)}");
        filters.Add($"show_id:=[{string.Join(',', showIds.Select(Literal))}]");
        filters.Add("soundcheck:=false");
        return new SearchRequest(
            SearchIndexSchema.Performances,
            "*",
            "name",
            Fields,
            "none",
            "track_plays:desc,popularity:desc",
            showIds.Count
        )
        {
            FilterBy = JoinFilters(filters),
            GroupBy = "show_id",
            GroupLimit = 1,
            Optional = true,
        };
    }

    public static SearchRequest? PopularExactSong(
        SearchInput input,
        SearchQuery query,
        IReadOnlyCollection<string> venues,
        SearchVocabulary vocabulary
    )
    {
        if (
            input is not { Scope: SearchScope.All, Page: 1 }
            || query.Order != "regular"
            || query.Chronological
            || query.Matrix
        )
        {
            return null;
        }

        var songs = vocabulary.ExactSongIds(query.Terms, query.Artist);
        if (songs.Length == 0)
        {
            return null;
        }
        var focused =
            songs.Length == 1
            && (
                query.Artist != null
                || !vocabulary
                    .Lookup(query.Terms, kind: "artist")
                    .Any(candidate => candidate.CatalogQuality >= 3)
            );

        // Typesense text scores for the same song can differ by recording
        // metadata. Search by song ID to give well-played versions a candidate slot.
        var filters = Constraints(query, SearchIndexSchema.Performances);
        filters.Add($"song_id:=[{string.Join(',', songs.Select(Literal))}]");
        filters.Add("soundcheck:=false");
        if (venues.Count > 0)
        {
            filters.Add(Venues(venues));
        }

        return new SearchRequest(
            SearchIndexSchema.Performances,
            "*",
            "name",
            Fields,
            "none",
            "track_plays:desc,popularity:desc",
            focused ? (input.Typing ? 12 : 36) : (input.AddTracks && !input.Typing ? 16 : 4)
        )
        {
            FilterBy = JoinFilters(filters),
            GroupBy = "show_id",
            GroupLimit = 1,
            Optional = true,
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
        foreach (var (artist, run, start, end) in SearchReviewedNamedShows.Runs)
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
                Q = performance.QueryBy == "name,aliases" ? wanted[^1] : wanted[^1] + " " + run,
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
            var request = plan[index];
            var collection = request.Collection;
            if (result.TryGetProperty("code", out var code))
            {
                ThrowIfCollectionMissing(result);

                if (request.Optional || code.GetInt32() == 404)
                {
                    replies[index] = new SearchReply(collection, 0, [], Rejected: true);
                    continue;
                }

                throw new InvalidDataException(
                    $"Typesense search of {collection} failed with code {code}: {result.GetProperty("error").GetString()}"
                );
            }

            var documents = SearchHits(result)
                .Select(hit => DecodeHit(hit, request))
                .OfType<SearchDocument>()
                .ToArray();
            replies[index] = new SearchReply(
                collection,
                result.GetProperty("found").GetInt32(),
                documents,
                FoundDocuments: result.TryGetProperty("found_docs", out var foundDocs)
                    ? foundDocs.GetInt32()
                    : null,
                KindCounts: ReadKindFacets(result)
            );
        }

        return replies;
    }

    private static Dictionary<string, int>? ReadKindFacets(JsonElement result)
    {
        if (!result.TryGetProperty("facet_counts", out var facets))
        {
            return null;
        }

        foreach (var facet in facets.EnumerateArray())
        {
            if (facet.GetProperty("field_name").GetString() == "kind")
            {
                return facet
                    .GetProperty("counts")
                    .EnumerateArray()
                    .ToDictionary(
                        count => count.GetProperty("value").GetString()!,
                        count => count.GetProperty("count").GetInt32()
                    );
            }
        }
        return null;
    }

    internal static void ThrowIfCollectionMissing(JsonElement result)
    {
        if (
            result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.Number
            && code.GetInt32() == 404
            && result.TryGetProperty("error", out var message)
            && message.ValueKind == JsonValueKind.String
            && string.Equals(
                message.GetString(),
                "Collection not found",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            throw new SearchCollectionMissingException();
        }
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

    private static SearchDocument? DecodeHit(JsonElement hit, SearchRequest request)
    {
        var collection = request.Collection;
        var indexed = hit.GetProperty("document");
        var document =
            indexed.Deserialize<SearchDocument>(TypesenseClient.Json)
            ?? throw new InvalidDataException("Typesense returned an empty search document.");
        var reviewText =
            collection == SearchIndexSchema.Reviews ? ReviewText(indexed, document) : null;
        if (collection == SearchIndexSchema.Reviews && reviewText == null)
        {
            return null;
        }

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
            TextMatch =
                request.Q != "*"
                && request.NumTypos != 0
                && hit.TryGetProperty("text_match_info", out var match)
                    ? match.Deserialize<SearchTextMatch>(TypesenseClient.Json)
                    : null,
            Highlights = highlights,
            // A legacy title-only review may match name and have no body highlight.
            // The preview is for display and does not contribute ranking evidence.
            ReviewExcerpt =
                reviewText != null
                && string.IsNullOrWhiteSpace(highlights.GetValueOrDefault("review_text"))
                    ? ReviewExcerpt(reviewText)
                    : null,
        };
    }

    private static string? ReviewText(JsonElement indexed, SearchDocument document)
    {
        var text = indexed.TryGetProperty("review_text", out var value) ? value.GetString() : null;
        return new[] { text, document.Label, document.Name }.FirstOrDefault(part =>
            !string.IsNullOrWhiteSpace(part)
        );
    }

    private static string ReviewExcerpt(string text)
    {
        text = text.Trim();
        if (text.Length <= ReviewExcerptLength)
        {
            return text;
        }

        var end = char.IsHighSurrogate(text[ReviewExcerptLength - 1])
            ? ReviewExcerptLength - 1
            : ReviewExcerptLength;
        return text[..end].TrimEnd() + "…";
    }

    private static SearchRequest Create(
        SearchQuery query,
        string collection,
        List<string> filters,
        int page
    )
    {
        // A title or alias identifies the item. Artist, venue and run add
        // context. Credits identify recordings directly, while free text in
        // notes and reviews is supporting evidence.
        var (fields, weights, highlights, size, group) = collection switch
        {
            SearchIndexSchema.Recordings => (
                "name,credits,notes,artist,place,run",
                "13,12,2,4,5,2",
                "notes,credits",
                12,
                true
            ),
            SearchIndexSchema.Reviews => ("name,review_text", "8,3", "review_text", 12, true),
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
            collection == SearchIndexSchema.Reviews ? Fields + ",review_text" : Fields,
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
            SearchScope.All
            or SearchScope.Shows
                when query.Terms.Length == 0
                    && (query.HasDate || query.Reading is ("selected_place" or "place_alias")) =>
                "kind:=show",
            SearchScope.Shows => "kind:=[show,tour]",
            // Track resources for marked passages are fetched by ID only.
            SearchScope.All => "kind:!=passage_track",
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
        foreach (var alias in SearchReviewedNicknames.CompoundSongAliases)
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

    internal static string Names(IEnumerable<string> names) =>
        $"name:=[{string.Join(',', names.Order(StringComparer.Ordinal).Select(Literal))}]";

    private static string Venues(IEnumerable<string> ids) =>
        $"venue_id:=[{string.Join(',', ids.Order(StringComparer.Ordinal).Select(Literal))}]";

    private static string? JoinFilters(List<string> filters) =>
        filters.Count == 0 ? null : string.Join(" && ", filters);
}

internal sealed record SearchReply(
    string Collection,
    int Found,
    IReadOnlyList<SearchDocument> Documents,
    bool Rejected = false,
    int? FoundDocuments = null,
    Dictionary<string, int>? KindCounts = null
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
    [JsonIgnore]
    public bool Optional { get; init; }

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
    public int? GroupMaxCandidates { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FilterBy { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Prefix { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Page { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Offset { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Limit { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FacetBy { get; init; }
}

internal sealed class SearchCollectionMissingException : Exception;
