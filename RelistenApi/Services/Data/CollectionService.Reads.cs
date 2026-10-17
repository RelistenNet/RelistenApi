using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;

namespace Relisten.Data;

public sealed partial class CollectionService
{
    private const string CollectionWithCounts = """
        SELECT c.*,counts.source_count,counts.show_count,counts.artist_count
        FROM collections c JOIN LATERAL (
            SELECT count(*) AS source_count,count(DISTINCT p.show_uuid) AS show_count,
                count(DISTINCT p.artist_uuid) AS artist_count
            FROM collection_playable_sources p WHERE p.collection_uuid=c.uuid
        ) counts ON true
        """;

    /// <summary>Lists published collections in slug/UUID order. Reads reflect the current catalog and are not cached.</summary>
    public async Task<CollectionPage<RelistenCollection>> ListAsync(
        int limit = 50,
        string? cursor = null
    )
    {
        var after = ReadCursor(cursor, "collections", Guid.Empty, limit);
        var rows = (
            await db.WithConnection(connection =>
                connection.QueryAsync<RelistenCollection>(
                    CollectionWithCounts
                        + """
                         WHERE c.published AND (@afterUuid IS NULL OR (c.slug COLLATE "C",c.uuid)>(@sort COLLATE "C",@afterUuid))
                         ORDER BY c.slug COLLATE "C",c.uuid LIMIT @take
                        """,
                    new
                    {
                        afterUuid = after?.Uuid,
                        sort = after?.Sort,
                        take = limit + 1,
                    }
                )
            )
        ).ToArray();
        return Page(rows, limit, row => NewCursor("collections", Guid.Empty, row.uuid, row.slug));
    }

    /// <summary>Finds a published collection by UUID or stable slug. Unpublished and missing collections both return null.</summary>
    public Task<RelistenCollection?> FindAsync(string uuidOrSlug)
    {
        var uuid = Guid.TryParse(uuidOrSlug, out var parsed) ? parsed : (Guid?)null;
        return db.WithConnection(connection =>
            connection.QuerySingleOrDefaultAsync<RelistenCollection>(
                CollectionWithCounts
                    + " WHERE c.published AND (c.uuid=@uuid OR (@uuid IS NULL AND c.slug=@uuidOrSlug))",
                new { uuid, uuidOrSlug }
            )
        );
    }

    /// <summary>Returns playable artist facets. Artist-wide inclusion is explicit and does not follow from a selected recording.</summary>
    public async Task<CollectionPage<CollectionArtist>?> ArtistsAsync(
        Guid collectionUuid,
        int limit = 50,
        string? cursor = null
    )
    {
        var after = ReadCursor(cursor, "artists", collectionUuid, limit);
        if (!await IsPublishedAsync(collectionUuid))
        {
            return null;
        }
        var rows = (
            await db.WithConnection(connection =>
                connection.QueryAsync<CollectionArtist>(
                    """
                    WITH facets AS (
                        SELECT p.artist_uuid AS uuid,p.artist_name AS name,p.artist_slug AS slug,p.artist_sort_name AS sort_name,
                            count(*) AS source_count,count(DISTINCT p.show_uuid) AS show_count,
                            EXISTS(SELECT 1 FROM normalization.collection_artist_memberships m
                                WHERE m.collection_uuid=@collectionUuid AND m.artist_uuid=p.artist_uuid AND m.active) AS includes_all_recordings
                        FROM collection_playable_sources p WHERE p.collection_uuid=@collectionUuid
                            AND EXISTS(SELECT 1 FROM collections c WHERE c.uuid=@collectionUuid AND c.published)
                        GROUP BY p.artist_uuid,p.artist_name,p.artist_slug,p.artist_sort_name
                    )
                    SELECT * FROM facets WHERE @afterUuid IS NULL OR (sort_name COLLATE "C",uuid)>(@sort COLLATE "C",@afterUuid)
                    ORDER BY sort_name COLLATE "C",uuid LIMIT @take
                    """,
                    new
                    {
                        collectionUuid,
                        afterUuid = after?.Uuid,
                        sort = after?.Sort,
                        take = limit + 1,
                    }
                )
            )
        ).ToArray();
        return Page(
            rows,
            limit,
            row => NewCursor("artists", collectionUuid, row.uuid, row.sort_name)
        );
    }

    /// <summary>
    /// Returns deduplicated canonical recordings in descending show-date, ascending source-UUID order.
    /// An artist filter selects only that performer's recordings already included in this collection.
    /// </summary>
    public async Task<CollectionPage<CollectionRecording>?> RecordingsAsync(
        Guid collectionUuid,
        int limit = 50,
        string? cursor = null,
        Guid? artistUuid = null
    )
    {
        var after = ReadCursor(cursor, "recordings", collectionUuid, limit, artistUuid);
        if (!await IsPublishedAsync(collectionUuid))
        {
            return null;
        }
        var rows = (
            await db.WithConnection(connection =>
                connection.QueryAsync<CollectionRecording>(
                    """
                    SELECT p.source_uuid,p.artist_uuid,p.artist_name,p.artist_slug,p.show_uuid,to_char(p.show_date,'YYYY-MM-DD') AS date,
                        p.display_date,p.track_count,p.duration
                    FROM collection_playable_sources p WHERE p.collection_uuid=@collectionUuid
                        AND (@artistUuid IS NULL OR p.artist_uuid=@artistUuid)
                        AND EXISTS(SELECT 1 FROM collections c WHERE c.uuid=@collectionUuid AND c.published)
                        AND (@afterUuid IS NULL OR p.show_date<CAST(@sort AS date)
                            OR (p.show_date=CAST(@sort AS date) AND p.source_uuid>@afterUuid))
                    ORDER BY p.show_date DESC,p.source_uuid LIMIT @take
                    """,
                    new
                    {
                        collectionUuid,
                        artistUuid,
                        afterUuid = after?.Uuid,
                        sort = after?.Sort,
                        take = limit + 1,
                    }
                )
            )
        ).ToArray();
        return Page(
            rows,
            limit,
            row => NewCursor("recordings", collectionUuid, row.source_uuid, row.date, artistUuid)
        );
    }

    /// <summary>Inspects retained artist-wide membership even after its catalog artist disappears.</summary>
    public Task<CollectionMembershipStatus?> GetArtistMembershipAsync(
        Guid collectionUuid,
        Guid artistUuid
    ) =>
        db.WithWriteConnection(connection =>
            connection.QuerySingleOrDefaultAsync<CollectionMembershipStatus>(
                """
                SELECT m.collection_uuid,'artist' AS basis,m.artist_uuid,m.active,m.reason,
                    EXISTS(SELECT 1 FROM artists a WHERE a.uuid=m.artist_uuid) AS reference_available
                FROM normalization.collection_artist_memberships m WHERE collection_uuid=@collectionUuid AND artist_uuid=@artistUuid
                """,
                new { collectionUuid, artistUuid }
            )
        );

    /// <summary>Inspects a direct recording inclusion, retaining its UUID after source deletion.</summary>
    public Task<CollectionMembershipStatus?> GetEditorialMembershipAsync(
        Guid collectionUuid,
        Guid sourceUuid
    ) =>
        db.WithWriteConnection(connection =>
            connection.QuerySingleOrDefaultAsync<CollectionMembershipStatus>(
                """
                SELECT m.collection_uuid,m.basis,m.source_uuid,m.active,m.reason,
                    EXISTS(SELECT 1 FROM sources s WHERE s.uuid=m.source_uuid) AS reference_available
                FROM normalization.collection_recording_memberships m
                WHERE collection_uuid=@collectionUuid AND basis='editorial' AND source_uuid=@sourceUuid
                """,
                new { collectionUuid, sourceUuid }
            )
        );

    /// <summary>Inspects an upstream inclusion even when its canonical source remains unresolved or unavailable.</summary>
    public Task<CollectionMembershipStatus?> GetProviderMembershipAsync(
        Guid selectorUuid,
        string itemIdentifier
    ) =>
        db.WithWriteConnection(connection =>
            connection.QuerySingleOrDefaultAsync<CollectionMembershipStatus>(
                """
                SELECT m.collection_uuid,m.basis,m.selector_uuid,m.item_identifier,m.active,m.reason,r.canonical_source_uuid AS source_uuid,
                    EXISTS(SELECT 1 FROM sources s JOIN artists a ON a.id=s.artist_id
                        WHERE s.uuid=r.canonical_source_uuid AND a.uuid=r.selected_artist_uuid
                            AND s.upstream_identifier=r.item_identifier AND EXISTS (
                                SELECT 1 FROM links l JOIN upstream_sources provider ON provider.id=l.upstream_source_id
                                WHERE l.source_id=s.id AND l.for_source AND provider.name='archive.org'
                            )) AS reference_available
                FROM normalization.collection_recording_memberships m
                JOIN normalization.archive_item_resolutions r ON r.item_identifier=m.item_identifier
                WHERE m.selector_uuid=@selectorUuid AND m.item_identifier=@itemIdentifier
                """,
                new { selectorUuid, itemIdentifier }
            )
        );

    private Task<bool> IsPublishedAsync(Guid collectionUuid) =>
        db.WithConnection(connection =>
            connection.QuerySingleAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM collections WHERE uuid=@collectionUuid AND published)",
                new { collectionUuid }
            )
        );
}
