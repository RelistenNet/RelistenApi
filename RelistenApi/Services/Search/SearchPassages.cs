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

namespace Relisten.Search;

internal sealed record SearchPassage(
    string ArtistSlug,
    string ShowId,
    string SourceId,
    string Set,
    int Date,
    string Venue,
    ImmutableArray<string> SongIds,
    ImmutableArray<string> TrackIds,
    ImmutableArray<SearchPassagePart> Parts
)
{
    public string Kind => "sequence";
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

    public IReadOnlyList<SearchPassage> Find(SearchQuery query)
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
            foreach (var match in rule.Matches)
            {
                if (
                    query.MatchesDate(match.Passage.Date)
                    && remainder.All(match.PlaceWords.Contains)
                )
                {
                    matches.Add(match.Passage);
                }
            }
        }

        return matches
            .OrderByDescending(match => match.Date)
            .ThenByDescending(match => match.ShowId, StringComparer.Ordinal)
            .Take(8)
            .ToArray();
    }

    public static async Task<SearchPassages> Load(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SearchVocabulary vocabulary,
        CancellationToken cancellationToken
    )
    {
        var resolved = new List<ResolvedRule>();
        foreach (var rule in SearchVocabularyRules.Passages)
        {
            var songs = rule
                .SongTitles.Select(title => vocabulary.FindSong(rule.Artist, title)?.Id)
                .ToArray();
            if (songs.All(song => song is not null))
            {
                resolved.Add(new(rule, songs.Select(song => song!).ToImmutableArray()));
            }
        }

        if (resolved.Count == 0)
        {
            return Empty;
        }

        var candidates = resolved.ToLookup(rule => (rule.Rule.Artist, rule.SongIds[0]));
        var matches = resolved.ToDictionary(rule => rule, _ => new List<PassageMatch>());
        var rows = connection.QueryUnbufferedAsync<PartRow>(
            ReadCandidateSets,
            new
            {
                artists = resolved.Select(rule => rule.Rule.Artist).Distinct().ToArray(),
                firstSongs = resolved.Select(rule => rule.SongIds[0]).Distinct().ToArray(),
            },
            transaction: transaction,
            commandTimeout: 600
        );

        // One set's rows are enough to establish every adjacent path. Keep the
        // original slots, including files with no resolved part or no playable URL.
        var set = new List<PartRow>();
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            if (set.Count > 0 && set[0].SetId != row.SetId)
            {
                AddSet(set, candidates, matches);
                set.Clear();
            }

            set.Add(row);
        }

        AddSet(set, candidates, matches);
        return new SearchPassages(
            resolved
                .Select(rule => new RuleMatches(rule.Rule, matches[rule].ToImmutableArray()))
                .ToImmutableArray()
        );
    }

    private static void AddSet(
        List<PartRow> rows,
        ILookup<(string Artist, string FirstSong), ResolvedRule> candidates,
        Dictionary<ResolvedRule, List<PassageMatch>> matches
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
                    matches[rule].Add(Describe(path, rule.SongIds, date));
                }
            }
        }
    }

    // An import may supply multiple track UUIDs for one physical slot. Preserve
    // one complete take and prefer playable files, then more resolved parts.
    private static (bool Playable, int MappedParts, Guid Track) TakePriority(
        IGrouping<Guid, PartRow> take
    ) => (take.First().Playable, take.Count(row => row.SongId is not null), take.Key);

    private static bool MatchesSongs(ReadOnlySpan<PartRow> path, ImmutableArray<string> songs)
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
        ImmutableArray<string> songs,
        int date
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
        var aliases = SearchVocabularyRules
            .PlaceAliases.Where(alias =>
                alias.Artist == first.Artist
                && place.Contains(alias.Place, StringComparison.Ordinal)
            )
            .Select(alias => alias.Alias);
        var placeWords = place
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Concat(aliases.SelectMany(alias => alias.Split(' ')))
            .ToFrozenSet();
        return new(
            new SearchPassage(
                first.Artist,
                UuidTypeId.Format("show", first.ShowUuid),
                UuidTypeId.Format("source", first.SourceUuid),
                first.SetName,
                date,
                first.Venue,
                songs,
                proof.Select(part => part.TrackId).Distinct().ToImmutableArray(),
                proof
            ),
            placeWords
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
            SELECT artist.slug, artist.id AS artist_id, show.uuid AS show_uuid, show.date,
                   coalesce(venue.name, '') AS venue, coalesce(venue.location, '') AS location,
                   source.uuid AS source_uuid, source.id AS source_id, sets.id AS set_id,
                   coalesce(sets.name, '') AS set_name
            FROM candidate_sets candidate JOIN source_sets sets ON sets.id = candidate.source_set_id
            JOIN sources source ON source.id = sets.source_id
            JOIN shows show ON show.id = source.show_id AND show.artist_id = source.artist_id
            JOIN artists artist ON artist.id = source.artist_id
            LEFT JOIN venues venue ON venue.id = show.venue_id
            WHERE artist.slug = ANY(@artists)
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
               coalesce(part.segue_after, false) AS SegueAfter
        FROM set_headers header
        JOIN source_tracks track ON track.source_set_id = header.set_id AND track.source_id = header.source_id
                                AND track.artist_id = header.artist_id AND NOT track.is_orphaned
        LEFT JOIN norm.track_songs part ON part.track_uuid = track.uuid
        ORDER BY header.slug, header.source_uuid, header.set_id, track.track_position, track.uuid, part.part
        """;

    internal sealed record RuleMatches(
        SearchVocabularyRules.PassageRule Rule,
        ImmutableArray<PassageMatch> Matches
    );

    internal sealed record PassageMatch(SearchPassage Passage, FrozenSet<string> PlaceWords);

    private sealed record ResolvedRule(
        SearchVocabularyRules.PassageRule Rule,
        ImmutableArray<string> SongIds
    );

    private sealed record PartRow(
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
        bool SegueAfter
    );
}
