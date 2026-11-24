using System.Collections.Generic;
using System.Linq;

namespace Relisten.Search;

internal static class SearchPlayTieBreak
{
    public static IReadOnlyList<SearchDocument> Rank(
        SearchInput input,
        SearchQuery query,
        IReadOnlyList<SearchDocument> ranked,
        CatalogQueryEvidence evidence
    )
    {
        if (
            input.Page != 1
            || input.AddTracks
            || input.ArtistId != null
            || query.Artist != null
            || query.ReadingArtist != null
            || query.Reading is ("artist_identity" or "artist_name_prefix" or "artist_with_context")
            || query.Chronological
            || query.Order != "regular"
            || query.Terms.Length == 0
            || ranked.FirstOrDefault() is not { } leader
            || !Eligible(leader)
        )
        {
            return ranked;
        }

        var tier = MatchQuality(leader, query.Terms);
        // A place-qualified performance can already lead an exact-title song.
        // Keep that decision and never swap a partial hit past a literal one.
        if (!tier.Exact && ranked.Any(hit => hit.Name == query.Terms))
        {
            return ranked;
        }

        var counts = new (CountKind Kind, long Plays)[ranked.Count];
        for (var index = 0; index < ranked.Count; index++)
        {
            var hit = ranked[index];
            if (!Eligible(hit) || MatchQuality(hit, query.Terms) != tier)
            {
                continue;
            }

            if (
                input.BoostArtistIds?.Any(id =>
                    evidence.Vocabulary.ArtistSlugsById.GetValueOrDefault(id) == hit.ArtistSlug
                ) == true
            )
            {
                break;
            }

            counts[index] = Starts(hit, evidence);
        }

        return SortComparableRuns(ranked, counts);
    }

    private static IReadOnlyList<SearchDocument> SortComparableRuns(
        IReadOnlyList<SearchDocument> ranked,
        (CountKind Kind, long Plays)[] counts
    )
    {
        SearchDocument[]? revised = null;
        var start = 0;
        while (start < counts.Length)
        {
            var kind = counts[start].Kind;
            if (kind == CountKind.None)
            {
                start++;
                continue;
            }

            // Crossing an incomparable row would reverse a pair without a
            // like-for-like comparison. Sort only consecutive comparable rows.
            var end = start + 1;
            while (end < counts.Length && counts[end].Kind == kind)
            {
                end++;
            }

            var indices = Enumerable.Range(start, end - start);
            if (indices.Any(i => ranked[i].ArtistSlug != ranked[start].ArtistSlug))
            {
                revised ??= ranked.ToArray();
                var sorted = indices.OrderByDescending(i => counts[i].Plays);
                var slot = start;
                foreach (var i in sorted)
                {
                    revised[slot++] = ranked[i];
                }
            }

            start = end;
        }

        return revised ?? ranked;
    }

    private static bool Eligible(SearchDocument hit) =>
        hit.ArtistSlug.Length > 0
        && !hit.Soundcheck
        && hit.Kind is ("song" or "performance")
        && (hit.Kind != "performance" || hit.Preview != null);

    private static (bool Exact, double CatalogQuality, long TextScore, string Kind) MatchQuality(
        SearchDocument hit,
        string terms
    ) =>
        hit.Name == terms ? (true, 0, 0, "")
        : hit.CatalogQuality > 0 ? (false, hit.CatalogQuality, 0, "")
        : (false, 0, hit.Score, hit.Kind);

    private static (CountKind Kind, long Plays) Starts(
        SearchDocument hit,
        CatalogQueryEvidence evidence
    )
    {
        if (hit.TrackPlays > 0)
        {
            return (CountKind.Performance, hit.TrackPlays);
        }

        var songId = hit.Kind == "song" ? hit.Id : hit.SongId;
        var songPlays = evidence.SongPlays(songId);
        if (songPlays == 0)
        {
            songPlays = evidence.TitlePlays(hit.ArtistSlug, hit.Name);
        }

        return songPlays > 0 ? (CountKind.Song, songPlays) : default;
    }

    private enum CountKind
    {
        None,
        Performance,
        Song,
    }
}
