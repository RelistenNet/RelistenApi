using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Relisten.Search;

/// <summary>Coordinates one search request against the current catalog aliases.</summary>
public sealed class CatalogSearchCoordinator
{
    // Leave room for a recovery request and playable browsing after the primary
    // multi-search, while capping the serial work each keystroke can trigger.
    private const int ExtraSearchLimit = 2;

    private readonly record struct PlaybackContext(string? Artist, bool Promote, bool Browse);

    private readonly TypesenseClient _typesense;
    private readonly CatalogQueryEvidenceStore _evidence;
    private readonly ILogger<CatalogSearchCoordinator> _logger;
    private readonly string _prefix;

    internal CatalogSearchCoordinator(
        TypesenseClient typesense,
        CatalogQueryEvidenceStore evidence,
        ILogger<CatalogSearchCoordinator> logger,
        string prefix
    )
    {
        _typesense = typesense;
        _evidence = evidence;
        _logger = logger;
        _prefix = prefix;
    }

    internal async Task<SearchResponse> Search(
        SearchInput input,
        CancellationToken cancellationToken
    )
    {
        var evidence = _evidence.Current;
        var query = SearchInterpretationReader.Read(input, evidence);
        var conflictingCountry = input.Country != null && query.Country != input.Country;
        if (conflictingCountry)
        {
            query = query with { Country = input.Country };
        }

        var reviewsAvailable = evidence.HasReviews(query.Artist ?? query.ReadingArtist);
        var venues = RecognizedVenues(input, query, evidence.Vocabulary);
        var interpretation = query.Interpretation(Chips(query, evidence.Vocabulary, venues));

        if (evidence.Collection == null)
        {
            return Response(input, interpretation, reviewsAvailable, "unavailable");
        }

        if (
            input.ArtistId != null && query.SelectedArtist == null
            || input.Scope == SearchScope.Reviews && !reviewsAvailable
            || conflictingCountry
        )
        {
            return Response(input, interpretation, reviewsAvailable, "full");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // A stalled engine should release typeahead requests quickly. A submit has
        // more time to fetch the recording and review groups with the primary hits.
        deadline.CancelAfter(input.Typing ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(5));
        try
        {
            return await Run(
                input,
                query,
                evidence,
                venues,
                reviewsAvailable,
                interpretation,
                deadline.Token
            );
        }
        catch (Exception error)
            when (!cancellationToken.IsCancellationRequested
                && error
                    is HttpRequestException
                        or OperationCanceledException
                        or InvalidDataException
                        or System.Text.Json.JsonException
            )
        {
            _logger.LogWarning(error, "Catalog search is temporarily unavailable");
            return Response(input, interpretation, reviewsAvailable, "unavailable");
        }
    }

    internal async Task<SearchPerformance?> Files(string key, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var alias = Alias(SearchIndexSchema.Performances);
        var document = await _typesense.ReadDocument(alias, key, deadline.Token);
        if (document is not { Kind: "performance" })
        {
            if (await _typesense.ReadAlias(alias, deadline.Token) == null)
            {
                throw new InvalidDataException("The performance search alias is unavailable.");
            }

            return null;
        }

        return new SearchPerformance(
            document.Id,
            document.ShowId,
            document.SongId,
            document.Label,
            (document.Files ?? [])
                .GroupBy(file => file.SourceId)
                .Select(source => new SearchPerformanceSource(
                    source.Key,
                    source.OrderBy(file => file.SetIndex).ThenBy(file => file.Position).ToArray()
                ))
                .ToArray()
        );
    }

    private async Task<SearchResponse> Run(
        SearchInput input,
        SearchQuery query,
        CatalogQueryEvidence evidence,
        IReadOnlySet<string> venues,
        bool reviewsAvailable,
        SearchInterpretation interpretation,
        CancellationToken cancellationToken
    )
    {
        var playback =
            input.AddTracks && input.Scope is SearchScope.All or SearchScope.Shows
                ? ReadPlaybackContext(query, evidence.Vocabulary, venues)
                : default;
        var plan = Plan(input, query, venues, reviewsAvailable, playback);
        var replies = await Execute(plan, cancellationToken);

        var visible = PrimaryResults(input, query, evidence, plan, replies);
        var extraSearches = ExtraSearchLimit;
        if (visible.Count == 0)
        {
            (visible, extraSearches) = await RecoverEmptyResults(
                input,
                query,
                evidence,
                venues,
                plan,
                cancellationToken
            );
        }

        if (playback.Promote)
        {
            visible = await PrioritizePlayable(
                input,
                query,
                playback,
                venues,
                visible,
                extraSearches,
                cancellationToken
            );
        }

        return DecorateResults(
            input,
            query,
            evidence,
            reviewsAvailable,
            interpretation,
            visible,
            replies
        );
    }

    private async Task<(
        IReadOnlyList<SearchDocument> Results,
        int SearchesLeft
    )> RecoverEmptyResults(
        SearchInput input,
        SearchQuery query,
        CatalogQueryEvidence evidence,
        IReadOnlySet<string> venues,
        IReadOnlyList<SearchRequest> plan,
        CancellationToken cancellationToken
    )
    {
        IReadOnlyList<SearchDocument> visible = [];
        var extraSearches = ExtraSearchLimit;
        if (input.Page == 1 && SearchRequests.NamedRun(query, plan, venues) is { } run)
        {
            var fallback = await Execute([run.Request], cancellationToken);
            visible = SearchRanker.SuggestNamedRun(run, fallback[0].Documents);
            extraSearches--;
        }

        if (
            visible.Count == 0
            && extraSearches > 0
            && await RestoreEmptyPrefix(input, query, evidence, venues, plan, cancellationToken)
                is { } restored
        )
        {
            visible = restored;
            extraSearches--;
        }

        if (
            visible.Count == 0
            && extraSearches > 0
            && SearchRequests.WidenEmptyPerformancePrefix(input, plan) is { } wider
        )
        {
            var replies = await Execute([wider], cancellationToken);
            visible = Primary(input, query, evidence, [wider], replies);
            extraSearches--;
        }

        return (visible, extraSearches);
    }

    private static SearchResponse DecorateResults(
        SearchInput input,
        SearchQuery query,
        CatalogQueryEvidence evidence,
        bool reviewsAvailable,
        SearchInterpretation interpretation,
        IReadOnlyList<SearchDocument> visible,
        IReadOnlyList<SearchReply> replies
    )
    {
        var artistIds = evidence.Vocabulary.ArtistDocuments;
        var decorated = visible
            .Select(result =>
                artistIds.TryGetValue(result.ArtistSlug, out var artist)
                    ? result with
                    {
                        ArtistId = artist.Id,
                    }
                    : result
            )
            .ToArray();

        var passages = input.Scope is SearchScope.All or SearchScope.Shows
            ? evidence.Passages.Find(query)
            : [];
        return Response(
            input,
            interpretation,
            reviewsAvailable,
            "full",
            decorated,
            replies,
            passages
        );
    }

    private static IReadOnlyList<SearchRequest> Plan(
        SearchInput input,
        SearchQuery query,
        IReadOnlySet<string> venues,
        bool reviewsAvailable,
        PlaybackContext playback
    )
    {
        var requests = string.IsNullOrWhiteSpace(input.Text)
            ? []
            : SearchRequests.Build(query, input.Scope, input.Typing, venues, input.Page);
        if (input.Scope == SearchScope.Shows && playback.Browse)
        {
            // The browse results need their own stable upstream page. Adding them
            // after the first page would skip or repeat later performance groups.
            var browse = SearchRequests.ScopedTracks(query, playback.Artist, venues) with
            {
                Page = input.Page,
            };
            requests =
            [
                .. requests.Where(request => request.Collection != SearchIndexSchema.Performances),
                browse,
            ];
        }

        return reviewsAvailable
            ? requests
            : requests.Where(request => request.Collection != SearchIndexSchema.Reviews).ToArray();
    }

    private static IReadOnlyList<SearchDocument> PrimaryResults(
        SearchInput input,
        SearchQuery query,
        CatalogQueryEvidence evidence,
        IReadOnlyList<SearchRequest> plan,
        IReadOnlyList<SearchReply> replies
    )
    {
        var visible = Primary(input, query, evidence, plan, replies);
        return input.Page == 1 && input.Scope is SearchScope.All or SearchScope.Shows
            ? SearchRanker.AddTourRoute(query, evidence.Vocabulary, visible)
            : visible;
    }

    private static IReadOnlyList<SearchDocument> Primary(
        SearchInput input,
        SearchQuery query,
        CatalogQueryEvidence evidence,
        IReadOnlyList<SearchRequest> plan,
        IReadOnlyList<SearchReply> replies
    )
    {
        if (input.Scope is SearchScope.Sources or SearchScope.Reviews)
        {
            return replies.SelectMany(reply => reply.Documents).ToArray();
        }

        var performanceTerms = plan.FirstOrDefault(request =>
            request.Collection == SearchIndexSchema.Performances
        )?.Q;
        var rankTerms = performanceTerms is not (null or "*") ? performanceTerms : query.Terms;
        var candidates = replies
            .Where(reply =>
                reply.Collection is SearchIndexSchema.Entities or SearchIndexSchema.Performances
            )
            .SelectMany(reply => reply.Documents);
        return SearchRanker.Primary(
            query,
            rankTerms,
            input.Scope,
            SearchRanker.KnownItems(query, input, evidence),
            candidates,
            input.Page,
            browsePerformances: input.Scope == SearchScope.Shows && performanceTerms == "*"
        );
    }

    private async Task<IReadOnlyList<SearchDocument>?> RestoreEmptyPrefix(
        SearchInput input,
        SearchQuery query,
        CatalogQueryEvidence evidence,
        IReadOnlySet<string> venues,
        IReadOnlyList<SearchRequest> plan,
        CancellationToken cancellationToken
    )
    {
        if (!input.Typing || input.Page != 1 || !plan.Any(request => request.Prefix == false))
        {
            return null;
        }

        var text = input.Text.Trim();
        var space = text.LastIndexOf(' ');
        if (space < 0 || space != text.Length - 2 || !char.IsLetter(text[^1]))
        {
            return null;
        }

        var previousInput = input with { Text = text[..space] };
        var previous = SearchInterpretationReader.Read(previousInput, evidence);
        var previousVenues = RecognizedVenues(previousInput, previous, evidence.Vocabulary);
        if (!previous.SameConstraints(query) || !previousVenues.SetEquals(venues))
        {
            return null;
        }

        var previousPlan = SearchRequests.Build(previous, input.Scope, true, previousVenues);
        var replies = await Execute(previousPlan, cancellationToken);
        return Primary(previousInput, previous, evidence, previousPlan, replies);
    }

    private static PlaybackContext ReadPlaybackContext(
        SearchQuery query,
        SearchVocabulary vocabulary,
        IReadOnlySet<string> venues
    )
    {
        var artistName = query.Reading is "artist_identity" or "artist_name_prefix";
        var artist = query.Artist ?? (artistName ? query.ReadingArtist : null);
        if (query.HasDate || query.Country != null || query.Tour != null)
        {
            var exact = vocabulary.FullArtist(query.Terms, artist);
            if (exact.Kind == "artist_identity")
            {
                artist ??= exact.Artist;
                artistName = true;
            }
        }

        if (
            query.Matrix
            || artist == null && !query.HasDate && query.Country == null && venues.Count == 0
        )
        {
            return default;
        }

        var barePlace = venues.Count > 0 && vocabulary.Places(query.Terms).Contains(query.Terms);
        return new(
            artist,
            Promote: true,
            Browse: query.Terms.Length == 0 || artistName || barePlace
        );
    }

    private async Task<IReadOnlyList<SearchDocument>> PrioritizePlayable(
        SearchInput input,
        SearchQuery query,
        PlaybackContext playback,
        IReadOnlySet<string> venues,
        IReadOnlyList<SearchDocument> visible,
        int extraSearches,
        CancellationToken cancellationToken
    )
    {
        var candidates = visible;
        if (input.Scope == SearchScope.All && extraSearches > 0 && playback.Browse)
        {
            var browse = SearchRequests.ScopedTracks(query, playback.Artist, venues);
            var replies = await Execute([browse], cancellationToken);
            candidates = replies[0].Documents;
        }

        var playable = candidates.Where(result =>
            result is { Kind: "performance", Preview: not null }
            && (playback.Artist == null || result.ArtistSlug == playback.Artist)
            && (venues.Count == 0 || venues.Contains(result.VenueId))
            && (!query.Soundboard || result.Preview.Soundboard)
        );
        return SearchRanker.AddTracksFirst(visible, playable, input.Scope == SearchScope.All);
    }

    private async Task<IReadOnlyList<SearchReply>> Execute(
        IReadOnlyList<SearchRequest> plan,
        CancellationToken cancellationToken
    )
    {
        if (plan.Count == 0)
        {
            return [];
        }

        var searches = plan.Select(request =>
                request with
                {
                    Collection = Alias(request.Collection),
                }
            )
            .ToArray();
        using var response = await _typesense.MultiSearch(new { searches }, cancellationToken);
        return SearchRequests.Decode(response, plan);
    }

    private string Alias(string collection) => SearchIndexBuilder.AliasName(_prefix, collection);

    private static IReadOnlySet<string> RecognizedVenues(
        SearchInput input,
        SearchQuery query,
        SearchVocabulary vocabulary
    ) =>
        !input.AddTracks
        || query.Reading is "artist_identity" or "artist_name_prefix"
        || input.Ignore?.Contains("venue") == true
            ? new HashSet<string>()
            : vocabulary.RecognizeVenues(query.Terms);

    private static List<SearchChip> Chips(
        SearchQuery query,
        SearchVocabulary vocabulary,
        IReadOnlySet<string> venues
    )
    {
        var chips = new List<SearchChip>();
        if (
            (query.ImpliedArtist ?? query.ReadingArtist) is { } artist
            && vocabulary.ArtistDocuments.TryGetValue(artist, out var artistDocument)
        )
        {
            chips.Add(new("artist", artistDocument.Label, artistDocument.Id));
        }
        if (query.Country is { } country)
        {
            chips.Add(new("country", country, country));
        }
        if (query.Date is int date)
        {
            var label = FormattableString.Invariant(
                $"{date / 10000:0000}-{date / 100 % 100:00}-{date % 100:00}"
            );
            chips.Add(new("date", label, label));
        }
        if (query.Year is int year)
        {
            var label = year.ToString(CultureInfo.InvariantCulture);
            chips.Add(new("year", label, label));
        }
        if (query.Month is int month)
        {
            chips.Add(
                new(
                    "month",
                    CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month),
                    month.ToString(CultureInfo.InvariantCulture)
                )
            );
        }
        if (query.DayKey is int day)
        {
            var label = FormattableString.Invariant($"{day / 100}/{day % 100}");
            chips.Add(new("day", label, label));
        }
        if (query.Soundboard)
        {
            chips.Add(new("soundboard", "Soundboard", "true"));
        }
        if (query.Matrix)
        {
            chips.Add(new("matrix", "Matrix", "true"));
        }
        if (query.Tour is { } tour)
        {
            chips.Add(new("run", tour, tour));
        }
        if (query.Order != "regular")
        {
            chips.Add(new("order", query.Order, query.Order));
        }
        if (
            venues.Count > 0
            && vocabulary
                .Places(query.Terms)
                .OrderByDescending(place => place.Length)
                .FirstOrDefault()
                is { } location
        )
        {
            chips.Add(new("venue", location, location));
        }

        return chips;
    }

    private static SearchResponse Response(
        SearchInput input,
        SearchInterpretation interpretation,
        bool reviewsAvailable,
        string availability,
        IReadOnlyList<SearchDocument>? visible = null,
        IReadOnlyList<SearchReply>? replies = null,
        IReadOnlyList<SearchPassage>? passages = null
    )
    {
        var primary = visible ?? [];
        var sources = replies?.FirstOrDefault(reply =>
            reply.Collection == SearchIndexSchema.Recordings
        );
        var reviews = replies?.FirstOrDefault(reply =>
            reply.Collection == SearchIndexSchema.Reviews
        );
        var counts = new Dictionary<string, int>
        {
            ["artists"] = primary.Count(result => result.Kind == "artist"),
            ["songs"] = primary.Count(result => result.Kind == "song"),
            ["venues"] = primary.Count(result => result.Kind == "venue"),
            ["shows"] = primary.Count(result => SearchRanker.InScope(result, SearchScope.Shows)),
            ["sources"] = sources?.Found ?? 0,
        };
        if (reviewsAvailable)
        {
            counts["reviews"] = reviews?.Found ?? 0;
        }

        if (input.Scope != SearchScope.All)
        {
            var key = input.Scope.ToString().ToLowerInvariant();
            if (counts.ContainsKey(key) && replies != null)
            {
                counts[key] = Math.Max(counts[key], replies.Sum(reply => reply.Found));
            }

            return new SearchResponse(
                input.Text,
                input.RequestId,
                interpretation,
                availability,
                counts,
                Results: primary,
                Page: input.Page
            );
        }

        var sections = new SearchSections(
            primary.Where(result => result.Kind == "artist").ToArray(),
            primary.Where(result => result.Kind == "song").ToArray(),
            primary.Where(result => result.Kind == "venue").ToArray(),
            primary.Where(result => result.Kind is "show" or "tour").ToArray(),
            primary.Where(result => result.Kind == "performance").ToArray(),
            sources?.Documents ?? [],
            reviewsAvailable ? reviews?.Documents ?? [] : null,
            passages ?? []
        );
        return new SearchResponse(
            input.Text,
            input.RequestId,
            interpretation,
            availability,
            counts,
            Top: primary,
            Sections: sections
        );
    }
}

internal sealed record SearchResponse(
    string Q,
    string? RequestId,
    SearchInterpretation Interpretation,
    string Availability,
    Dictionary<string, int> Counts,
    [property: JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        IReadOnlyList<SearchDocument>? Top = null,
    [property: JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        SearchSections? Sections = null,
    [property: JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        IReadOnlyList<SearchDocument>? Results = null,
    [property: JsonProperty(NullValueHandling = NullValueHandling.Ignore)] int? Page = null
);

internal sealed record SearchSections(
    IReadOnlyList<SearchDocument> Artists,
    IReadOnlyList<SearchDocument> Songs,
    IReadOnlyList<SearchDocument> Venues,
    IReadOnlyList<SearchDocument> Shows,
    IReadOnlyList<SearchDocument> Performances,
    IReadOnlyList<SearchDocument> Sources,
    [property: JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        IReadOnlyList<SearchDocument>? Reviews,
    IReadOnlyList<SearchPassage> Passages
);

internal sealed record SearchPerformance(
    string PerformanceKey,
    string ShowId,
    string SongId,
    string Label,
    IReadOnlyList<SearchPerformanceSource> Sources
);

internal sealed record SearchPerformanceSource(
    string SourceId,
    IReadOnlyList<CatalogSearchProjection.SearchFile> Files
);
