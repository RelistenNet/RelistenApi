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
                """
                SELECT s.id canonical_id,COALESCE(c.matched_track_count,0) matched_track_count,
                       COALESCE(c.matched_show_count,0) matched_show_count
                FROM norm.songs s LEFT JOIN norm.song_counts c ON c.song_id=s.id
                WHERE s.artist_uuid=@artistUuid AND s.merged_into IS NULL
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
