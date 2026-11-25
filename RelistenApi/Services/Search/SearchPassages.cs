using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Identifiers;
using Relisten.Services.Popularity;

namespace Relisten.Search;

internal sealed record SearchPassage(
    string ArtistSlug,
    string ShowId,
    string SourceId,
    string Set,
    int Date,
    string Venue,
    ImmutableArray<string> SongIds,
    ImmutableArray<string> SongTitles,
    ImmutableArray<string> TrackIds,
    ImmutableArray<SearchPassagePart> Parts,
    long? TrackPlays = null,
    double PopularityPercentile = 0,
    long? RecordingPlays = null
) : ISearchRankItem
{
    public string Kind => "sequence";
    public string Label => string.Join(" → ", SongTitles);
    public string Key => $"sequence:{ShowId}:{string.Join(':', SongIds)}";
}

internal sealed record SearchPassagePart(
    string SongId,
    int Position,
    int Part,
    string TrackId,
    bool SegueAfter
);

internal sealed class SearchPassages(ImmutableArray<SearchPassages.RuleMatches> rules)
{
    public static readonly SearchPassages Empty = new([]);
    private const int PopularityBatchSize = 512;
    private readonly Dictionary<double, double> _percentiles = CalculatePercentiles(rules);

    public IReadOnlyList<SearchPassage> Find(
        SearchQuery query,
        IReadOnlySet<string>? venues = null,
        bool allowAliases = true,
        int limit = 8
    )
    {
        // A passage carries ordered files and place text. Country and recording
        // filters require typed attributes for the chosen source.
        if (
            string.IsNullOrEmpty(query.Terms)
            || !string.IsNullOrEmpty(query.Country)
            || query.Matrix
            || query.Soundboard
        )
        {
            return [];
        }

        var matches = new List<SearchPassage>();
        var paddedTerms = " " + query.Terms + " ";
        foreach (var rule in rules)
        {
            if (!string.IsNullOrEmpty(query.Artist) && query.Artist != rule.Rule.Artist)
            {
                continue;
            }

            var phrase = rule.Rule.Phrases.FirstOrDefault(value =>
                paddedTerms.Contains(" " + value + " ", StringComparison.Ordinal)
            );
            if (phrase is null)
            {
                continue;
            }

            var position = paddedTerms.IndexOf(" " + phrase + " ", StringComparison.Ordinal);
            var remainder = paddedTerms
                .Remove(position + 1, phrase.Length)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var place = string.Join(' ', remainder);
            foreach (var match in rule.Matches)
            {
                if (
                    query.MatchesDate(match.Passage.Date)
                    && (venues == null || venues.Contains(match.VenueId))
                    && (
                        remainder.All(match.PlaceWords.Contains)
                        || (allowAliases && match.Aliases?.Contains(place) == true)
                    )
                )
                {
                    matches.Add(match.Passage);
                }
            }
        }

        return BestRecordings(matches)
            .OrderByDescending(match =>
                query.Order switch
                {
                    "first" => -match.Date,
                    "last" => match.Date,
                    _ => 0,
                }
            )
            .ThenByDescending(match => match.TrackPlays)
            .ThenByDescending(match => match.Date)
            .ThenByDescending(match => match.ShowId, StringComparer.Ordinal)
            .Take(limit)
            .Select(match =>
                match with
                {
                    PopularityPercentile = _percentiles.GetValueOrDefault(
                        match.TrackPlays.GetValueOrDefault()
                    ),
                }
            )
            .ToArray();
    }

    private static IEnumerable<SearchPassage> BestRecordings(IEnumerable<SearchPassage> matches) =>
        matches
            .GroupBy(match => match.Key, StringComparer.Ordinal)
            .Select(group => group.MaxBy(match => match.RecordingPlays)!);

    private static Dictionary<double, double> CalculatePercentiles(
        ImmutableArray<RuleMatches> rules
    )
    {
        var distribution = new SearchPercentiles();
        // Each show and ordered sequence contributes once across the snapshot.
        // Extra tapes and alternate spellings of a query cannot move the scale.
        foreach (
            var match in BestRecordings(
                rules.SelectMany(rule => rule.Matches).Select(match => match.Passage)
            )
        )
        {
            distribution.Observe(match.Kind, match.TrackPlays.GetValueOrDefault());
        }

        return distribution.Calculate().GetValueOrDefault("sequence") ?? [];
    }

    public static async Task<ImmutableArray<RuleMatches>> Discover(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        SearchVocabulary vocabulary,
        IReadOnlyDictionary<string, SearchProjectionRows.Venue> venues,
        CancellationToken cancellationToken
    )
    {
        var resolved = new List<ResolvedRule>();
        foreach (var rule in SearchReviewedNamedShows.Passages)
        {
            var songs = rule
                .SongTitles.Select(title => vocabulary.FindSong(rule.Artist, title))
                .ToArray();
            if (songs.All(song => song is not null))
            {
                resolved.Add(
                    new(
                        rule,
                        songs.Select(song => song!.Id).ToImmutableArray(),
                        songs.Select(song => song!.Label).ToImmutableArray()
                    )
                );
            }
        }

        if (resolved.Count == 0)
        {
            return [];
        }

        return await ReadMatches(
            connection,
            transaction,
            resolved,
            vocabulary,
            venues,
            cancellationToken
        );
    }

    private static async Task<ImmutableArray<RuleMatches>> ReadMatches(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        List<ResolvedRule> resolved,
        SearchVocabulary vocabulary,
        IReadOnlyDictionary<string, SearchProjectionRows.Venue> venues,
        CancellationToken cancellationToken
    )
    {
        var candidates = resolved.ToLookup(rule => (rule.Rule.Artist, rule.SongIds[0]));
        var matches = resolved.ToDictionary(rule => rule, _ => new List<PassageMatch>());
        var rows = connection.QueryUnbufferedAsync<PartRow>(
            ReadCandidateSets,
            new
            {
                artists = resolved.Select(rule => rule.Rule.Artist).Distinct().ToArray(),
                firstSongs = resolved.Select(rule => rule.SongIds[0]).Distinct().ToArray(),
            },
            transaction: transaction
        );

        // One set's rows are enough to establish every adjacent path. Keep the
        // original slots, including files with no resolved part or no playable URL.
        var set = new List<PartRow>();
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            if (set.Count > 0 && set[0].SetId != row.SetId)
            {
                AddSet(set, candidates, matches, vocabulary);
                set.Clear();
            }

            set.Add(
                row with
                {
                    VenueId = SearchProjectionRows.CurrentVenue(venues, row.VenueId)?.Id,
                }
            );
        }

        AddSet(set, candidates, matches, vocabulary);
        return resolved
            .Select(rule => new RuleMatches(rule.Rule, matches[rule].ToImmutableArray()))
            .ToImmutableArray();
    }

    public static async Task<SearchPassages> RankByPublishedStarts(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        ImmutableArray<RuleMatches> rules,
        CancellationToken cancellationToken
    )
    {
        // Only confirmed passages need popularity. Joining metrics to every
        // candidate setlist row changes the planner's join order and does more work.
        var passages = rules
            .SelectMany(rule => rule.Matches)
            .Select(match => match.Passage)
            .ToArray();
        var performances = await ReadPerformanceStarts(
            connection,
            transaction,
            passages.SelectMany(ShowSongs),
            cancellationToken
        );
        var recordings = await ReadRecordingStarts(
            connection,
            transaction,
            passages.Select(Source),
            cancellationToken
        );

        return new SearchPassages(
            rules
                .Select(rule =>
                    rule with
                    {
                        Matches = rule
                            .Matches.Select(match =>
                                match with
                                {
                                    Passage = WithPlays(match.Passage, performances, recordings),
                                }
                            )
                            .ToImmutableArray(),
                    }
                )
                .ToImmutableArray()
        );
    }

    private static async Task<Dictionary<(Guid Show, string Song), long>> ReadPerformanceStarts(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IEnumerable<(Guid Show, string Song)> songs,
        CancellationToken cancellationToken
    )
    {
        var starts = new Dictionary<(Guid Show, string Song), long>();
        foreach (var batch in songs.Distinct().Chunk(PopularityBatchSize))
        {
            var metrics = await PopularityStore.ReadSelectedPerformances(
                connection,
                batch.Select(item => item.Show).ToArray(),
                batch.Select(item => item.Song).ToArray(),
                transaction,
                cancellationToken
            );
            foreach (var (key, value) in metrics)
            {
                if (value.windows.Year is { } year)
                {
                    starts.Add(key, year.plays);
                }
            }
        }

        return starts;
    }

    private static async Task<Dictionary<Guid, long>> ReadRecordingStarts(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IEnumerable<Guid> sources,
        CancellationToken cancellationToken
    )
    {
        var starts = new Dictionary<Guid, long>();
        foreach (var batch in sources.Distinct().Chunk(PopularityBatchSize))
        {
            var metrics = await PopularityStore.ReadRecordingMetrics(
                connection,
                batch,
                transaction,
                cancellationToken
            );
            foreach (var (source, value) in metrics)
            {
                if (value.windows.Year is { } year)
                {
                    starts.Add(source, year.plays);
                }
            }
        }

        return starts;
    }

    private static SearchPassage WithPlays(
        SearchPassage passage,
        IReadOnlyDictionary<(Guid Show, string Song), long> performances,
        IReadOnlyDictionary<Guid, long> recordings
    )
    {
        long? plays = null;
        foreach (var key in ShowSongs(passage))
        {
            if (performances.TryGetValue(key, out var count))
            {
                plays = plays.GetValueOrDefault() + count;
            }
        }

        return passage with
        {
            TrackPlays = plays,
            RecordingPlays = recordings.TryGetValue(Source(passage), out var starts)
                ? starts
                : null,
        };
    }

    private static IEnumerable<(Guid Show, string Song)> ShowSongs(SearchPassage passage)
    {
        var show = UuidTypeId.Parse(passage.ShowId, "show");
        return passage.SongIds.Distinct(StringComparer.Ordinal).Select(song => (show, song));
    }

    private static Guid Source(SearchPassage passage) =>
        UuidTypeId.Parse(passage.SourceId, "source");

    private static void AddSet(
        List<PartRow> rows,
        ILookup<(string Artist, string FirstSong), ResolvedRule> candidates,
        Dictionary<ResolvedRule, List<PassageMatch>> matches,
        SearchVocabulary vocabulary
    )
    {
        var parts = rows.GroupBy(row => row.Position)
            .SelectMany(slot => slot.GroupBy(row => row.TrackUuid).MaxBy(TakePriority)!)
            .ToArray();

        for (var index = 0; index < parts.Length; index++)
        {
            var first = parts[index];
            if (!first.Playable || !first.SegueAfter || first.SongId is null)
            {
                continue;
            }

            foreach (var rule in candidates[(first.Artist, first.SongId)])
            {
                var count = rule.SongIds.Length;
                var date = first.Date.Year * 10_000 + first.Date.Month * 100 + first.Date.Day;
                if (
                    index + count > parts.Length
                    || (rule.Rule.Date is int required && required != date)
                )
                {
                    continue;
                }

                var path = parts.AsSpan(index, count);
                if (MatchesSongs(path, rule.SongIds))
                {
                    matches[rule].Add(Describe(path, rule, date, vocabulary));
                }
            }
        }
    }

    // An import may supply multiple track UUIDs for one physical slot. Preserve
    // one complete take and prefer playable files, then more resolved parts.
    private static (bool Playable, int MappedParts, Guid Track) TakePriority(
        IGrouping<Guid, PartRow> take
    ) => (take.First().Playable, take.Count(row => row.SongId is not null), take.Key);

    internal static bool MatchesSongs(ReadOnlySpan<PartRow> path, ImmutableArray<string> songs)
    {
        for (var index = 0; index < path.Length; index++)
        {
            if (!path[index].Playable || path[index].SongId != songs[index])
            {
                return false;
            }

            if (index > 0 && !Adjacent(path[index - 1], path[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Adjacent(PartRow left, PartRow right) =>
        left.SegueAfter
        && (
            left.TrackUuid == right.TrackUuid
                ? right.Part == left.Part + 1
                : right.Position == left.Position + 1
        );

    private static PassageMatch Describe(
        ReadOnlySpan<PartRow> path,
        ResolvedRule rule,
        int date,
        SearchVocabulary vocabulary
    )
    {
        var first = path[0];
        var parts = ImmutableArray.CreateBuilder<SearchPassagePart>(path.Length);
        foreach (var row in path)
        {
            parts.Add(
                new(
                    row.SongId!,
                    row.Position,
                    row.Part,
                    UuidTypeId.Format("track", row.TrackUuid),
                    row.SegueAfter
                )
            );
        }

        var proof = parts.MoveToImmutable();
        var place = SearchText.Fold(first.Venue + " " + first.Location);
        var venueId =
            first.VenueId
            ?? (
                first.ImportedVenueUuid is { } uuid ? UuidTypeId.Format("imported_venue", uuid) : ""
            );
        var aliases = vocabulary.AliasesAtVenue(venueId);
        var placeWords = place.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToFrozenSet();
        return new(
            new SearchPassage(
                first.Artist,
                UuidTypeId.Format("show", first.ShowUuid),
                UuidTypeId.Format("source", first.SourceUuid),
                first.SetName,
                date,
                first.Venue,
                rule.SongIds,
                rule.SongTitles,
                proof.Select(part => part.TrackId).Distinct().ToImmutableArray(),
                proof
            ),
            placeWords,
            venueId,
            aliases.ToFrozenSet(StringComparer.Ordinal)
        );
    }

    private const string ReadCandidateSets = """
        WITH RECURSIVE song_map AS (
            SELECT song.id, song.id AS canonical_id, ARRAY[song.id] AS path
            FROM norm.songs song JOIN artists artist ON artist.uuid = song.artist_uuid
            WHERE song.merged_into IS NULL AND artist.slug = ANY(@artists)
            UNION ALL
            SELECT song.id, map.canonical_id, map.path || song.id
            FROM song_map map JOIN norm.songs song ON song.merged_into = map.id
            WHERE cardinality(map.path) < 32 AND NOT song.id = ANY(map.path)
        ), candidate_sets AS MATERIALIZED (
            SELECT DISTINCT track.source_set_id
            FROM song_map map
            JOIN norm.track_songs part ON part.song_id = map.id AND part.resolved AND part.segue_after
            JOIN source_tracks track ON track.uuid = part.track_uuid AND NOT track.is_orphaned
            WHERE map.canonical_id = ANY(@firstSongs)
              AND (nullif(track.mp3_url, '') IS NOT NULL OR nullif(track.flac_url, '') IS NOT NULL)
        ), set_headers AS MATERIALIZED (
            -- OFFSET 0 keeps Postgres from flattening this lateral lookup.
            -- The set-ID-first plan keeps this read inside the replica's 20 s budget.
            SELECT header.*
            FROM candidate_sets candidate
            CROSS JOIN LATERAL (
                SELECT artist.slug, artist.id AS artist_id, show.uuid AS show_uuid, show.date,
                       coalesce(venue.name, '') AS venue, coalesce(venue.location, '') AS location,
                       source.uuid AS source_uuid, source.id AS source_id, sets.id AS set_id,
                     coalesce(sets.name, '') AS set_name,
                     CASE WHEN feature.norm_catalogue THEN mapped.venue_id END AS venue_id,
                     venue.uuid AS imported_venue_uuid
                FROM source_sets sets
                JOIN sources source ON source.id = sets.source_id
                JOIN shows show ON show.id = source.show_id AND show.artist_id = source.artist_id
                JOIN artists artist ON artist.id = source.artist_id
                LEFT JOIN features feature ON feature.artist_id = artist.id
                LEFT JOIN venues venue ON venue.id = show.venue_id
                LEFT JOIN norm.show_venues mapped ON mapped.show_uuid = show.uuid
                WHERE sets.id = candidate.source_set_id AND artist.slug = ANY(@artists)
                OFFSET 0
            ) header
        )
        SELECT header.slug AS Artist, header.show_uuid AS ShowUuid, header.date AS Date,
               header.venue AS Venue, header.location AS Location,
               header.source_uuid AS SourceUuid, header.set_id AS SetId, header.set_name AS SetName,
               coalesce(track.track_position, 0) AS Position, track.uuid AS TrackUuid,
               (nullif(track.mp3_url, '') IS NOT NULL OR nullif(track.flac_url, '') IS NOT NULL) AS Playable,
               coalesce(part.part, 0) AS Part,
               -- Build the canonical ID lookup once. A join can rescan the
               -- whole recursive map for every track part in these sets.
               CASE WHEN part.resolved THEN
                   (SELECT jsonb_object_agg(id, canonical_id) FROM song_map) ->> part.song_id
               END AS SongId,
               coalesce(part.segue_after, false) AS SegueAfter,
               header.venue_id AS VenueId, header.imported_venue_uuid AS ImportedVenueUuid
        FROM set_headers header
        JOIN source_tracks track ON track.source_set_id = header.set_id AND track.source_id = header.source_id
                                AND track.artist_id = header.artist_id AND NOT track.is_orphaned
        LEFT JOIN norm.track_songs part ON part.track_uuid = track.uuid
        ORDER BY header.slug, header.source_uuid, header.set_id, track.track_position, track.uuid, part.part
        """;

    internal sealed record RuleMatches(
        SearchReviewedNamedShows.PassageRule Rule,
        ImmutableArray<PassageMatch> Matches
    );

    internal sealed record PassageMatch(
        SearchPassage Passage,
        FrozenSet<string> PlaceWords,
        string VenueId = "",
        FrozenSet<string>? Aliases = null
    );

    private sealed record ResolvedRule(
        SearchReviewedNamedShows.PassageRule Rule,
        ImmutableArray<string> SongIds,
        ImmutableArray<string> SongTitles
    );

    internal sealed record PartRow(
        string Artist,
        Guid ShowUuid,
        DateOnly Date,
        string Venue,
        string Location,
        Guid SourceUuid,
        long SetId,
        string SetName,
        int Position,
        Guid TrackUuid,
        bool Playable,
        int Part,
        string? SongId,
        bool SegueAfter,
        string? VenueId = null,
        Guid? ImportedVenueUuid = null
    );
}
