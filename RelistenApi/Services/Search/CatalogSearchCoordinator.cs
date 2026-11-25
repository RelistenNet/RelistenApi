using System;
using System.Collections.Frozen;
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
    private readonly TypesenseClient _typesense;
    private readonly CatalogQueryEvidenceStore _evidence;
    private readonly CatalogQueryEvidenceRefreshService? _refresh;
    private readonly ILogger<CatalogSearchCoordinator> _logger;
    private readonly string _prefix;

    internal CatalogSearchCoordinator(
        TypesenseClient typesense,
        CatalogQueryEvidenceStore evidence,
        CatalogQueryEvidenceRefreshService? refresh,
        ILogger<CatalogSearchCoordinator> logger,
        string prefix
    )
    {
        _typesense = typesense;
        _evidence = evidence;
        _refresh = refresh;
        _logger = logger;
        _prefix = prefix;
    }

    internal Task<CatalogSearchEnvelope> Search(
        SearchInput input,
        CancellationToken cancellationToken
    )
    {
        var evidence = _evidence.Current;
        var collections = new SearchCollections(_prefix, evidence.Collection);
        return new CatalogSearchRun(_typesense, _logger, collections, _refresh).Search(
            input,
            evidence,
            cancellationToken
        );
    }
}

// Evidence and resources must come from the same generation. A daily publish
// can swap several aliases while one request is retrieving and attaching rows.
internal sealed class SearchCollections(string prefix, string? entities)
{
    private readonly string? _build = Build(prefix, entities);

    private static string? Build(string prefix, string? entities)
    {
        var stem = SearchIndexBuilder.AliasName(prefix, SearchIndexSchema.Entities) + "_";
        return entities?.StartsWith(stem, StringComparison.Ordinal) == true
            ? entities[stem.Length..]
            : null;
    }

    public string Name(string kind) =>
        _build is { Length: > 0 } generation
            ? SearchIndexBuilder.CollectionName(prefix, kind, generation)
            : SearchIndexBuilder.AliasName(prefix, kind);
}

internal sealed class CatalogSearchRun(
    TypesenseClient _typesense,
    ILogger<CatalogSearchCoordinator> _logger,
    SearchCollections collections,
    CatalogQueryEvidenceRefreshService? refresh
)
{
    // Spend up to two extras in this order: verified place/song and related
    // versions or dated browse, nicknames, alternate readings, ordinary browse,
    // popular versions, empty-result recovery, then playable promotion.
    // Scoped paging probes and render/verification reads are outside this budget.
    private const int ExtraSearchLimit = 2;

    private readonly record struct PlaybackContext(string? Artist, bool Promote, bool Browse);

    private sealed record CatalogBrowse(SearchRequest Request, string? Match);

    public async Task<CatalogSearchEnvelope> Search(
        SearchInput input,
        CatalogQueryEvidence evidence,
        CancellationToken cancellationToken
    )
    {
        // The client can return from a selected reading to the ordinary query.
        var usePrimary = input.ReadingId == "query";
        if (usePrimary)
        {
            input = input with { ReadingId = null };
        }

        var reading = Interpret(input, evidence, usePrimary);
        if (evidence.Collection == null)
        {
            return Empty(input, reading, "unavailable");
        }

        if (
            (input.ArtistId != null && reading.Query.SelectedArtist == null)
            || (input.Scope == SearchScope.Reviews && !reading.ReviewsAvailable)
            || reading.ConflictingCountry
        )
        {
            return Empty(input, reading, "full");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // A stalled engine should release typeahead requests quickly. A submit has
        // more time to fetch the recording and review groups with the primary hits.
        deadline.CancelAfter(input.Typing ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(5));
        try
        {
            var candidates = await Run(input, reading, evidence, deadline.Token);
            return await RenderPage(input, candidates, reading.Constraints, deadline.Token);
        }
        catch (SearchCollectionMissingException)
        {
            // Do not combine stale ranking evidence with a different index. The
            // loader follows the latest entities alias immediately for later requests.
            refresh?.RefreshNow();
            return Empty(input, reading, "unavailable");
        }
        catch (Exception error)
            when (!cancellationToken.IsCancellationRequested
                && (IsSearchFailure(error) || error is OperationCanceledException)
            )
        {
            _logger.LogWarning(error, "Catalog search is temporarily unavailable");
            return Empty(input, reading, "unavailable");
        }
    }

    private sealed record InterpretedQuery(
        SearchQuery Query,
        IReadOnlySet<string> Venues,
        SearchReadings Readings,
        SearchConstraints Constraints,
        CatalogReading? Alternative,
        bool ReviewsAvailable,
        bool ConflictingCountry,
        bool UsePrimary
    );

    private static InterpretedQuery Interpret(
        SearchInput input,
        CatalogQueryEvidence evidence,
        bool usePrimary = false
    )
    {
        var query = SearchInterpretationReader.Read(input, evidence);
        var conflictingCountry = input.Country != null && query.Country != input.Country;
        if (conflictingCountry)
        {
            query = query with { Country = input.Country };
        }

        var venues = RecognizedVenues(input, query, evidence.Vocabulary);
        var options = CatalogReading.Options(input, query, venues, evidence);
        var picked = options.FirstOrDefault(option => option.Option.Id == input.ReadingId);
        // A verified place answers the former venue name or city first.
        // Then try a unique artist initialism, then the literal query.
        var alternative =
            options.FirstOrDefault(option => option.Query.Reading == "place_alias")
            ?? options.FirstOrDefault(option => option.Query.Reading == "artist_initialism")
            ?? options.FirstOrDefault(option => option.Option.Id == "literal");

        // Scoped pages use one query per collection. Interleaving independent
        // pages of literal and inferred hits repeats the same item on later pages.
        // A review must match its own text. Keep the place words when the
        // request selects Reviews, even if the place also has a catalog alias.
        var scopedPlace =
            !usePrimary
            && input.Scope is not (SearchScope.All or SearchScope.Reviews)
            && alternative?.Query.Reading == "place_alias"
                ? alternative
                : null;
        var active = picked ?? scopedPlace;
        if (active != null)
        {
            query = active.Query;
            venues = active.Venues;
            if (query.Reading == "place_alias")
            {
                query = query with { Reading = "selected_place" };
            }
            if (active.Option.Id.StartsWith("artist:", StringComparison.Ordinal))
            {
                query = query with { SelectedArtist = query.Artist };
            }
        }

        // A page can only advertise constraints shared by its primary and
        // mixed readings. Scoped tabs run one reading, so inferred filters count.
        var mixed = active == null && input.Scope == SearchScope.All ? alternative : null;
        var enforcedArtist =
            query.SelectedArtist ?? (input.Scope != SearchScope.All ? query.ImpliedArtist : null);
        var artist = enforcedArtist is { } slug
            ? evidence.Vocabulary.ArtistDocuments.GetValueOrDefault(slug)
            : null;
        var constraints = new SearchConstraints(
            artist?.Id ?? input.ArtistId,
            mixed == null || mixed.Query.Date == query.Date ? query.Date : null,
            query.Reading == "selected_place"
        );
        var readings = Readings(input, query, artist, options, active, mixed);

        return new(
            query,
            venues,
            readings,
            constraints,
            mixed,
            evidence.HasReviews(enforcedArtist),
            conflictingCountry,
            usePrimary
        );
    }

    private async Task<CatalogPage> Run(
        SearchInput input,
        InterpretedQuery reading,
        CatalogQueryEvidence evidence,
        CancellationToken cancellationToken
    )
    {
        var retrieval = PlanRetrieval(input, reading, evidence.Vocabulary);
        if (input.Scope == SearchScope.Shows)
        {
            return await RunScopedShows(input, reading, evidence, retrieval, cancellationToken);
        }

        var batches = await Retrieve(retrieval, cancellationToken);
        var primary = await ResolvePrimaryHits(
            input,
            reading,
            evidence,
            retrieval,
            batches,
            cancellationToken
        );
        return await RankResults(
            input,
            reading,
            evidence,
            retrieval,
            batches,
            primary,
            cancellationToken
        );
    }

    private async Task<CatalogPage> RunScopedShows(
        SearchInput input,
        InterpretedQuery reading,
        CatalogQueryEvidence evidence,
        RetrievalPlan retrieval,
        CancellationToken cancellationToken
    )
    {
        var passages = FindPassages(input, reading, evidence);
        var scoped = await RetrieveScopedShows(
            input,
            reading,
            evidence,
            retrieval,
            passages.Count,
            cancellationToken
        );
        var batches = scoped.Batches;
        var visible = batches
            .Primary.SelectMany(reply => reply.Documents)
            .Select(SearchRanker.LabelSoundcheck)
            .ToArray();
        IReadOnlyList<SearchDocument> results =
            scoped.TourRoute != null && input.Page == 1 ? [scoped.TourRoute, .. visible] : visible;

        var recovered =
            input.Page == 1
            && scoped.Window.Total == 0
            && !scoped.Partial
            && batches.ExtraRequests < ExtraSearchLimit;
        if (recovered)
        {
            (results, _) = await RecoverEmptyResults(
                input,
                reading.Query,
                evidence,
                reading.Venues,
                retrieval.Primary,
                ExtraSearchLimit - batches.ExtraRequests,
                cancellationToken
            );
        }

        var candidates = await RateMatches(
            input,
            reading,
            evidence,
            retrieval.NicknameReading,
            batches,
            new PrimaryHits(
                Documents: results,
                PlaceSongPerformances: [],
                Related: [],
                NextPage: null
            ),
            cancellationToken
        );
        candidates.AddRange(
            passages
                .Skip(scoped.Window.Passages.Offset)
                .Take(scoped.Window.Passages.Count)
                .Select(passage =>
                    CatalogSearchRanking.Passage(passage, reading.Readings.Applied.Id)
                )
        );
        // Add Tracks promotes playable matches within this window. Pulling
        // additional versions here would put them on two pages.
        var ranked = RankedCandidate.Order(
            candidates,
            input.AddTracks,
            reading.Query.Chronological,
            reading.Query.Order
        );
        if (recovered)
        {
            ranked = ranked.Take(ScopedShowPage.Size).ToArray();
        }
        var counts = SearchTabCounts.Empty();
        counts.Add(
            SearchScope.Shows,
            recovered && ranked.Length > 0 ? ranked.Length : scoped.Total.Count,
            scoped.Total.Exact && (!recovered || ranked.Length == 0)
        );
        var nextPage =
            input.Page < ScopedShowPage.LastPage
            && scoped.Window.Total > input.Page * ScopedShowPage.Size
                ? input.Page + 1
                : (int?)null;
        return new(
            DescribeRetrieval(reading, retrieval, batches),
            scoped.Partial ? "partial" : "full",
            counts,
            ranked,
            nextPage
        );
    }

    private sealed record ScopedShowResults(
        ScopedShowPage.Window Window,
        RetrievalBatch Batches,
        SearchDocument? TourRoute,
        (int Count, bool Exact) Total,
        bool Partial
    );

    private async Task<ScopedShowResults> RetrieveScopedShows(
        SearchInput input,
        InterpretedQuery reading,
        CatalogQueryEvidence evidence,
        RetrievalPlan retrieval,
        int passageCount,
        CancellationToken cancellationToken
    )
    {
        (RetrievalRole Role, SearchRequest Request)[] streams =
        [
            .. retrieval.Primary.Select(request => (RetrievalRole.Primary, request)),
            .. retrieval.Nickname.Select(request => (RetrievalRole.Nickname, request)),
        ];
        var priorRows = Math.Max(0, (input.Page - 1) * ScopedShowPage.Size - passageCount);
        var initialOffset = priorRows / Math.Max(1, streams.Length);
        var requests = streams.Select(stream => stream.Request).ToArray();
        var probe = requests
            .Select(request => ScopedShowPage.Request(request, initialOffset, ScopedShowPage.Size))
            .ToArray();
        var replies = await Execute(probe, cancellationToken);
        var tour =
            passageCount == 0
                ? await ScopedTourRoute(
                    reading.Query,
                    evidence.Vocabulary,
                    probe,
                    replies,
                    initialOffset,
                    cancellationToken
                )
                : null;
        var leadingCount = passageCount + (tour == null ? 0 : 1);
        var window = ScopedShowPage.Select(
            input.Page,
            leadingCount,
            replies.Select(reply => reply.Found).ToArray()
        );
        var page = await ReadShowWindow(
            requests,
            replies,
            window.Catalog,
            initialOffset,
            cancellationToken
        );
        var groups = streams.Zip(page).ToLookup(pair => pair.First.Role, pair => pair.Second);
        var batches = new RetrievalBatch(
            Primary: groups[RetrievalRole.Primary].ToArray(),
            Supplemental: null,
            Alternative: [],
            Browse: null,
            Related: null,
            Nickname: groups[RetrievalRole.Nickname].ToArray(),
            ExtraRequests: retrieval.Nickname.Length
        );
        var partial = page.Any(reply => reply.Rejected);
        return new(
            window,
            batches,
            tour,
            CountScopedShows(probe, replies, leadingCount, partial),
            partial
        );
    }

    private async Task<SearchDocument?> ScopedTourRoute(
        SearchQuery query,
        SearchVocabulary vocabulary,
        SearchRequest[] probe,
        IReadOnlyList<SearchReply> replies,
        int initialOffset,
        CancellationToken cancellationToken
    )
    {
        if (!SearchRanker.CanSuggestTourRoute(query))
        {
            return null;
        }

        var entityStream = Array.FindIndex(
            probe,
            request => request.Collection == SearchIndexSchema.Entities
        );
        if (entityStream < 0)
        {
            return null;
        }

        // The first page can add a tour shortcut. Use the same first-page
        // evidence on later pages so its slot never skips a show.
        var anchor =
            initialOffset == 0
                ? replies[entityStream]
                : (await Execute([probe[entityStream] with { Offset = 0 }], cancellationToken))[0];
        return SearchRanker.TourRoute(query, vocabulary, anchor.Documents);
    }

    private static (int Count, bool Exact) CountScopedShows(
        SearchRequest[] probe,
        IReadOnlyList<SearchReply> replies,
        int leadingCount,
        bool partial
    )
    {
        var streams = probe.Zip(replies);
        var exact =
            !partial && streams.All(pair => SearchTabCounts.ExactTotal(pair.First, pair.Second));
        var rows = streams.Sum(pair =>
            exact ? pair.Second.Found : SearchTabCounts.MinimumTotal(pair.First, pair.Second)
        );
        return (leadingCount + rows, exact);
    }

    // The initial offsets assume both index streams have hits. If one has
    // ended, read the actual offset of the surviving stream before ranking.
    private async Task<SearchReply[]> ReadShowWindow(
        SearchRequest[] requests,
        IReadOnlyList<SearchReply> replies,
        ScopedShowPage.Slice[] slices,
        int initialOffset,
        CancellationToken cancellationToken
    )
    {
        var page = replies.ToArray();
        var missing = new List<(int Stream, SearchRequest Request)>();
        for (var index = 0; index < slices.Length; index++)
        {
            var slice = slices[index];
            var skip = slice.Offset - initialOffset;
            if (
                slice.Count == 0
                || (skip >= 0 && skip + slice.Count <= page[index].Documents.Count)
            )
            {
                page[index] = page[index] with
                {
                    Documents =
                        slice.Count == 0
                            ? []
                            : page[index].Documents.Skip(skip).Take(slice.Count).ToArray(),
                };
                continue;
            }

            missing.Add(
                (index, ScopedShowPage.Request(requests[index], slice.Offset, slice.Count))
            );
        }

        var corrected = await Execute(
            missing.Select(item => item.Request).ToArray(),
            cancellationToken
        );
        foreach (var (item, reply) in missing.Zip(corrected))
        {
            page[item.Stream] = reply;
        }
        return page;
    }

    // Supplemental, alternative, browse and related retrieval are All-only.
    private sealed record RetrievalPlan(
        SearchRequest[] Primary,
        SearchRequest? Supplemental,
        SearchRequest[] Alternative,
        CatalogBrowse? Browse,
        SearchRequest? Related,
        SearchRequest[] Nickname,
        ReviewedSongReading? NicknameReading,
        PlaybackContext Playback,
        PlaceSongMatch? PlaceSong
    );

    private static RetrievalPlan PlanRetrieval(
        SearchInput input,
        InterpretedQuery reading,
        SearchVocabulary vocabulary
    )
    {
        var query = reading.Query;
        var venues = reading.Venues;
        var alternative = reading.Alternative;
        var playback =
            input.AddTracks && input.Scope is (SearchScope.All or SearchScope.Shows)
                ? ReadPlaybackContext(query, vocabulary, venues)
                : default;
        var placeSong = SearchRequests.ReadPlaceSong(input, query, vocabulary);
        var exactPlaceSong = placeSong is { Tier: MatchTier.Exact } ? placeSong : null;
        var datedVenues =
            input is { Scope: SearchScope.All, Page: 1, ReadingId: null }
            && input.Ignore?.Contains("venue") != true
            && query.HasDate
            && !query.Chronological
                ? vocabulary.VenuesQualifiedByCity(query.Terms)
                : FrozenSet<string>.Empty;
        var plan = Plan(
            input,
            query,
            venues,
            reading.ReviewsAvailable,
            playback,
            exactPlaceSong?.VenueIds ?? (datedVenues.Count > 0 ? datedVenues : null),
            RecordingSong(input, query, vocabulary) != null
        );
        var placeSongSearch =
            input.Scope == SearchScope.All && placeSong != null
                ? SearchRequests.PlaceSongPerformances(query, placeSong)
                : null;
        var datedBrowse = DatedPlaceBrowse(input, query, vocabulary, datedVenues, exactPlaceSong);
        var related =
            placeSongSearch != null
            && exactPlaceSong != null
            && datedBrowse == null
            && !input.Typing
            && input.ReadingId == null
            && query.Date == null
                ? SearchRequests.RelatedSongVersions(query, exactPlaceSong)
                : null;
        var reserved =
            (placeSongSearch == null ? 0 : 1)
            + (datedBrowse == null ? 0 : 1)
            + (related == null ? 0 : 1);
        var nickname = vocabulary.ReadReviewedSong(
            input,
            query,
            query.Reading == "selected_place" ? venues : null
        );
        var nicknamePlan =
            nickname == null
                ? []
                : SearchRequests
                    .ReviewedSong(input, query, nickname)
                    .Take(ExtraSearchLimit - reserved)
                    .ToArray();
        reserved += nicknamePlan.Length;
        if (input.Scope != SearchScope.All && nicknamePlan.Length > 0)
        {
            // A scoped tab advances one result stream per collection. A
            // verified nickname supplies that stream instead of a second page.
            var nicknameCollections = nicknamePlan
                .Select(request => request.Collection)
                .ToHashSet();
            plan = plan.Where(request => !nicknameCollections.Contains(request.Collection))
                .ToArray();
        }
        var alternativePlan = AlternativeSearches(
            input,
            query,
            alternative,
            ExtraSearchLimit - reserved
        );
        var budget = ExtraSearchLimit - reserved - alternativePlan.Length;
        var browse = datedBrowse ?? (budget > 0 ? BrowseCatalog(input, query, vocabulary) : null);
        if (browse != null && datedBrowse == null)
        {
            budget--;
        }
        var supplemental =
            placeSongSearch
            ?? (
                budget > 0
                    ? SearchRequests.PopularExactSong(input, query, venues, vocabulary)
                    : null
            );
        return new(
            plan.Select(request =>
                    input.Scope == SearchScope.All
                    && request.Collection == SearchIndexSchema.Entities
                        ? request with
                        {
                            FacetBy = "kind",
                        }
                        : request
                )
                .ToArray(),
            supplemental,
            alternativePlan,
            browse,
            related,
            nicknamePlan,
            nickname,
            playback,
            placeSong
        );
    }

    private sealed record PrimaryHits(
        IReadOnlyList<SearchDocument> Documents,
        SearchDocument[] PlaceSongPerformances,
        SearchDocument[] Related,
        int? NextPage,
        PlaceSongMatch? PlaceSong = null
    );

    private async Task<PrimaryHits> ResolvePrimaryHits(
        SearchInput input,
        InterpretedQuery reading,
        CatalogQueryEvidence evidence,
        RetrievalPlan retrieval,
        RetrievalBatch batches,
        CancellationToken cancellationToken
    )
    {
        var query = reading.Query;
        var venues = reading.Venues;
        var plan = retrieval.Primary;
        var replies = batches.Primary;
        var additional = batches.Supplemental is { } extra
            ? SupplementalPerformances(extra, query.Soundboard)
            : [];
        var atPlace = retrieval.PlaceSong != null ? additional : [];
        var exact = retrieval.PlaceSong is { Tier: MatchTier.Exact } ? atPlace : [];
        var browse =
            retrieval.Browse is { } browsing && batches.Browse is { } browseReply
                ? BrowseResults(query, evidence, browseReply, browsing.Match)
                : [];
        var related =
            batches.Related is { } relatedReply
            && retrieval.PlaceSong is { } placeSong
            && exact.Length > 0
                ? RelatedVersions(relatedReply, query.Soundboard, placeSong)
                : [];

        var visible = PrimaryResults(input, query, evidence, plan, replies);
        var searchesLeft = ExtraSearchLimit - batches.ExtraRequests;
        if (visible.Count == 0 && additional.Length == 0 && browse.Length == 0 && searchesLeft > 0)
        {
            (visible, searchesLeft) = await RecoverEmptyResults(
                input,
                query,
                evidence,
                venues,
                plan,
                searchesLeft,
                cancellationToken
            );
        }

        if (retrieval.Playback.Promote && searchesLeft > 0)
        {
            visible = await PrioritizePlayable(
                input,
                query,
                retrieval.Playback,
                venues,
                visible,
                searchesLeft,
                cancellationToken
            );
        }

        visible = SearchPlayTieBreak.Rank(input, query, visible, evidence);
        visible =
            exact.Length > 0 && !visible.Any(result => result.Name == query.Terms)
                ? SearchRanker.LeadWithPerformances(visible, exact, mixed: true)
                : SearchRanker.PreferPlaceAndSong(input, query, visible, evidence.Vocabulary);
        if (retrieval.PlaceSong == null && additional.Length > 0)
        {
            // These are ordinary candidates. Keep explicit place promotions separate.
            visible = [.. visible, .. additional];
        }
        var pageReplies = replies
            .Zip(plan)
            .Concat(batches.Alternative.Zip(retrieval.Alternative))
            .Concat(batches.Nickname.Zip(retrieval.Nickname));
        var nextPage =
            input.Scope != SearchScope.All
            && input.Page < 100
            && pageReplies.Any(pair => pair.First.Found > input.Page * pair.Second.PerPage)
                ? input.Page + 1
                : (int?)null;
        return new([.. visible, .. browse], atPlace, related, nextPage, retrieval.PlaceSong);
    }

    private enum RetrievalRole
    {
        Primary,
        Supplemental,
        Alternative,
        Browse,
        Related,
        Nickname,
    }

    private sealed record RetrievalBatch(
        SearchReply[] Primary,
        SearchReply? Supplemental,
        SearchReply[] Alternative,
        SearchReply? Browse,
        SearchReply? Related,
        SearchReply[] Nickname,
        int ExtraRequests
    );

    private async Task<RetrievalBatch> Retrieve(
        RetrievalPlan plan,
        CancellationToken cancellationToken
    )
    {
        static IEnumerable<(RetrievalRole, SearchRequest)> One(
            RetrievalRole role,
            SearchRequest? request
        )
        {
            if (request != null)
            {
                yield return (role, request);
            }
        }

        (RetrievalRole Role, SearchRequest Request)[] requests =
        [
            .. plan.Primary.Select(request => (RetrievalRole.Primary, request)),
            .. One(RetrievalRole.Supplemental, plan.Supplemental),
            .. plan.Alternative.Select(request => (RetrievalRole.Alternative, request)),
            .. One(RetrievalRole.Browse, plan.Browse?.Request),
            .. One(RetrievalRole.Related, plan.Related),
            .. plan.Nickname.Select(request => (RetrievalRole.Nickname, request)),
        ];
        var replies = await Execute(
            requests.Select(request => request.Request).ToArray(),
            cancellationToken
        );
        var groups = requests.Zip(replies).ToLookup(pair => pair.First.Role, pair => pair.Second);
        return new(
            groups[RetrievalRole.Primary].ToArray(),
            groups[RetrievalRole.Supplemental].SingleOrDefault(),
            groups[RetrievalRole.Alternative].ToArray(),
            groups[RetrievalRole.Browse].SingleOrDefault(),
            groups[RetrievalRole.Related].SingleOrDefault(),
            groups[RetrievalRole.Nickname].ToArray(),
            requests.Length - plan.Primary.Length
        );
    }

    private static SearchReadings Readings(
        SearchInput input,
        SearchQuery query,
        SearchDocument? artist,
        CatalogReading[] options,
        CatalogReading? picked,
        CatalogReading? mixed
    )
    {
        var appliedId = picked?.Option.Id ?? "query";
        var chips = new HashSet<(string Key, string Value)>();
        SearchReading Unique(SearchReading reading) =>
            reading with
            {
                Chips = reading.Chips.Where(chip => chips.Add((chip.Key, chip.Value))).ToArray(),
            };

        var applied = Unique(
            new SearchReading(
                appliedId,
                [.. picked?.Option.Chips ?? [], .. Chips(input, query, artist, mixed?.Query)]
            )
        );
        var alternatives = options
            .Select(option => option.Option)
            .Where(option => option.Id != appliedId)
            .Select(Unique)
            .Where(option => option.Id == "literal" || option.Chips.Count > 0)
            .ToList();

        if (appliedId != "query" && alternatives.All(option => option.Id != "query"))
        {
            alternatives.Insert(0, new SearchReading("query", []));
        }

        return new(applied, alternatives);
    }

    private static SearchReadings DescribeRetrieval(
        InterpretedQuery reading,
        RetrievalPlan plan,
        RetrievalBatch batches
    )
    {
        if (
            plan.NicknameReading?.ReplacesYear != true
            || !batches.Nickname.Any(reply => reply.Found > 0)
        )
        {
            return reading.Readings;
        }

        // This reading interpreted the apparent year as part of the title.
        var applied = reading.Readings.Applied;
        return reading.Readings with
        {
            Applied = applied with
            {
                Chips = applied.Chips.Where(chip => chip.Key != "year").ToArray(),
            },
        };
    }

    private async Task<CatalogSearchEnvelope> RenderPage(
        SearchInput input,
        CatalogPage page,
        SearchConstraints constraints,
        CancellationToken token
    )
    {
        var pool = page.Ranked;
        var available = new Dictionary<string, SearchResult>(StringComparer.Ordinal);
        var rejected = new HashSet<string>(StringComparer.Ordinal);
        var display = new CatalogSearchDisplay(_typesense, collections);
        RankedCandidate[] selected;
        string[] moreScopes;
        // A shortlist has the IDs to score cheaply. Fetch v4 payloads only for
        // the page, then reapply diversity if a missing resource rejects a row.
        do
        {
            var eligible = pool.Where(candidate => !rejected.Contains(candidate.Row.Key)).ToArray();
            (selected, moreScopes) = SelectPage(input, eligible, page.SongFocused);
            var missing = selected
                .Where(candidate => !available.ContainsKey(candidate.Row.Key))
                .ToArray();
            if (missing.Length == 0)
            {
                break;
            }

            var resources = await display.Read(missing, token);
            var resolved = CatalogSearchResults.Attach(missing, resources);
            foreach (var candidate in missing)
            {
                if (resolved.TryGetValue(candidate.Row.Key, out var result))
                {
                    available.Add(candidate.Row.Key, result);
                }
                else
                {
                    rejected.Add(candidate.Row.Key);
                }
            }
        } while (rejected.Count > 0);

        var leadSong = RankedCandidate.LeadSong(
            pool.Where(candidate => !rejected.Contains(candidate.Row.Key))
        );
        var candidates = selected
            .Where(candidate => available.ContainsKey(candidate.Row.Key))
            .Select(candidate => new SearchCandidate(
                available[candidate.Row.Key],
                candidate.MergeOrder(candidate.Row.Key == leadSong)
            ))
            .DistinctBy(candidate => candidate.Result.Key, StringComparer.Ordinal)
            .ToArray();
        var availability = rejected.Count == 0 ? page.Availability : "partial";
        var scope = input.Scope.ToString().ToLowerInvariant();
        if (availability == "partial" && input.Scope != SearchScope.All)
        {
            page.Counts.Exact.Remove(scope);
            page.Counts.Add(input.Scope, candidates.Length, exact: false);
        }
        var limit =
            input.Scope == SearchScope.All
                ? (input.AddTracks || page.SongFocused ? SearchDiversity.SongPageSize : 24)
                : candidates.Length;
        return new(
            new SearchPage(
                input.Text,
                input.RequestId,
                scope,
                new(availability, "not_requested"),
                page.Counts.Exact,
                page.Readings,
                page.NextPage,
                moreScopes
            )
            {
                CountLowerBounds = page.Counts.LowerBounds,
            },
            constraints,
            candidates,
            limit
        );
    }

    private static (RankedCandidate[] Results, string[] MoreScopes) SelectPage(
        SearchInput input,
        RankedCandidate[] ranked,
        bool songFocused
    )
    {
        if (input.Scope != SearchScope.All)
        {
            return (ranked, []);
        }

        return input.AddTracks
            ? (SearchDiversity.AddTracksPage(ranked, songFocused), [])
            : SearchDiversity.ExplorePage(ranked, songFocused);
    }

    private static CatalogSearchEnvelope Empty(
        SearchInput input,
        InterpretedQuery reading,
        string availability
    )
    {
        var counts = SearchTabCounts.Empty();
        if (availability == "full" && input.Scope != SearchScope.All)
        {
            counts.Add(input.Scope, 0, exact: true);
        }
        return new(
            new SearchPage(
                input.Text,
                input.RequestId,
                input.Scope.ToString().ToLowerInvariant(),
                new(availability, "not_requested"),
                counts.Exact,
                reading.Readings,
                null,
                []
            ),
            reading.Constraints,
            [],
            24
        );
    }

    private sealed record CatalogPage(
        SearchReadings Readings,
        string Availability,
        SearchTabCounts Counts,
        RankedCandidate[] Ranked,
        int? NextPage
    )
    {
        public bool SongFocused { get; init; }
    }

    private static SearchRequest[] AlternativeSearches(
        SearchInput input,
        SearchQuery query,
        CatalogReading? alternative,
        int budget
    )
    {
        if (
            alternative == null
            || budget == 0
            || alternative.Query.Terms == query.Terms && alternative.Query.Artist == query.Artist
        )
        {
            return [];
        }

        var initialism = alternative.Query.Reading == "artist_initialism";
        var placeAlias = alternative.Query.Reading == "place_alias";
        if (!initialism && !placeAlias && input.Scope != SearchScope.All)
        {
            return [];
        }

        var artistAndDateOnly =
            query is { ImpliedArtist: not null, Terms.Length: 0 } && query.HasDate;
        return SearchRequests
            .Build(alternative.Query, input.Scope, typing: true, alternative.Venues, input.Page)
            .Take(budget)
            .Select(request =>
                initialism || placeAlias ? request with { Optional = true }
                : artistAndDateOnly ? request with { NumTypos = 0, Optional = true }
                : request
            )
            .ToArray();
    }

    private static CatalogBrowse? BrowseCatalog(
        SearchInput input,
        SearchQuery query,
        SearchVocabulary vocabulary
    )
    {
        if (
            input is not { Scope: SearchScope.All, Page: 1 }
            || input.AddTracks
            || query.Order != "regular"
            || query.Chronological
            || query.Soundboard
            || query.Matrix
        )
        {
            return null;
        }

        if (query.NamedArtist is { } artist)
        {
            return new(SearchRequests.PopularArtistShows(query, artist), null);
        }
        if (
            input.ReadingId != "literal"
            && input.Ignore?.Contains("run") != true
            && SearchRequests.PopularRunShows(query, vocabulary) is { } run
        )
        {
            return new(run, "run_browse");
        }
        if (input.Ignore?.Contains("venue") != true && input.ReadingId == null)
        {
            var venues = vocabulary
                .VenuesFor(query.Terms)
                .Select(venue => venue.VenueId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (venues.Length > 0)
            {
                return new(SearchRequests.PopularPlaceShows(query, venues), "place_browse");
            }
        }

        return null;
    }

    private static CatalogBrowse? DatedPlaceBrowse(
        SearchInput input,
        SearchQuery query,
        SearchVocabulary vocabulary,
        IReadOnlySet<string> qualifiedVenues,
        PlaceSongMatch? song
    )
    {
        if (
            input is not { Scope: SearchScope.All, Page: 1, ReadingId: null }
            || input.Ignore?.Contains("venue") == true
            || !query.HasDate
            || query.Chronological
            || query.Soundboard
            || query.Matrix
            || query.Order != "regular"
        )
        {
            return null;
        }

        if (qualifiedVenues.Count > 0)
        {
            return new(
                SearchRequests.PopularPlaceShows(query, qualifiedVenues),
                "dated_place_browse"
            );
        }
        if (song != null)
        {
            return null;
        }

        // A venue and an explicit date can still find a show when another
        // term isn't in the vocabulary, such as an unfamiliar nickname.
        if (
            vocabulary.ReadPlaceAlias(query.Terms, query.Artist) is
            { Place.IsCity: true, Remainder.Length: > 0 } place
        )
        {
            return new(
                SearchRequests.PopularPlaceShows(query, place.Place.VenueIds),
                "dated_place_browse"
            );
        }
        if (
            query.Date != null
            && vocabulary
                .Places(query.Terms)
                .OrderByDescending(phrase => phrase.Length)
                .FirstOrDefault(phrase =>
                    phrase.Length < query.Terms.Length
                    && vocabulary.VenueOptions(phrase).Take(2).Count() == 1
                )
                is { } venue
        )
        {
            return new(SearchRequests.DatedShowsAtNamedVenue(query, venue), "dated_place_browse");
        }
        return null;
    }

    private static SearchDocument[] BrowseResults(
        SearchQuery query,
        CatalogQueryEvidence evidence,
        SearchReply reply,
        string? match
    ) =>
        query.NamedArtist is { } artist
            ? [.. reply.Documents, .. evidence.PopularSongs(artist)]
            : reply.Documents.Select(document => document with { CatalogMatch = match }).ToArray();

    private SearchDocument[] SupplementalPerformances(SearchReply reply, bool soundboard)
    {
        if (reply.Rejected)
        {
            _logger.LogWarning("Typesense rejected the optional performance search");
            return [];
        }

        return reply
            .Documents.Where(result =>
                result.Preview is { } preview && (!soundboard || preview.Soundboard)
            )
            .ToArray();
    }

    private SearchDocument[] RelatedVersions(
        SearchReply reply,
        bool soundboard,
        PlaceSongMatch match
    )
    {
        var phrase = $"{match.Title} {match.Place}";
        return SupplementalPerformances(reply, soundboard)
            .Where(result =>
                match.SongIds.Contains(result.SongId)
                || MatchEvidence.Read(match.Title, ("title", result.Name, 1)).Tier
                    == MatchTier.Exact
                || (
                    result.SongId.Length == 0
                    && MatchEvidence.Read(phrase, ("title", result.Name, 1)).Relevance == 1
                )
            )
            .ToArray();
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
        int searchesLeft,
        CancellationToken cancellationToken
    )
    {
        IReadOnlyList<SearchDocument> visible = [];
        if (input.Page == 1 && SearchRequests.NamedRun(query, plan, venues) is { } run)
        {
            var fallback = await Execute([run.Request], cancellationToken);
            visible = SearchRanker.SuggestNamedRun(run, fallback[0].Documents);
            searchesLeft--;
        }

        if (
            visible.Count == 0
            && searchesLeft > 0
            && await RestoreEmptyPrefix(input, query, evidence, venues, plan, cancellationToken)
                is { } restored
        )
        {
            visible = restored;
            searchesLeft--;
        }

        if (
            visible.Count == 0
            && searchesLeft > 0
            && SearchRequests.WidenEmptyPerformancePrefix(input, plan) is { } wider
        )
        {
            var replies = await Execute([wider], cancellationToken);
            visible = Primary(input, query, evidence, [wider], replies);
            searchesLeft--;
        }

        return (visible, searchesLeft);
    }

    private async Task<CatalogPage> RankResults(
        SearchInput input,
        InterpretedQuery reading,
        CatalogQueryEvidence evidence,
        RetrievalPlan retrieval,
        RetrievalBatch batches,
        PrimaryHits primary,
        CancellationToken cancellationToken
    )
    {
        var query = reading.Query;
        var replies =
            input.Scope != SearchScope.All
                ? batches.Primary.Concat(batches.Nickname).ToArray()
                : batches.Primary;
        var candidates = await RateMatches(
            input,
            reading,
            evidence,
            retrieval.NicknameReading,
            batches,
            primary,
            cancellationToken
        );
        var isAll = input.Scope == SearchScope.All;
        var ranked = RankedCandidate.Order(
            isAll ? await RecordingsAsShows(candidates, replies, cancellationToken) : candidates,
            input.AddTracks,
            query.Chronological,
            query.Order,
            mixed: isAll
        );
        var songFocused = isAll && SearchDiversity.FocusedSong(ranked) != null;
        if (isAll)
        {
            ranked = ranked
                .DistinctBy(
                    candidate =>
                        candidate.Row
                            is SearchDocument
                            {
                                Kind: "performance",
                                Preview.TrackId.Length: > 0
                            } performance
                            ? $"performance:track:{performance.Preview.TrackId}"
                            : candidate.Row.Key,
                    StringComparer.Ordinal
                )
                .ToArray();
            ranked = LimitAllCandidates(input, evidence.Vocabulary, ranked, songFocused);
        }

        var counts = CountTabs(input, reading, evidence, retrieval, batches);
        return new(
            DescribeRetrieval(reading, retrieval, batches),
            replies.Any(reply => reply.Rejected) ? "partial" : "full",
            counts,
            ranked,
            primary.NextPage
        )
        {
            SongFocused = songFocused,
        };
    }

    private async Task<List<RankedCandidate>> RateMatches(
        SearchInput input,
        InterpretedQuery reading,
        CatalogQueryEvidence evidence,
        ReviewedSongReading? nickname,
        RetrievalBatch batches,
        PrimaryHits primary,
        CancellationToken cancellationToken
    )
    {
        var query = reading.Query;
        var venues = reading.Venues;
        var primaryReading = reading.Readings.Applied.Id;
        var alternative = reading.Alternative;
        var artistIds = evidence.Vocabulary.ArtistDocuments;
        var contextualPlaces =
            input.Scope == SearchScope.All
            && (query.HasDate || query.Artist != null)
            && input.ReadingId == null
            && input.Ignore?.Contains("venue") != true
                ? evidence
                    .Vocabulary.VenuesFor(query.Terms)
                    .Select(place => place.VenueId)
                    .ToHashSet(StringComparer.Ordinal)
                : [];
        RankedCandidate Rate(
            SearchDocument document,
            SearchQuery interpretation,
            IReadOnlySet<string> places,
            string? id
        ) =>
            CatalogSearchRanking.Candidate(
                artistIds.TryGetValue(document.ArtistSlug, out var artist)
                    ? document with
                    {
                        ArtistId = artist.Id,
                    }
                    : document,
                input,
                interpretation,
                evidence,
                places.Count == 0 && interpretation.Terms == query.Terms
                    ? contextualPlaces
                    : places,
                id
            );
        var rows =
            input.Scope == SearchScope.All
                ? primary.Documents.Concat(
                    batches
                        .Primary.SelectMany(reply => reply.Documents)
                        .Where(row =>
                            row.Kind is "recording" or "review" || query.Order == "regular"
                        )
                )
                : primary.Documents;

        var candidates = rows.Select(row => Rate(row, query, venues, primaryReading)).ToList();
        if (nickname != null)
        {
            candidates.AddRange(
                batches
                    .Nickname.SelectMany(reply => reply.Documents)
                    .Select(row =>
                        CatalogSearchRanking.ReviewedSong(
                            Rate(
                                input.Scope == SearchScope.Shows
                                    ? SearchRanker.LabelSoundcheck(row)
                                    : row,
                                query,
                                nickname.Venues,
                                primaryReading
                            ),
                            nickname.Contextual
                        )
                    )
            );
        }
        if (primary.PlaceSong is { } placeSong)
        {
            candidates.AddRange(
                primary.PlaceSongPerformances.Select(row =>
                    CatalogSearchRanking.VerifiedPlaceSong(
                        Rate(row, query, venues, primaryReading),
                        placeSong.Tier
                    )
                )
            );
        }
        if (alternative != null)
        {
            candidates.AddRange(
                batches
                    .Alternative.SelectMany(reply => reply.Documents)
                    .Select(row =>
                        Rate(
                            alternative.Query
                                is {
                                    Reading: "place_alias",
                                    Terms.Length: 0,
                                    HasDate: false,
                                    Artist: null
                                }
                                ? row with
                                {
                                    CatalogMatch = "place_browse",
                                }
                                : row,
                            alternative.Query,
                            alternative.Venues,
                            alternative.Option.Id
                        )
                    )
            );
        }
        if (input.Scope == SearchScope.All)
        {
            AddPassageCandidates(input, reading, evidence, candidates);
        }
        candidates.AddRange(
            primary.Related.Select(row =>
                Rate(row, query, venues, primaryReading) with
                {
                    Related = true,
                }
            )
        );
        var verifiedNotes = await VerifyRecordingSongMatches(
            input,
            query,
            evidence.Vocabulary,
            batches.Primary,
            cancellationToken
        );
        candidates.AddRange(
            verifiedNotes.Select(row =>
                Rate(row, query, venues, primaryReading) with
                {
                    Related = true,
                }
            )
        );

        ApplyResultConstraints(
            input,
            query,
            venues,
            contextualPlaces,
            primary.PlaceSong is { Tier: MatchTier.Exact } ? primary.PlaceSongPerformances : [],
            candidates
        );
        return candidates;
    }

    private static void ApplyResultConstraints(
        SearchInput input,
        SearchQuery query,
        IReadOnlySet<string> venues,
        IReadOnlySet<string> contextualPlaces,
        IReadOnlyList<SearchDocument> exact,
        List<RankedCandidate> candidates
    )
    {
        if (contextualPlaces.Count > 0)
        {
            // A note about another night's venue should not turn this
            // recording into a show match for an artist or date at that place.
            candidates.RemoveAll(candidate =>
                candidate.Row is SearchDocument { Kind: "recording" } row
                && !contextualPlaces.Contains(row.VenueId)
            );
        }

        if (exact.Count > 0)
        {
            // A named place plus song is a setlist lookup. Matching one token
            // in another venue's taper notes does not make that show a hit.
            var matchedVenues = exact.Select(row => row.VenueId).ToHashSet(StringComparer.Ordinal);
            candidates.RemoveAll(candidate =>
                candidate.Row is SearchDocument { Kind: "recording" or "review" } row
                && !matchedVenues.Contains(row.VenueId)
            );
        }

        if (input.Scope == SearchScope.All && query.Matrix && !input.Typing)
        {
            // Every show returned for an explicit recording format must open a
            // source with that format, even if the listener prefers another one.
            candidates.RemoveAll(candidate =>
                candidate.Row is not SearchDocument { Kind: "recording", Matrix: true }
            );
        }

        if (query.SelectedArtist is { } artist)
        {
            candidates.RemoveAll(candidate =>
                candidate.Row switch
                {
                    SearchDocument row => row.ArtistSlug != artist
                        && !row.ArtistSlugs.Contains(artist),
                    SearchPassage passage => passage.ArtistSlug != artist,
                    _ => false,
                }
            );
        }

        if (query.Reading == "selected_place")
        {
            candidates.RemoveAll(candidate =>
                candidate.Row is SearchDocument row && !venues.Contains(row.VenueId)
            );
        }
    }

    private static RankedCandidate[] LimitAllCandidates(
        SearchInput input,
        SearchVocabulary vocabulary,
        RankedCandidate[] ranked,
        bool songFocused
    )
    {
        var distinct = VenueSearchIdentity.Collapse(ranked, vocabulary).ToArray();
        var limit = input.AddTracks || songFocused ? SearchDiversity.SongPageSize + 24 : 36;
        var relatedTracks = input.AddTracks
            ? distinct
                .Where(candidate => candidate.Related && candidate.Playable)
                .Take(SearchDiversity.RelatedTrackLimit)
                .ToArray()
            : [];
        return distinct
            .Except(relatedTracks)
            .Take(limit - relatedTracks.Length)
            .Concat(relatedTracks)
            .ToArray();
    }

    private async Task<IReadOnlyList<SearchDocument>> VerifyRecordingSongMatches(
        SearchInput input,
        SearchQuery query,
        SearchVocabulary vocabulary,
        IReadOnlyList<SearchReply> replies,
        CancellationToken cancellationToken
    )
    {
        if (RecordingSong(input, query, vocabulary) is not { } song)
        {
            return [];
        }

        var shows = replies
            .Where(reply => reply.Collection == SearchIndexSchema.Recordings)
            .SelectMany(reply => reply.Documents)
            .Select(row => row.ShowId)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (shows.Length == 0)
        {
            return [];
        }

        // A notes hit suggests a show. Confirm it has a playable file for the
        // song. A failed optional proof leaves the required hits usable.
        try
        {
            var proof = await Execute(
                [SearchRequests.SongAtRecordingShows(query, song, shows)],
                cancellationToken
            );
            return SupplementalPerformances(proof[0], query.Soundboard);
        }
        catch (Exception error) when (IsSearchFailure(error))
        {
            _logger.LogWarning(error, "Could not verify song matches mentioned in recordings");
            return [];
        }
    }

    private static IReadOnlyList<SearchPassage> FindPassages(
        SearchInput input,
        InterpretedQuery reading,
        CatalogQueryEvidence evidence
    ) =>
        evidence.Passages.Find(
            reading.Query,
            reading.Query.Reading == "selected_place" ? reading.Venues : null,
            input.ReadingId != "literal" && input.Ignore?.Contains("venue") != true,
            limit: input.Scope == SearchScope.All ? 8 : int.MaxValue
        );

    private static void AddPassageCandidates(
        SearchInput input,
        InterpretedQuery reading,
        CatalogQueryEvidence evidence,
        List<RankedCandidate> candidates
    )
    {
        var query = reading.Query;
        var allowAliases = input.ReadingId != "literal" && input.Ignore?.Contains("venue") != true;
        var passages = FindPassages(input, reading, evidence);
        if (passages.Count == 0)
        {
            return;
        }

        if (allowAliases && evidence.Vocabulary.ReadPlaceAlias(query.Terms, query.Artist) != null)
        {
            // The marked sequence verifies the place reading. Keep the other
            // takes as related results so they don't crowd out its show.
            var matchingShows = passages.Select(passage => passage.ShowId).ToHashSet();
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (
                    candidate.Row is SearchDocument { Kind: "performance" } row
                    && !matchingShows.Contains(row.ShowId)
                )
                {
                    candidates[i] = candidate with { Related = true };
                }
            }
        }

        candidates.AddRange(
            passages.Select(passage =>
                CatalogSearchRanking.Passage(passage, reading.Readings.Applied.Id)
            )
        );
    }

    private async Task<IEnumerable<RankedCandidate>> RecordingsAsShows(
        IReadOnlyList<RankedCandidate> candidates,
        IReadOnlyList<SearchReply> replies,
        CancellationToken cancellationToken
    )
    {
        var rows = candidates.Select(candidate => candidate.Row).OfType<SearchDocument>().ToArray();
        var recordings = rows.Where(row => row.Kind == "recording").ToArray();
        if (recordings.Length == 0)
        {
            return candidates;
        }

        var recordingShowIds = recordings
            .Where(row => row.ShowId.Length > 0)
            .Select(row => row.ShowId)
            .ToHashSet(StringComparer.Ordinal);
        var shows = rows.Where(row => row.Kind == "show")
            .DistinctBy(row => row.ShowId)
            .ToDictionary(row => row.ShowId, StringComparer.Ordinal);
        foreach (var document in replies.SelectMany(reply => reply.Documents))
        {
            if (document.Kind == "show" && recordingShowIds.Contains(document.ShowId))
            {
                // An explicit recording format can exclude the direct show result.
                shows.TryAdd(document.ShowId, document);
            }
        }

        var missing = recordingShowIds.Where(id => !shows.ContainsKey(id)).ToArray();
        if (missing.Length > 0)
        {
            // A notes or credits hit may have no counterpart in the text shortlist.
            // Fetch all missing parents together before ranking the show cards.
            foreach (var show in await ReadShowParents(missing, cancellationToken))
            {
                shows.Add(show.ShowId, show);
            }
        }

        return CatalogSearchRanking.RecordingsAsShows(candidates, shows);
    }

    private async Task<IReadOnlyList<SearchDocument>> ReadShowParents(
        string[] missing,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var replies = await Execute([SearchRequests.ShowsById(missing)], cancellationToken);
            if (!replies[0].Rejected)
            {
                return replies[0].Documents;
            }

            _logger.LogWarning("Typesense rejected the search for recording parent shows");
        }
        catch (Exception error) when (IsSearchFailure(error))
        {
            _logger.LogWarning(error, "Could not load recording parent shows");
        }

        return [];
    }

    private static string? RecordingSong(
        SearchInput input,
        SearchQuery query,
        SearchVocabulary vocabulary
    ) =>
        input is { AddTracks: true, Typing: false, Scope: SearchScope.All, Page: 1 }
        && query is { Reading: "artist_with_context", HasDate: false, Artist: { } artist }
            ? vocabulary.FindSong(artist, query.Terms)?.Id
            : null;

    private static IReadOnlyList<SearchRequest> Plan(
        SearchInput input,
        SearchQuery query,
        IReadOnlySet<string> venues,
        bool reviewsAvailable,
        PlaybackContext playback,
        IReadOnlySet<string>? reviewVenueIds,
        bool retainNoteQuery
    )
    {
        var requests = string.IsNullOrWhiteSpace(input.Text)
            ? []
            : SearchRequests.Build(
                query,
                input.Scope,
                input.Typing,
                venues,
                input.Page,
                includeAllEvidence: input.AddTracks || query.NamedArtist == null,
                reviewVenueIds: reviewVenueIds
            );
        if (input.AddTracks && input.Scope == SearchScope.All && !input.Typing)
        {
            requests = requests
                .Select(request =>
                    request.Collection == SearchIndexSchema.Performances
                        ? request with
                        {
                            PerPage = 40,
                        }
                    : retainNoteQuery && request.Collection == SearchIndexSchema.Recordings
                        ? request with
                        {
                            Q = SearchText.Fold(input.Text),
                        }
                    : request
                )
                .ToArray();
        }
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
        return input.Page == 1 && input.Scope is (SearchScope.All or SearchScope.Shows)
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
        var ranked = SearchRanker.Primary(
            query,
            rankTerms,
            input.Scope,
            SearchRanker.KnownItems(query, input, evidence),
            candidates,
            input.Page,
            browsePerformances: input.Scope == SearchScope.Shows && performanceTerms == "*"
        );
        return SearchRanker.PreferPlayedVenueMatch(input, query, ranked, evidence);
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
        int searchesLeft,
        CancellationToken cancellationToken
    )
    {
        var candidates = visible;
        if (input.Scope == SearchScope.All && searchesLeft > 0 && playback.Browse)
        {
            var playablePromotion = SearchRequests.ScopedTracks(query, playback.Artist, venues);
            var replies = await Execute([playablePromotion], cancellationToken);
            candidates = replies[0].Documents;
        }

        var playable = candidates.Where(result =>
            result is { Kind: "performance", Preview: not null }
            && (playback.Artist == null || result.ArtistSlug == playback.Artist)
            && (venues.Count == 0 || venues.Contains(result.VenueId))
            && (!query.Soundboard || result.Preview.Soundboard)
        );
        return SearchRanker.LeadWithPerformances(visible, playable, input.Scope == SearchScope.All);
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
                    Collection = collections.Name(request.Collection),
                }
            )
            .ToArray();
        using var response = await _typesense.MultiSearch(new { searches }, cancellationToken);
        return SearchRequests.Decode(response, plan);
    }

    private static IReadOnlySet<string> RecognizedVenues(
        SearchInput input,
        SearchQuery query,
        SearchVocabulary vocabulary
    ) =>
        !input.AddTracks
        || query.Reading is "artist_identity" or "artist_name_prefix"
        || input.Ignore?.Contains("venue") == true
        || vocabulary.ReadPlaceAlias(query.Terms, query.Artist) != null
            ? new HashSet<string>()
            : vocabulary.RecognizeVenues(query.Terms);

    private static bool IsSearchFailure(Exception error) =>
        error is HttpRequestException or InvalidDataException or System.Text.Json.JsonException;

    private static List<SearchChip> Chips(
        SearchInput input,
        SearchQuery query,
        SearchDocument? artist,
        SearchQuery? alternate
    )
    {
        var chips = new List<SearchChip>();
        if (artist != null)
        {
            chips.Add(new("artist", artist.Label, artist.Id));
        }
        if (query.Country is { } country && (alternate == null || alternate.Country == country))
        {
            chips.Add(new("country", country, country));
        }
        if (query.Date is int date && (alternate == null || alternate.Date == date))
        {
            var label = FormattableString.Invariant(
                $"{date / 10000:0000}-{date / 100 % 100:00}-{date % 100:00}"
            );
            chips.Add(new("date", label, label));
        }
        if (query.Year is int year && (alternate == null || alternate.Year == year))
        {
            var label = year.ToString(CultureInfo.InvariantCulture);
            chips.Add(new("year", label, label));
        }
        if (query.Month is int month && (alternate == null || alternate.Month == month))
        {
            chips.Add(
                new(
                    "month",
                    CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month),
                    month.ToString(CultureInfo.InvariantCulture)
                )
            );
        }
        if (query.DayKey is int day && (alternate == null || alternate.DayKey == day))
        {
            var label = FormattableString.Invariant($"{day / 100}/{day % 100}");
            chips.Add(new("day", label, label));
        }
        if (query.Soundboard && input.Scope == SearchScope.Sources)
        {
            chips.Add(new("soundboard", "Soundboard", "true"));
        }
        if (
            query.Matrix
            && (
                input.Scope == SearchScope.Sources
                || (input.Scope == SearchScope.All && !input.Typing)
            )
        )
        {
            chips.Add(new("matrix", "Matrix", "true"));
        }
        if (query.Tour is { } tour && (alternate == null || alternate.Tour == tour))
        {
            chips.Add(new("run", tour, tour));
        }
        return chips;
    }

    private static SearchTabCounts CountTabs(
        SearchInput input,
        InterpretedQuery reading,
        CatalogQueryEvidence evidence,
        RetrievalPlan retrieval,
        RetrievalBatch batches
    )
    {
        var counts = SearchTabCounts.Empty();
        var observed = retrieval
            .Primary.Zip(batches.Primary)
            .Concat(retrieval.Nickname.Zip(batches.Nickname))
            .Concat(retrieval.Alternative.Zip(batches.Alternative))
            .ToArray();
        if (input.Scope != SearchScope.All)
        {
            var totals = observed
                .Select(pair => SearchTabCounts.Read(pair.First, [pair]))
                .ToArray();
            counts.Add(
                input.Scope,
                totals.Sum(total => total.Count),
                totals.All(total => total.Exact)
            );
            return counts;
        }

        foreach (var scope in Enum.GetValues<SearchScope>())
        {
            if (
                scope == SearchScope.All
                || (scope == SearchScope.Reviews && !reading.ReviewsAvailable)
            )
            {
                continue;
            }

            // Reuse totals from the requests already made in All. A different
            // reading or filter is a different result set, so a missing total is
            // unknown rather than zero. No requests are sent for tab counts.
            var scoped = input with
            {
                Scope = scope,
                Page = 1,
            };
            var tabReading = Interpret(scoped, evidence, reading.UsePrimary);
            var tab = PlanRetrieval(scoped, tabReading, evidence.Vocabulary);
            var requests = tab.Primary.Concat(tab.Nickname);
            if (scope == SearchScope.Shows)
            {
                requests = requests.Select(request =>
                    ScopedShowPage.Request(request, 0, ScopedShowPage.Size)
                );
            }

            var totals = requests
                .Select(request => SearchTabCounts.Read(request, observed))
                .ToArray();
            var passages =
                scope == SearchScope.Shows ? FindPassages(scoped, tabReading, evidence).Count : 0;
            counts.Add(
                scope,
                passages + totals.Sum(total => total.Count),
                totals.All(total => total.Exact)
            );
        }

        return counts;
    }
}
