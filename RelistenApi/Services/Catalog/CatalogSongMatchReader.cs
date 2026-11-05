using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Aggregates file durations per show without transferring the full recording match list.
    /// Shows remain members even when no file has a classifiable, positive duration.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, CatalogSongShowTimings>> ReadShowTimings(
        CatalogReadSession session,
        Guid artistUuid,
        string canonicalId,
        CancellationToken cancellationToken
    )
    {
        // Matching ordinals already include this song's merged aliases. Any other
        // resolved song part identifies a different song; repeated parts still count one file.
        var rows = await session.Connection.QueryAsync<ShowTimingRow>(
            session.Command(
                """
                WITH files AS (
                    SELECT m.track_uuid,m.show_uuid,
                           CASE WHEN bool_or(p.resolved AND p.song_id IS NOT NULL
                                             AND NOT p.part=ANY(m.matching_part_ordinals)) THEN true
                                WHEN bool_and(p.resolved) THEN false END is_multi_song,
                           CASE WHEN t.duration>0 THEN t.duration END duration
                    FROM norm.song_recording_matches(@artistUuid,@canonicalIds::text[]) m
                    JOIN source_tracks t ON t.uuid=m.track_uuid
                    JOIN norm.track_songs p ON p.track_uuid=m.track_uuid
                    GROUP BY m.track_uuid,m.show_uuid,t.duration
                )
                SELECT show_uuid,is_multi_song,count(duration)::integer track_count,
                       min(duration) min,max(duration) max,avg(duration)::double precision avg,
                       percentile_cont(0.5) WITHIN GROUP (ORDER BY duration) median
                FROM files GROUP BY show_uuid,is_multi_song
                """,
                new { artistUuid, canonicalIds = new[] { canonicalId } },
                cancellationToken
            )
        );

        var timings = new Dictionary<Guid, CatalogSongShowTimings>();
        foreach (var row in rows)
        {
            if (!timings.TryGetValue(row.show_uuid, out var show))
            {
                show = new();
                timings.Add(row.show_uuid, show);
            }

            if (row.track_count == 0 || row.is_multi_song is null)
            {
                continue;
            }

            var statistics = new CatalogTrackDurationStatistics(
                row.track_count,
                row.min,
                row.max,
                row.avg,
                row.median
            );
            if (row.is_multi_song == false)
            {
                show.SingleSong = statistics;
            }
            else
            {
                show.MultiSong = statistics;
            }
        }
        return timings;
    }

    private sealed class ShowTimingRow
    {
        public Guid show_uuid { get; set; }
        public bool? is_multi_song { get; set; }
        public int track_count { get; set; }
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
