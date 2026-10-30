using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Catalog;

/// <summary>
/// Reads exact recording matches in the caller's snapshot. Discovery and its counts
/// share one membership rule; the caller owns the artist's approved-read decision.
/// </summary>
public sealed class CatalogSongMatchReader
{
    public async Task<IReadOnlyDictionary<string, CatalogSongMatchCounts>> ReadCounts(
        CatalogReadSession session,
        Guid artistUuid,
        IReadOnlyList<string>? canonicalIds,
        CancellationToken cancellationToken
    )
    {
        if (canonicalIds is { Count: 0 })
        {
            return new Dictionary<string, CatalogSongMatchCounts>();
        }

        var counts = await session.Connection.QueryAsync<CatalogSongMatchCounts>(
            session.Command(
                MatchMembership
                    + """

                    SELECT canonical_id,count(DISTINCT track_uuid)::int matched_track_count,
                           count(DISTINCT show_uuid)::int matched_show_count
                    FROM valid_matches GROUP BY canonical_id
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
                    MatchMembership
                        + """

                        SELECT track_uuid,show_uuid,matching_part_ordinals
                        FROM valid_matches
                        ORDER BY show_date,show_uuid,source_uuid,track_position,track_uuid
                        """,
                    new { artistUuid, canonicalIds = new[] { canonicalId } },
                    cancellationToken
                )
            )
        ).ToArray();

    // Reverse traversal starts only from surviving songs, so cycles, broken chains,
    // cross-artist links and chains longer than the projection's 32 nodes are omitted.
    // Keep the complete map even for a single-song read: another resolved part with
    // an invalid link makes the entire track interpretation unavailable.
    private const string MatchMembership = """
        WITH RECURSIVE song_map AS (
            SELECT s.id,s.id canonical_id,ARRAY[s.id] path
            FROM norm.songs s
            WHERE s.artist_uuid=@artistUuid AND s.merged_into IS NULL
            UNION ALL
            SELECT s.id,m.canonical_id,m.path || s.id
            FROM song_map m JOIN norm.songs s ON s.merged_into=m.id
            WHERE s.artist_uuid=@artistUuid AND cardinality(m.path)<32
              AND NOT s.id=ANY(m.path)
        ), candidate_files AS MATERIALIZED (
            SELECT m.canonical_id,t.uuid track_uuid,t.source_id,t.source_set_id,
                   t.artist_id,t.track_position,array_agg(ts.part ORDER BY ts.part) matching_part_ordinals
            FROM song_map m JOIN norm.track_songs ts ON ts.song_id=m.id AND ts.resolved
            JOIN source_tracks t ON t.uuid=ts.track_uuid AND NOT t.is_orphaned
            WHERE @canonicalIds::text[] IS NULL OR m.canonical_id=ANY(@canonicalIds)
            GROUP BY m.canonical_id,t.uuid,t.source_id,t.source_set_id,t.artist_id,t.track_position
        ), invalid_files AS MATERIALIZED (
            SELECT DISTINCT part.track_uuid
            FROM norm.track_songs part
            JOIN (SELECT DISTINCT track_uuid FROM candidate_files) c ON c.track_uuid=part.track_uuid
            LEFT JOIN song_map m ON m.id=part.song_id
            WHERE part.resolved AND part.song_id IS NOT NULL AND m.id IS NULL
        ), valid_matches AS (
            SELECT c.canonical_id,c.track_uuid,sh.uuid show_uuid,c.matching_part_ordinals,
                   sh.date show_date,src.uuid source_uuid,c.track_position
            FROM candidate_files c
            JOIN artists a ON a.id=c.artist_id AND a.uuid=@artistUuid
            JOIN sources src ON src.id=c.source_id AND src.artist_id=c.artist_id
            JOIN source_sets sets ON sets.id=c.source_set_id AND sets.source_id=src.id
            JOIN shows sh ON sh.id=src.show_id AND sh.artist_id=c.artist_id
            LEFT JOIN invalid_files invalid ON invalid.track_uuid=c.track_uuid
            WHERE invalid.track_uuid IS NULL
        )
        """;
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
    public int[] matching_part_ordinals { get; set; } = [];
}
