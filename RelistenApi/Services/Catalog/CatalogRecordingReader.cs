using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Catalog;

/// <summary>
/// Loads v4 recording hierarchies in two round trips within the caller's snapshot.
/// Sources are read once; sets, playable tracks and links are batched by source ID.
/// Normalization and response projection remain outside this loader.
/// </summary>
public sealed class CatalogRecordingReader
{
    public Task<IReadOnlyList<CatalogSource>> ReadForShow(
        CatalogReadSession session,
        long showId,
        CancellationToken cancellationToken
    ) => Read(session, "src.show_id=@showId", new { showId }, cancellationToken);

    public async Task<CatalogSource?> ReadSource(
        CatalogReadSession session,
        Guid uuid,
        CancellationToken cancellationToken
    ) => (await Read(session, "src.uuid=@uuid", new { uuid }, cancellationToken)).SingleOrDefault();

    internal Task<IReadOnlyList<CatalogSource>> ReadSources(
        CatalogReadSession session,
        Guid[] uuids,
        CancellationToken cancellationToken
    ) => Read(session, "src.uuid=ANY(@uuids)", new { uuids }, cancellationToken);

    private static async Task<IReadOnlyList<CatalogSource>> Read(
        CatalogReadSession session,
        string predicate,
        object parameters,
        CancellationToken cancellationToken
    )
    {
        // Resolve bounded source IDs first, then reuse them for the child reads.
        // Predicates are internal choices; identities are SQL parameters.
        var sources = (
            await session.Connection.QueryAsync<CatalogSource>(
                session.Command(
                    SourceSelect
                        + $" WHERE {predicate} ORDER BY src.avg_rating_weighted DESC,src.uuid",
                    parameters,
                    cancellationToken
                )
            )
        ).ToArray();
        if (sources.Length == 0)
        {
            return [];
        }

        using var rows = await session.Connection.QueryMultipleAsync(
            session.Command(
                """
                SELECT sets.*,a.uuid artist_uuid,src.uuid source_uuid
                FROM source_sets sets JOIN sources src ON src.id=sets.source_id
                JOIN artists a ON a.id=src.artist_id
                WHERE src.id=ANY(@sourceIds) ORDER BY sets.index,sets.uuid;

                SELECT t.*,src.uuid source_uuid,sets.uuid source_set_uuid,a.uuid artist_uuid,sh.uuid show_uuid
                FROM source_tracks t JOIN source_sets sets ON sets.id=t.source_set_id
                JOIN sources src ON src.id=t.source_id AND src.id=sets.source_id
                JOIN artists a ON a.id=src.artist_id JOIN shows sh ON sh.id=src.show_id
                WHERE src.id=ANY(@sourceIds) AND NOT t.is_orphaned
                ORDER BY t.track_position,t.uuid;

                SELECT l.*,src.uuid source_uuid,up.name provider FROM links l
                JOIN sources src ON src.id=l.source_id JOIN upstream_sources up ON up.id=l.upstream_source_id
                WHERE src.id=ANY(@sourceIds) ORDER BY l.id;
                """,
                new { sourceIds = sources.Select(source => source.id).ToArray() },
                cancellationToken
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

    internal const string SourceSelect = """
        SELECT src.*,a.uuid artist_uuid,sh.uuid show_uuid,
               COALESCE(rc.source_review_count,0) review_count
        FROM sources src JOIN artists a ON a.id=src.artist_id JOIN shows sh ON sh.id=src.show_id
        LEFT JOIN source_review_counts rc ON rc.source_id=src.id
        """;
}
