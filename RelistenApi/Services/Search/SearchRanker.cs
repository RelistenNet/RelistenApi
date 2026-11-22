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
                && result.Name == first.Name
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
        var prefix = page == 1 ? known.Where(result => InScope(result, scope)).ToArray() : [];
        var ids = prefix.Select(result => result.Id).ToHashSet(StringComparer.Ordinal);
        return prefix
            .Concat(ordered.Where(result => !ids.Contains(result.Id)))
            .Take(scope == SearchScope.All ? VisibleLimit : int.MaxValue)
            .Select(LabelSoundcheck)
            .ToArray();
    }

    public static bool InScope(SearchDocument result, SearchScope scope) =>
        scope switch
        {
            SearchScope.All => true,
            SearchScope.Artists => result.Kind == "artist",
            SearchScope.Songs => result.Kind == "song",
            SearchScope.Venues => result.Kind == "venue",
            SearchScope.Shows => result.Kind is "show" or "tour" or "performance",
            SearchScope.Sources => result.Kind == "recording",
            SearchScope.Reviews => result.Kind == "review",
            _ => false,
        };

    public static IReadOnlyList<SearchDocument> AddTracksFirst(
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
    )
    {
        if (
            query.Terms.Length != 0
            || query.Artist == null
            || query.Country == null
            || query.Year == null
        )
        {
            return visible;
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
            || vocabulary.FindTour(query.Artist, tours[0]) is not { } tour
        )
        {
            return visible;
        }

        return
        [
            tour with
            {
                Country = query.Country,
                BrowseReason = "one tour spans the matched venues",
            },
            .. visible.Take(VisibleLimit - 1),
        ];
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

    private static SearchDocument LabelSoundcheck(SearchDocument result) =>
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
