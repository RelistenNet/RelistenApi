using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Catalog;

/// <summary>
/// Loads imported recording hierarchies in two round trips. Sources, sets, tracks
/// and links have separate result sets so large source descriptions are read once.
/// The caller owns the open connection and optional snapshot. No normalization,
/// popularity, caching or API serialization runs here.
/// </summary>
public sealed class CatalogRecordingReader
{
    /// <summary>Stable order adds identity tie-breakers; legacy adapters retain rank-only ties.</summary>
    public Task<IReadOnlyList<CatalogSource>> ReadForShow(
        IDbConnection connection,
        long showId,
        long? artistId = null,
        bool stableOrder = true,
        IDbTransaction? transaction = null,
        CancellationToken cancellationToken = default
    ) =>
        Read(
            connection,
            "src.show_id=@showId AND (@artistId::bigint IS NULL OR src.artist_id=@artistId)",
            new { showId, artistId },
            stableOrder,
            transaction,
            cancellationToken
        );

    public async Task<CatalogSource?> ReadSource(
        IDbConnection connection,
        Guid uuid,
        IDbTransaction? transaction,
        CancellationToken cancellationToken
    ) =>
        (
            await Read(
                connection,
                "src.uuid=@uuid",
                new { uuid },
                true,
                transaction,
                cancellationToken
            )
        ).SingleOrDefault();

    private static async Task<IReadOnlyList<CatalogSource>> Read(
        IDbConnection connection,
        string predicate,
        object parameters,
        bool stableOrder,
        IDbTransaction? transaction,
        CancellationToken cancellationToken
    )
    {
        // Resolve bounded source IDs first, then reuse them for each child read.
        // Predicates and ordering are internal choices; identities are parameters.
        var sources = (
            await connection.QueryAsync<CatalogSource>(
                new CommandDefinition(
                    $"""
                SELECT src.*,a.uuid artist_uuid,v.uuid venue_uuid,sh.uuid show_uuid,
                       COALESCE(rc.source_review_count,0) review_count
                FROM sources src JOIN artists a ON a.id=src.artist_id
                LEFT JOIN venues v ON v.id=src.venue_id JOIN shows sh ON sh.id=src.show_id
                LEFT JOIN source_review_counts rc ON rc.source_id=src.id
                WHERE {predicate} ORDER BY src.avg_rating_weighted DESC{(
                    stableOrder ? ",src.uuid" : ""
                )}
                """,
                    parameters,
                    transaction,
                    commandTimeout: 30,
                    cancellationToken: cancellationToken
                )
            )
        ).ToArray();
        if (sources.Length == 0)
        {
            return [];
        }

        using var rows = await connection.QueryMultipleAsync(
            new CommandDefinition(
                $"""
                SELECT sets.*,a.uuid artist_uuid,src.uuid source_uuid
                FROM source_sets sets JOIN sources src ON src.id=sets.source_id
                JOIN artists a ON a.id=src.artist_id
                WHERE src.id=ANY(@sourceIds) ORDER BY sets.index{(stableOrder ? ",sets.uuid" : "")};

                SELECT t.*,src.uuid source_uuid,sets.uuid source_set_uuid,a.uuid artist_uuid,sh.uuid show_uuid
                FROM source_tracks t JOIN source_sets sets ON sets.id=t.source_set_id
                JOIN sources src ON src.id=t.source_id AND src.id=sets.source_id
                JOIN artists a ON a.id=src.artist_id JOIN shows sh ON sh.id=src.show_id
                WHERE src.id=ANY(@sourceIds) AND NOT t.is_orphaned
                ORDER BY t.track_position{(stableOrder ? ",t.uuid" : "")};

                SELECT l.*,src.uuid source_uuid,up.name provider FROM links l
                JOIN sources src ON src.id=l.source_id JOIN upstream_sources up ON up.id=l.upstream_source_id
                WHERE src.id=ANY(@sourceIds){(stableOrder ? " ORDER BY l.id" : "")};
                """,
                new { sourceIds = sources.Select(source => source.id).ToArray() },
                transaction,
                commandTimeout: 30,
                cancellationToken: cancellationToken
            )
        );
        var sets = (await rows.ReadAsync<CatalogSet>()).ToArray();
        var tracks = (await rows.ReadAsync<CatalogTrack>()).ToLookup(track => track.source_set_id);
        var links = (await rows.ReadAsync<CatalogLink>()).ToLookup(link => link.source_id);

        foreach (var set in sets)
        {
            set.Tracks = tracks[set.id].ToList();
        }
        var setsBySource = sets.ToLookup(set => set.source_id);
        foreach (var source in sources)
        {
            source.Sets = setsBySource[source.id].ToList();
            source.Links = links[source.id].ToList();
        }
        return sources;
    }
}
