using System;
using System.Collections.Generic;
using System.Linq;

namespace Relisten.Search;

internal static class SearchRanker
{
    public const int VisibleLimit = 15;
    private const int KnownItemLimit = 6;
    private const int ExactTitleQuality = 6;
    private const int SameTitleCandidateLimit = 50;
    private const double SongPlayWeight = 0.35;
    private const double ListenerArtistBonus = 4;

    // Require an order-of-magnitude lead before plays outweigh an exact title.
    // The Tahoe replay had 402 starts for Phish and 6 for the visible cover.
    private const double ExactSongPlayAdvantage = 10;

    public static IReadOnlyList<SearchDocument> KnownItems(
        SearchQuery query,
        SearchInput input,
        CatalogQueryEvidence evidence
    )
    {
        if (
            query.HasDate
            || query.Country != null
            || query.Matrix
            || query.Soundboard
            || query.Order != "regular"
        )
        {
            return [];
        }

        var kind = input.Scope switch
        {
            SearchScope.Artists => "artist",
            SearchScope.Songs => "song",
            SearchScope.Venues => "venue",
            _ => null,
        };
        if (kind == null && input.Scope != SearchScope.All)
        {
            return [];
        }

        var vocabulary = evidence.Vocabulary;
        // A selected artist makes two-character song prefixes specific enough
        // to promote. Global promotion waits for a third character.
        var known = vocabulary.Lookup(
            query.Terms,
            query.Artist,
            curated: true,
            fuzzy: true,
            minLength: query.Artist == null ? 3 : 2,
            kind: kind
        );
        if (
            query is { VerifiedArtistPrefix: true, ReadingArtist: { } artist }
            && input.Scope is (SearchScope.All or SearchScope.Artists)
            && vocabulary.ArtistDocuments.TryGetValue(artist, out var artistDocument)
        )
        {
            return [artistDocument, .. known.Where(document => document.Id != artistDocument.Id)];
        }

        if (
            known.FirstOrDefault() is not { Kind: "song", CatalogQuality: ExactTitleQuality } first
            || input.Scope is not (SearchScope.All or SearchScope.Songs)
        )
        {
            return known;
        }

        var boosts = BoostedArtists(query, input, vocabulary);
        var tied = vocabulary
            .Lookup(query.Terms, query.Artist, curated: true, limit: SameTitleCandidateLimit)
            .Where(result =>
                result.Kind == "song"
                && result.CatalogQuality == ExactTitleQuality
                && (
                    result.Name == first.Name
                    || result.Name == SearchVocabulary.AlternateSongTitle(first.Name)
                )
            )
            .ToArray();
        if (tied.Length < 2)
        {
            return known;
        }

        // Client affinity only reorders exact same-title candidates. It does not
        // supply an artist constraint or expand the candidate population.
        // Show count gets more weight than recent plays. A fixed artist bonus
        // lets listener affinity matter even when the two catalogs differ in size.
        var ranked = tied.OrderByDescending(result =>
            Math.Log(1d + result.Popularity)
            + SongPlayWeight * Math.Log(1d + evidence.SongPlays(result.Id))
            + (boosts.Contains(result.ArtistSlug) ? ListenerArtistBonus : 0d)
        );
        var ids = tied.Select(result => result.Id).ToHashSet(StringComparer.Ordinal);
        return ranked
            .Concat(known.Where(result => !ids.Contains(result.Id)))
            .Take(KnownItemLimit)
            .ToArray();
    }

    public static IReadOnlyList<SearchDocument> Primary(
        SearchQuery query,
        string rankTerms,
        SearchScope scope,
        IReadOnlyList<SearchDocument> known,
        IEnumerable<SearchDocument> candidates,
        int page,
        bool browsePerformances = false
    )
    {
        var terms = rankTerms.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ranked = candidates
            .Where(result =>
                result.Kind != "performance" || browsePerformances || SharesTitle(result, terms)
            )
            .OrderByDescending(result => Score(result, query, rankTerms));

        var emittedShows = new HashSet<string>(StringComparer.Ordinal);
        var distinct = ranked
            .Where(result =>
                scope != SearchScope.All
                || result.ShowId.Length == 0
                || result.Kind is not ("show" or "performance")
                || emittedShows.Add(result.ShowId)
            )
            .Take(scope == SearchScope.All ? VisibleLimit : int.MaxValue)
            .ToArray();

        // For calendar queries, show the most recent of the relevant matches first.
        var ordered = query.Chronological
            ? distinct.OrderByDescending(result => result.Date)
            : distinct.AsEnumerable();
        // Scoped rows come from one index stream. Prepending vocabulary hits
        // would put the same result on page one and again at its index offset.
        var prefix = page == 1 && scope == SearchScope.All ? known : [];
        var ids = prefix.Select(result => result.Id).ToHashSet(StringComparer.Ordinal);
        return prefix
            .Concat(ordered.Where(result => !ids.Contains(result.Id)))
            .Take(scope == SearchScope.All ? VisibleLimit : int.MaxValue)
            .Select(LabelSoundcheck)
            .ToArray();
    }

    public static IReadOnlyList<SearchDocument> PreferPlayedVenueMatch(
        SearchInput input,
        SearchQuery query,
        IReadOnlyList<SearchDocument> ranked,
        CatalogQueryEvidence evidence
    )
    {
        var vocabulary = evidence.Vocabulary;
        if (
            ranked.FirstOrDefault() is not { Kind: "song" } song
            || !IsUnscopedExactSongLead(input, query, song)
            || input.BoostArtistIds?.Any(id =>
                vocabulary.ArtistSlugsById.GetValueOrDefault(id) == song.ArtistSlug
            ) == true
        )
        {
            return ranked;
        }

        // An exact song title has a strong claim. A performance by another
        // artist needs an exact venue-and-title reading and much stronger
        // playback evidence. Imported songs lack song-wide play totals, so
        // count their strongest visible performance as evidence too.
        var exactPerformanceStarts = ranked
            .Where(result =>
                result.Kind == "performance"
                && result.ArtistSlug == song.ArtistSlug
                && result.Name == query.Terms
            )
            .Select(result => result.TrackPlays)
            .DefaultIfEmpty()
            .Max();
        var exactStarts = Math.Max(evidence.SongPlays(song.Id), exactPerformanceStarts);
        var requiredStarts = ExactSongPlayAdvantage * (1d + exactStarts);
        var performance = ranked
            .Where(result =>
                result is { Kind: "performance", Preview: not null, Soundcheck: false }
                && result.ArtistSlug != song.ArtistSlug
                && 1d + result.TrackPlays >= requiredStarts
                && NamesVenueAndSong(query.Terms, result, vocabulary)
            )
            .MaxBy(result => result.TrackPlays);

        return performance == null
            ? ranked
            : [performance, .. ranked.Where(result => result.Id != performance.Id)];
    }

    private static bool IsUnscopedExactSongLead(
        SearchInput input,
        SearchQuery query,
        SearchDocument song
    ) => !input.AddTracks && IsUnscopedPlaceQuery(input, query) && song.Name == query.Terms;

    private static bool IsUnscopedPlaceQuery(SearchInput input, SearchQuery query) =>
        input is { Scope: SearchScope.All, Page: 1 }
        && query
            is {
                Artist: null,
                ReadingArtist: null,
                Country: null,
                Tour: null,
                HasDate: false,
                Soundboard: false,
                Matrix: false,
                Order: "regular"
            }
        && input.Ignore?.Contains("venue") != true
        && input.ReadingId != "literal"
        && query.Terms.Contains(' ');

    private static bool NamesVenueAndSong(
        string terms,
        SearchDocument performance,
        SearchVocabulary vocabulary
    ) =>
        PlaceBesideTitle(terms, performance.Name) is { } place
        && vocabulary.IsVenueAtPlace(place, performance.VenueId);

    public static IReadOnlyList<SearchDocument> PreferPlaceAndSong(
        SearchInput input,
        SearchQuery query,
        IReadOnlyList<SearchDocument> ranked,
        SearchVocabulary vocabulary
    )
    {
        // Let the existing exact-title rule handle queries that also name an item verbatim.
        if (
            !IsUnscopedPlaceQuery(input, query)
            || ranked.Count < 2
            || ranked.Any(result => result.Name == query.Terms)
        )
        {
            return ranked;
        }

        // "Denver Ghost" can name a performance in Denver or refer to words
        // in a festival or track title. Prefer the exact title at the named
        // place, then use comparable performance plays among those matches.
        var performance = ranked
            .Where(result =>
                result is { Kind: "performance", Preview: not null, Soundcheck: false }
                && PlaceBesideTitle(query.Terms, result.Name) is { } place
                && (
                    vocabulary.IsVenueAtPlace(place, result.VenueId)
                    || (
                        vocabulary.IsKnownPlace(place) && SearchText.Fold(result.CityLabel) == place
                    )
                )
            )
            .MaxBy(result => result.TrackPlays);

        return performance == null || performance.Id == ranked[0].Id
            ? ranked
            : [performance, .. ranked.Where(result => result.Id != performance.Id)];
    }

    private static string? PlaceBesideTitle(string terms, string title)
    {
        if (title.Length == 0)
        {
            return null;
        }

        if (terms.EndsWith(" " + title, StringComparison.Ordinal))
        {
            return terms[..^(title.Length + 1)];
        }

        if (terms.StartsWith(title + " ", StringComparison.Ordinal))
        {
            return terms[(title.Length + 1)..];
        }

        return null;
    }

    public static IReadOnlyList<SearchDocument> LeadWithPerformances(
        IReadOnlyList<SearchDocument> visible,
        IEnumerable<SearchDocument> playable,
        bool mixed
    )
    {
        var performances = playable.ToArray();
        if (performances.Length == 0)
        {
            return visible;
        }

        // Mixed search keeps one result per show. A paged scope must emit every
        // candidate it consumed, including other performances from the same show.
        var promoted = performances
            .Select(result => mixed ? result.ShowId : result.Id)
            .ToHashSet(StringComparer.Ordinal);
        return performances
            .Concat(visible.Where(result => !promoted.Contains(mixed ? result.ShowId : result.Id)))
            .Take(mixed ? VisibleLimit : int.MaxValue)
            .ToArray();
    }

    public static IReadOnlyList<SearchDocument> SuggestNamedRun(
        SearchRunFallback fallback,
        IReadOnlyList<SearchDocument> documents
    )
    {
        var wanted = fallback.Wanted;
        var eligible = documents
            .Where(result =>
            {
                var title = result.Name.Split(' ');
                return result.Preview != null
                    && title.Length == wanted.Length
                    && title.Skip(1).SequenceEqual(wanted.Skip(1))
                    && Distance(title[0], wanted[0]) <= 3;
            })
            .ToArray();

        if (eligible.Select(result => result.Name).Distinct(StringComparer.Ordinal).Count() != 1)
        {
            return [];
        }

        return eligible
            .Take(5)
            .Select(result => result with { SuggestedQuery = result.Label + " " + fallback.Run })
            .ToArray();
    }

    public static IReadOnlyList<SearchDocument> AddTourRoute(
        SearchQuery query,
        SearchVocabulary vocabulary,
        IReadOnlyList<SearchDocument> visible
    ) =>
        TourRoute(query, vocabulary, visible) is { } tour
            ? [tour, .. visible.Take(VisibleLimit - 1)]
            : visible;

    public static bool CanSuggestTourRoute(SearchQuery query) =>
        query is { Terms.Length: 0, Artist: not null, Country: not null, Year: not null };

    public static SearchDocument? TourRoute(
        SearchQuery query,
        SearchVocabulary vocabulary,
        IReadOnlyList<SearchDocument> visible
    )
    {
        if (!CanSuggestTourRoute(query) || query.Artist is not { } artist)
        {
            return null;
        }

        var shows = visible.Where(result => result.Kind == "show").ToArray();
        var tours = shows
            .Select(result => SearchText.Fold(result.TourLabel))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var venues = shows
            .Select(result => result.VenueId)
            .Where(id => id.Length != 0)
            .Distinct(StringComparer.Ordinal);
        if (
            tours.Length != 1
            || venues.Count() < 3
            || vocabulary.FindTour(artist, tours[0]) is not { } tour
        )
        {
            return null;
        }

        return tour with
        {
            Country = query.Country!,
            BrowseReason = "one tour spans the matched venues",
        };
    }

    private static HashSet<string> BoostedArtists(
        SearchQuery query,
        SearchInput input,
        SearchVocabulary vocabulary
    )
    {
        if (
            input.BoostArtistIds == null
            || query.Artist != null
            || query.Reading != null
            || query.HasDate
            || query.Country != null
            || vocabulary.Places(query.Terms).Any()
        )
        {
            return [];
        }

        return input
            .BoostArtistIds.Select(id => vocabulary.ArtistSlugsById.GetValueOrDefault(id))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
    }

    private static (int Bucket, int Category, decimal First, decimal Second, decimal Third) Score(
        SearchDocument result,
        SearchQuery query,
        string terms
    )
    {
        var category = result.Soundcheck ? 0 : 1;
        if (result.Kind == "performance" && query.Order != "regular")
        {
            var metric = query.Order switch
            {
                "longest" => (decimal)result.Longest,
                "last" => result.Date,
                "popular" => result.TrackPlays,
                _ => 0,
            };
            return (3, category, metric, result.Score, (decimal)result.Popularity);
        }

        if (terms.Length == 0 && query.DayKey != null)
        {
            return (0, category, result.Date, (decimal)result.Popularity, 0);
        }

        var exact =
            terms.Length != 0
            && result.Name == terms
            && result.Kind is "artist" or "venue" or "song" or "tour";
        return (exact ? 2 : 0, category, result.Score, (decimal)result.Popularity, result.Date);
    }

    private static bool SharesTitle(SearchDocument result, string[] terms)
    {
        var words = (result.Name + " " + result.Aliases).Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries
        );
        return terms.Any(term => words.Contains(term, StringComparer.Ordinal))
            || (
                terms.Length != 0
                && terms[^1].Length >= 2
                && words.Any(word => word.StartsWith(terms[^1], StringComparison.Ordinal))
            );
    }

    internal static SearchDocument LabelSoundcheck(SearchDocument result) =>
        result is { Soundcheck: true, Preview.Soundcheck: true }
            ? result with
            {
                Label = result.Preview.Label,
                Name = SearchText.Fold(result.Preview.Label),
            }
            : result;

    private static int Distance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[previous.Length];
        for (var a = 0; a < left.Length; a++)
        {
            current[0] = a + 1;
            for (var b = 0; b < right.Length; b++)
            {
                var replace = previous[b] + (left[a] == right[b] ? 0 : 1);
                current[b + 1] = Math.Min(replace, Math.Min(previous[b + 1] + 1, current[b] + 1));
            }
            (previous, current) = (current, previous);
        }
        return previous[^1];
    }
}
