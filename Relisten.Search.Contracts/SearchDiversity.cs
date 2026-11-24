namespace Relisten.Search;

public static class SearchDiversity
{
    private const int VersionsPerItem = 3;
    public const int AddTracksPageSize = 48;
    public const int SongPageSize = 48;
    public const int RelatedTrackLimit = 6;

    public static RankedCandidate? FocusedSong(IReadOnlyList<RankedCandidate> ranked)
    {
        var songs = ranked
            .Where(candidate =>
                candidate.Match.Tier == MatchTier.Exact
                && candidate.Row is ISearchRankItem { Kind: "song", SongId.Length: > 0 }
            )
            .DistinctBy(candidate => ((ISearchRankItem)candidate.Row).SongId)
            .Take(2)
            .ToArray();
        if (songs is not [var song] || song.Row is not ISearchRankItem identity)
        {
            return null;
        }
        // An artist identity or a different song at a verified place gives
        // the listener another plausible goal for this query.
        var competingReading = ranked.Any(candidate =>
            (candidate.Row.Kind == "artist" && candidate.Match.Tier >= MatchTier.Prefix)
            || (
                candidate.Playable
                && candidate.Match.Tier == MatchTier.Exact
                && candidate.Match.Fields.Contains("place")
                && !IsSongVersion(candidate, identity)
            )
        );
        return competingReading ? null : song;
    }

    /// <summary>Give playable versions room in Add Tracks and keep a few routes to browse.</summary>
    public static RankedCandidate[] AddTracksPage(
        IReadOnlyList<RankedCandidate> ranked,
        bool allowSongFocus = true
    )
    {
        var versions = ranked
            .Where(candidate => candidate.Playable && !candidate.Related)
            .ToArray();
        if (versions.Length == 0)
        {
            return ranked.Take(AddTracksPageSize).ToArray();
        }

        // If the exact answer cannot be added directly, its drill-in can lead.
        var lead = ranked[0].Match.Tier > versions[0].Match.Tier ? ranked[0] : null;
        var focus = allowSongFocus ? FocusedSong(ranked) : null;
        var song = focus;
        if (
            song == lead
            || (song?.Row is ISearchRankItem identity && !IsSongVersion(versions[0], identity))
        )
        {
            song = null;
        }
        var matchingShows = focus?.Row is ISearchRankItem focused
            ? versions
                .Where(candidate => IsSongVersion(candidate, focused))
                .Select(candidate => ((ISearchRankItem)candidate.Row).ShowId)
                .ToHashSet(StringComparer.Ordinal)
            : null;
        var browse = ranked
            .Where(candidate =>
                !candidate.Related
                && !candidate.Playable
                && candidate != lead
                && (song == null || candidate.Row.Kind != "song")
            )
            .Where(candidate =>
                matchingShows is not { Count: > 0 }
                || candidate.Row is not ISearchRankItem { Kind: "show" } show
                || matchingShows.Contains(show.ShowId)
            )
            .Where(candidate =>
                candidate.Row.Kind is "show" or "song" or "sequence"
                || (candidate.Row.Kind == "artist" && candidate.Match.Tier >= MatchTier.Prefix)
            )
            .DistinctBy(candidate => candidate.Row.Kind)
            .Take(3)
            .ToArray();
        var related = ranked
            .Where(candidate => candidate.Playable && candidate.Related)
            .Take(RelatedTrackLimit)
            .ToArray();
        var reviews = ranked
            .Where(candidate => candidate.Row.Kind == "review" && candidate != lead)
            .Take(4);
        return (lead == null ? [] : new[] { lead })
            .Append(versions[0])
            .Concat(song == null ? [] : new[] { song })
            .Concat(
                versions
                    .Skip(1)
                    .Take(
                        AddTracksPageSize
                            - browse.Length
                            - related.Length
                            - 1
                            - (lead == null ? 0 : 1)
                            - (song == null ? 0 : 1)
                    )
            )
            .Concat(browse)
            .Concat(related)
            .Concat(reviews)
            .Take(AddTracksPageSize)
            .ToArray();
    }

    public static (RankedCandidate[] Results, string[] MoreScopes) ExplorePage(
        IReadOnlyList<RankedCandidate> ranked,
        bool allowSongFocus = true
    )
    {
        if (!allowSongFocus || FocusedSong(ranked)?.Row is not ISearchRankItem song)
        {
            var (results, scopes) = Limit(ranked);
            return (results.Take(24).ToArray(), scopes);
        }

        bool FromSong(RankedCandidate candidate) =>
            candidate.Row is ISearchRankItem { Kind: "song" } row && row.SongId == song.SongId
            || IsSongVersion(candidate, song);
        var index = ranked.TakeWhile(candidate => !FromSong(candidate)).Count();
        var ordered = ranked
            .Take(index)
            .Concat(
                ranked
                    .Skip(index)
                    .Where(FromSong)
                    .OrderByDescending(candidate => candidate.Row.Kind == "song")
            )
            .Concat(ranked.Skip(index).Where(candidate => !FromSong(candidate)))
            .ToArray();
        var (focused, moreScopes) = Limit(ordered, song);
        if (focused.Skip(SongPageSize).Any(FromSong) && !moreScopes.Contains("shows"))
        {
            moreScopes = [.. moreScopes, "shows"];
        }
        return (focused.Take(SongPageSize).ToArray(), moreScopes);
    }

    /// <summary>Limit repeated versions in All while preserving the rank of their parent rows.</summary>
    public static (RankedCandidate[] Results, string[] MoreScopes) Limit(
        IReadOnlyList<RankedCandidate> ranked,
        ISearchRankItem? focusedSong = null
    )
    {
        var occurrences = new Dictionary<(string Scope, string Item), int>();
        var more = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<RankedCandidate>(ranked.Count);
        foreach (var candidate in ranked)
        {
            if (
                VersionGroup(candidate.Row) is { } group
                && (focusedSong == null || !IsSongVersion(candidate, focusedSong))
            )
            {
                var count = occurrences.GetValueOrDefault(group);
                if (count == VersionsPerItem)
                {
                    more.Add(group.Scope);
                    continue;
                }

                occurrences[group] = count + 1;
            }

            results.Add(candidate);
        }

        return (results.ToArray(), more.Order(StringComparer.Ordinal).ToArray());
    }

    private static bool IsSongVersion(RankedCandidate candidate, ISearchRankItem song)
    {
        if (!candidate.Playable || candidate.Row is not ISearchRankItem version)
        {
            return false;
        }

        return version.SongId == song.SongId
            || (
                song.SongId.StartsWith("imported_song_", StringComparison.Ordinal)
                && version.SongId.Length == 0
                && version.ArtistSlug == song.ArtistSlug
                && MatchEvidence.Fold(version.Name) == MatchEvidence.Fold(song.Name)
            );
    }

    private static (string Scope, string Item)? VersionGroup(ISearchRankItem row) =>
        row switch
        {
            ISearchRankItem { Kind: "performance", SongId.Length: > 0 } performance => (
                "shows",
                performance.SongId
            ),
            ISearchRankItem { Kind: "performance", Name.Length: > 0, ArtistSlug.Length: > 0 } raw =>
                ("shows", $"raw:{raw.ArtistSlug}:{MatchEvidence.Fold(raw.Name)}"),
            ISearchRankItem { Kind: "recording", ShowId.Length: > 0 } recording => (
                "sources",
                recording.ShowId
            ),
            _ => null,
        };
}
