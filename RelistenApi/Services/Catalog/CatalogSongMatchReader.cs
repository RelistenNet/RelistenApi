using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Catalog;

/// <summary>
/// Reads stored song counts and live recording matches in the caller's snapshot.
/// Recounts and discovery use the same SQL membership function. Counts may briefly
/// lag recording changes; reads never refresh them. The caller owns approval gating.
/// </summary>
public sealed class CatalogSongMatchReader
{
    public async Task<IReadOnlyDictionary<string, CatalogSongMatchCounts>> ReadCounts(
        CatalogReadSession session,
        Guid? artistUuid,
        IReadOnlyList<string>? canonicalIds,
        CancellationToken cancellationToken
    )
    {
        if (artistUuid is null && canonicalIds is null)
        {
            throw new ArgumentException("Song counts require an artist scope or explicit IDs.");
        }
        if (canonicalIds is { Count: 0 })
        {
            return new Dictionary<string, CatalogSongMatchCounts>();
        }

        var counts = await session.Connection.QueryAsync<CatalogSongMatchCounts>(
            session.Command(
                """
                SELECT s.id canonical_id,COALESCE(c.matched_track_count,0) matched_track_count,
                       COALESCE(c.matched_show_count,0) matched_show_count
                FROM norm.songs s LEFT JOIN norm.song_counts c ON c.song_id=s.id
                WHERE (@artistUuid::uuid IS NULL OR s.artist_uuid=@artistUuid) AND s.merged_into IS NULL
                  AND (@canonicalIds::text[] IS NULL OR s.id=ANY(@canonicalIds))
                """,
                new { artistUuid, canonicalIds = canonicalIds?.Distinct().ToArray() },
                cancellationToken
            )
        );
        return counts.ToDictionary(row => row.canonical_id);
    }

    public async Task<IReadOnlyList<CatalogSongMatchRow>> ReadMatches(
        CatalogReadSession session,
        Guid artistUuid,
        string canonicalId,
        CancellationToken cancellationToken
    ) =>
        (
            await session.Connection.QueryAsync<CatalogSongMatchRow>(
                session.Command(
                    """
                    SELECT track_uuid,show_uuid
                    FROM norm.song_recording_matches(@artistUuid,@canonicalIds::text[])
                    ORDER BY show_date,show_uuid,source_uuid,track_position,track_uuid
                    """,
                    new { artistUuid, canonicalIds = new[] { canonicalId } },
                    cancellationToken
                )
            )
        ).ToArray();

    /// <summary>
    /// Aggregates source maxima without transferring the full recording match list.
    /// Shows remain members even when no file has a classifiable, positive duration.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, CatalogSongShowStatistics>> ReadShowStatistics(
        CatalogReadSession session,
        Guid artistUuid,
        string canonicalId,
        CancellationToken cancellationToken
    ) =>
        (
            await ReadShowStatisticsBatch(
                session,
                [new CatalogSongStatisticsRequest(artistUuid, canonicalId, null)],
                cancellationToken
            )
        )[0];

    /// <summary>Page-scoped statistics for multiple song relationships in one command.</summary>
    public async Task<
        IReadOnlyList<IReadOnlyDictionary<Guid, CatalogSongShowStatistics>>
    > ReadShowStatisticsBatch(
        CatalogReadSession session,
        IReadOnlyList<CatalogSongStatisticsRequest> requests,
        CancellationToken cancellationToken
    )
    {
        if (requests.Count == 0)
        {
            return [];
        }
        // Matching ordinals include this song's merged aliases. Classify each file
        // once before choosing per-source maxima, even when its parts repeat the song.
        var rows = await session.Connection.QueryAsync<ShowStatisticsRow>(
            session.Command(
                """
                WITH inputs AS (
                    SELECT * FROM jsonb_to_recordset(@inputs::jsonb)
                    AS i(ordinal int,artist_uuid uuid,song_id text,show_uuids uuid[])
                ), files AS (
                    SELECT i.ordinal,m.track_uuid,m.show_uuid,m.source_uuid,
                           CASE WHEN bool_or(p.resolved AND p.song_id IS NOT NULL
                                             AND NOT p.part=ANY(m.matching_part_ordinals)) THEN true
                                WHEN bool_and(p.resolved) THEN false END is_multi_song,
                           CASE WHEN t.duration>0 THEN t.duration END duration
                    FROM inputs i CROSS JOIN LATERAL norm.song_recording_matches(i.artist_uuid,ARRAY[i.song_id]) m
                    JOIN source_tracks t ON t.uuid=m.track_uuid
                    JOIN norm.track_songs p ON p.track_uuid=m.track_uuid
                    WHERE i.show_uuids IS NULL OR m.show_uuid=ANY(i.show_uuids)
                    GROUP BY i.ordinal,m.track_uuid,m.show_uuid,m.source_uuid,t.duration
                ), sources AS (
                    SELECT ordinal,show_uuid,source_uuid,
                           max(duration) FILTER (WHERE NOT is_multi_song) single_song_duration,
                           max(duration) FILTER (WHERE is_multi_song) multi_song_duration
                    FROM files GROUP BY ordinal,show_uuid,source_uuid
                )
                SELECT ordinal,show_uuid,sample.kind,count(sample.value)::integer source_count,
                       min(sample.value) min,max(sample.value) max,avg(sample.value)::double precision avg,
                       percentile_cont(0.5) WITHIN GROUP (ORDER BY sample.value) median
                FROM sources CROSS JOIN LATERAL (
                    VALUES ('single_song',single_song_duration),
                           ('multi_song',multi_song_duration)
                ) sample(kind,value)
                GROUP BY ordinal,show_uuid,sample.kind
                """,
                new
                {
                    inputs = JsonSerializer.Serialize(
                        requests.Select(
                            (r, index) =>
                                new
                                {
                                    ordinal = index,
                                    artist_uuid = r.Artist,
                                    song_id = r.Song,
                                    show_uuids = r.Shows,
                                }
                        )
                    ),
                },
                cancellationToken
            )
        );

        var statistics = requests
            .Select(_ => new Dictionary<Guid, CatalogSongShowStatistics>())
            .ToArray();
        foreach (var row in rows)
        {
            if (!statistics[row.ordinal].TryGetValue(row.show_uuid, out var show))
            {
                show = new();
                statistics[row.ordinal].Add(row.show_uuid, show);
            }

            if (row.source_count == 0)
            {
                continue;
            }

            var values = new CatalogSourceStatistics(
                row.source_count,
                row.min,
                row.max,
                row.avg,
                row.median
            );
            switch (row.kind)
            {
                case "single_song":
                    show.SingleSong = values;
                    break;
                case "multi_song":
                    show.MultiSong = values;
                    break;
            }
        }
        return statistics;
    }

    /// <summary>
    /// One slim row per matched file, including files without usable timing samples.
    /// Each show's sources containing the song are ordered by <see cref="CatalogSourceRanking"/>;
    /// Top keeps only the first, so a show's best source overall may not appear when it
    /// lacks the song. All returns every source's files in that order.
    /// </summary>
    public async Task<IReadOnlyList<CatalogSongShowTrack>> ReadShowTracks(
        CatalogReadSession session,
        Guid artistUuid,
        string canonicalId,
        CatalogSongShowTracks selection,
        CancellationToken cancellationToken
    ) =>
        (
            await session.Connection.QueryAsync<CatalogSongShowTrack>(
                session.Command(
                    $"""
                    WITH matches AS MATERIALIZED (
                        SELECT * FROM norm.song_recording_matches(@artistUuid,@canonicalIds::text[])
                    ), ranked_sources AS (
                        SELECT matched.show_uuid,matched.source_uuid,
                               row_number() OVER (
                                   PARTITION BY matched.show_uuid
                                   ORDER BY {CatalogSourceRanking.OrderBy("src")}
                               ) source_rank
                        FROM (SELECT DISTINCT show_uuid,source_uuid FROM matches) matched
                        JOIN sources src ON src.uuid=matched.source_uuid
                    )
                    SELECT m.track_uuid uuid,m.source_uuid,m.show_uuid,t.title,t.slug,t.duration,t.mp3_url
                    FROM matches m
                    JOIN ranked_sources r ON r.show_uuid=m.show_uuid AND r.source_uuid=m.source_uuid
                    JOIN source_tracks t ON t.uuid=m.track_uuid
                    WHERE @allSources OR r.source_rank=1
                    ORDER BY m.show_date,m.show_uuid,r.source_rank,m.track_position,m.track_uuid
                    """,
                    new
                    {
                        artistUuid,
                        canonicalIds = new[] { canonicalId },
                        allSources = selection == CatalogSongShowTracks.All,
                    },
                    cancellationToken
                )
            )
        ).ToArray();

    private sealed class ShowStatisticsRow
    {
        public int ordinal { get; set; }
        public Guid show_uuid { get; set; }
        public string kind { get; set; } = null!;
        public int source_count { get; set; }
        public int min { get; set; }
        public int max { get; set; }
        public double avg { get; set; }
        public double median { get; set; }
    }
}

public sealed record CatalogSongMatchCounts(
    string canonical_id,
    int matched_track_count,
    int matched_show_count
);

public sealed class CatalogSongMatchRow
{
    public Guid track_uuid { get; set; }
    public Guid show_uuid { get; set; }
}

public sealed record CatalogSongStatisticsRequest(Guid Artist, string Song, Guid[]? Shows);
