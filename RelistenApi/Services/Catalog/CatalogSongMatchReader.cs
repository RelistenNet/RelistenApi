using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Catalog;

/// <summary>
/// Reads stored song counts and recording matches in the caller's snapshot. Both are kept
/// by <c>norm.refresh_song_counts</c>, which every import, normalization run and admin edit
/// calls after its source writes, so they lag those writes together and reads never refresh
/// them. Matching a large song live costs hundreds of milliseconds per request (16k files
/// for Grateful Dead "Drums"); the stored rows also carry each show's ranked sources. The
/// caller owns approval gating.
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
                    FROM norm.song_files
                    WHERE artist_uuid=@artistUuid AND song_id=@canonicalId
                    ORDER BY show_date,show_uuid,source_uuid,track_position,track_uuid
                    """,
                    new { artistUuid, canonicalId },
                    cancellationToken
                )
            )
        ).ToArray();

    /// <summary>
    /// Tests candidate shows against the stored matches complete REST reads use.
    /// A song shared by several page requests is read once for their union of shows.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlySet<Guid>>> ReadShowMembershipBatch(
        CatalogReadSession session,
        IReadOnlyList<(string Song, Guid[] Shows)> requests,
        CancellationToken token
    )
    {
        if (requests.Count == 0 || requests.All(request => request.Shows.Length == 0))
        {
            return new Dictionary<string, IReadOnlySet<Guid>>();
        }

        var rows = await session.Connection.QueryAsync<SongShowMembershipRow>(
            session.Command(
                """
                WITH requested_songs AS MATERIALIZED (
                    SELECT s.id,s.artist_uuid,array_agg(DISTINCT sh.show_uuid) show_uuids
                    FROM jsonb_to_recordset(@inputs::jsonb) AS i(song text,shows uuid[])
                    JOIN norm.songs s ON s.id=i.song
                    JOIN artists a ON a.uuid=s.artist_uuid
                    JOIN features f ON f.artist_id=a.id AND f.norm_catalogue
                    CROSS JOIN LATERAL unnest(i.shows) sh(show_uuid)
                    GROUP BY s.id,s.artist_uuid
                )
                SELECT DISTINCT s.id song_id,m.show_uuid
                FROM requested_songs s
                JOIN norm.song_files m ON m.song_id=s.id AND m.artist_uuid=s.artist_uuid
                  AND m.show_uuid=ANY(s.show_uuids)
                """,
                new
                {
                    inputs = JsonSerializer.Serialize(
                        requests.Select(r => new { song = r.Song, shows = r.Shows })
                    ),
                },
                token
            )
        );
        return rows.GroupBy(row => row.song_id)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlySet<Guid>)group.Select(row => row.show_uuid).ToHashSet()
            );
    }

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
        // Each stored file is classified once (is_multi_song) before per-source maxima,
        // even when its parts repeat the song or its merged aliases.
        var rows = await session.Connection.QueryAsync<ShowStatisticsRow>(
            session.Command(
                """
                WITH inputs AS (
                    SELECT * FROM jsonb_to_recordset(@inputs::jsonb)
                    AS i(ordinal int,artist_uuid uuid,song_id text,show_uuids uuid[])
                ), sources AS (
                    SELECT i.ordinal,f.show_uuid,f.source_uuid,
                           max(f.duration) FILTER (WHERE NOT f.is_multi_song) single_song_duration,
                           max(f.duration) FILTER (WHERE f.is_multi_song) multi_song_duration
                    FROM inputs i
                    JOIN norm.song_files f ON f.song_id=i.song_id AND f.artist_uuid=i.artist_uuid
                    WHERE i.show_uuids IS NULL OR f.show_uuid=ANY(i.show_uuids)
                    GROUP BY i.ordinal,f.show_uuid,f.source_uuid
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
    /// Each show's sources containing the song are ordered by <see cref="CatalogSourceRanking"/>
    /// (stored as source_rank); Top keeps only the first, so a show's best source overall may
    /// not appear when it lacks the song. All returns every source's files in that order.
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
                    """
                    SELECT f.track_uuid uuid,f.source_uuid,f.show_uuid,t.title,t.slug,t.duration,t.mp3_url
                    FROM norm.song_files f
                    JOIN source_tracks t ON t.uuid=f.track_uuid
                    WHERE f.artist_uuid=@artistUuid AND f.song_id=@canonicalId
                      AND (@allSources OR f.source_rank=1)
                    ORDER BY f.show_date,f.show_uuid,f.source_rank,f.track_position,f.track_uuid
                    """,
                    new
                    {
                        artistUuid,
                        canonicalId,
                        allSources = selection == CatalogSongShowTracks.All,
                    },
                    cancellationToken
                )
            )
        ).ToArray();

    private sealed class SongShowMembershipRow
    {
        public string song_id { get; set; } = null!;
        public Guid show_uuid { get; set; }
    }

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
