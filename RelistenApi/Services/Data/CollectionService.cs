using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;
using Relisten.Import;
using Relisten.Services.Collections;
using Relisten.Vendor.ArchiveOrg;

namespace Relisten.Data;

public class CollectionService : RelistenDataServiceBase, IArchiveCollectionResolverRepository,
    IAadamJacobsCollectionRepository
{
    private readonly ArtistService artistService;
    private readonly UpstreamSourceService upstreamSourceService;

    public CollectionService(DbService db, ArtistService artistService, UpstreamSourceService upstreamSourceService) :
        base(db)
    {
        this.artistService = artistService;
        this.upstreamSourceService = upstreamSourceService;
    }

    public async Task<ArchiveCollection> EnsureAadamJacobsCollection()
    {
        var upstreamSource = await upstreamSourceService.FindUpstreamSourceByName(ArchiveOrgImporter.DataSourceName);
        if (upstreamSource == null)
        {
            throw new InvalidOperationException("archive.org upstream source was not found.");
        }

        return await db.WithWriteConnection(con => con.QuerySingleAsync<ArchiveCollection>(@"
            INSERT INTO collections
                (
                    uuid,
                    slug,
                    upstream_source_id,
                    upstream_identifier,
                    collection_type,
                    name,
                    description
                )
            VALUES
                (
                    md5('root::collection::aadamjacobs')::uuid,
                    'aadam-jacobs',
                    @upstreamSourceId,
                    'aadamjacobs',
                    'taper_archive',
                    'Aadam Jacobs Collection',
                    'A curated Archive.org collection imported into playable Relisten sources.'
                )
            ON CONFLICT (slug) DO UPDATE SET
                upstream_source_id = EXCLUDED.upstream_source_id,
                upstream_identifier = EXCLUDED.upstream_identifier,
                collection_type = EXCLUDED.collection_type,
                name = EXCLUDED.name,
                description = EXCLUDED.description,
                updated_at = timezone('utc'::text, now())
            RETURNING *
        ", new {upstreamSourceId = upstreamSource.id}));
    }

    public Task<DateTime> GetServerTimestamp()
    {
        return db.WithConnection(con => con.QuerySingleAsync<DateTime>(@"
            SELECT timezone('utc'::text, now())
        "));
    }

    public Task UpsertIndexedItem(ArchiveCollection collection, ArchiveOrgCollectionIndexItem item,
        DateTime indexRunTimestamp)
    {
        return db.WithWriteConnection(con => con.ExecuteAsync(@"
            INSERT INTO collection_items
                (
                    collection_uuid,
                    upstream_identifier,
                    title,
                    creator_raw,
                    date_raw,
                    display_date,
                    year,
                    import_status,
                    last_seen_at,
                    updated_at
                )
            VALUES
                (
                    @collectionUuid,
                    @identifier,
                    @title,
                    @creator,
                    @date,
                    @date,
                    @year,
                    @pendingStatus,
                    @indexRunTimestamp,
                    timezone('utc'::text, now())
                )
            ON CONFLICT (collection_uuid, upstream_identifier) DO UPDATE SET
                title = EXCLUDED.title,
                creator_raw = EXCLUDED.creator_raw,
                date_raw = EXCLUDED.date_raw,
                display_date = EXCLUDED.display_date,
                year = EXCLUDED.year,
                last_seen_at = EXCLUDED.last_seen_at,
                removed_at = NULL,
                import_status = CASE
                    WHEN collection_items.source_uuid IS NULL THEN @pendingStatus
                    ELSE collection_items.import_status
                END,
                import_error = CASE
                    WHEN collection_items.source_uuid IS NULL THEN NULL
                    ELSE collection_items.import_error
                END,
                updated_at = EXCLUDED.updated_at
        ", new
        {
            collectionUuid = collection.uuid,
            item.identifier,
            title = item.title ?? item.identifier,
            creator = item.creator,
            date = item.date,
            item.year,
            pendingStatus = (int)ArchiveCollectionItemImportStatus.Pending,
            indexRunTimestamp
        }));
    }

    public Task<int> MarkMissingItemsRemoved(ArchiveCollection collection, DateTime indexRunTimestamp)
    {
        return db.WithWriteConnection(con => con.ExecuteAsync(@"
            UPDATE collection_items
            SET
                removed_at = @indexRunTimestamp,
                updated_at = timezone('utc'::text, now())
            WHERE collection_uuid = @collectionUuid
              AND removed_at IS NULL
              AND last_seen_at <> @indexRunTimestamp
        ", new {collectionUuid = collection.uuid, indexRunTimestamp}));
    }

    public Task CompleteIndexRun(ArchiveCollection collection, DateTime indexRunTimestamp, int activeItemCount)
    {
        return db.WithWriteConnection(con => con.ExecuteAsync(@"
            UPDATE collections
            SET
                item_count = @activeItemCount,
                indexed_at = @indexRunTimestamp,
                updated_at = timezone('utc'::text, now())
            WHERE uuid = @collectionUuid
        ", new {collectionUuid = collection.uuid, indexRunTimestamp, activeItemCount}));
    }

    public async Task<IReadOnlyList<CollectionItem>> LoadActivePendingItems(ArchiveCollection collection)
    {
        var items = await db.WithConnection(con => con.QueryAsync<CollectionItem>(@"
            SELECT *
            FROM collection_items
            WHERE collection_uuid = @collectionUuid
              AND removed_at IS NULL
              AND NOT (
                  import_status IN (@linkedStatus, @importedStatus)
                  AND artist_uuid IS NOT NULL
                  AND source_uuid IS NOT NULL
                  AND show_uuid IS NOT NULL
              )
            ORDER BY upstream_identifier
        ", new
        {
            collectionUuid = collection.uuid,
            linkedStatus = (int)ArchiveCollectionItemImportStatus.LinkedExistingSource,
            importedStatus = (int)ArchiveCollectionItemImportStatus.ImportedSource
        }));

        return items.ToList();
    }

    public Task<Guid?> FindExistingSourceUuid(int artistId, string upstreamIdentifier)
    {
        return db.WithConnection(con => con.QuerySingleOrDefaultAsync<Guid?>(@"
            SELECT uuid
            FROM sources
            WHERE artist_id = @artistId
              AND upstream_identifier = @upstreamIdentifier
            LIMIT 1
        ", new {artistId, upstreamIdentifier}));
    }

    public Task MarkItemLinkedToSource(ArchiveCollection collection, string upstreamIdentifier, Artist artist,
        Guid sourceUuid, ArchiveCollectionItemImportStatus status)
    {
        return db.WithWriteConnection(con => con.ExecuteAsync(@"
            UPDATE collection_items
            SET
                artist_uuid = @artistUuid,
                source_uuid = @sourceUuid,
                import_status = @status,
                import_error = NULL,
                last_imported_at = timezone('utc'::text, now()),
                updated_at = timezone('utc'::text, now())
            WHERE collection_uuid = @collectionUuid
              AND upstream_identifier = @upstreamIdentifier
        ", new
        {
            collectionUuid = collection.uuid,
            upstreamIdentifier,
            artistUuid = artist.uuid,
            sourceUuid,
            status = (int)status
        }));
    }

    public Task MarkItemSkipped(ArchiveCollection collection, string upstreamIdentifier, string reason)
    {
        return UpdateItemStatus(collection, upstreamIdentifier, ArchiveCollectionItemImportStatus.Skipped, reason);
    }

    public Task MarkItemImportError(ArchiveCollection collection, string upstreamIdentifier, string error)
    {
        return UpdateItemStatus(collection, upstreamIdentifier, ArchiveCollectionItemImportStatus.ImportError, error);
    }

    public Task WithArtistImportLock(int artistId, Func<Task> action)
    {
        return db.WithAdvisoryLock(DbService.ArtistImportLockKey(artistId), action);
    }

    private Task UpdateItemStatus(ArchiveCollection collection, string upstreamIdentifier,
        ArchiveCollectionItemImportStatus status, string message)
    {
        return db.WithWriteConnection(con => con.ExecuteAsync(@"
            UPDATE collection_items
            SET
                import_status = @status,
                import_error = @message,
                updated_at = timezone('utc'::text, now())
            WHERE collection_uuid = @collectionUuid
              AND upstream_identifier = @upstreamIdentifier
        ", new
        {
            collectionUuid = collection.uuid,
            upstreamIdentifier,
            status = (int)status,
            message
        }));
    }

    public async Task LinkImportedItemsForArtist(ArchiveCollection collection, int artistId)
    {
        await db.WithWriteConnection(con => con.ExecuteAsync(@"
            UPDATE collection_items ci
            SET
                artist_uuid = a.uuid,
                source_uuid = s.uuid,
                show_uuid = sh.uuid,
                last_imported_at = COALESCE(ci.last_imported_at, timezone('utc'::text, now())),
                updated_at = timezone('utc'::text, now())
            FROM sources s
            JOIN artists a ON a.id = s.artist_id
            LEFT JOIN shows sh ON sh.id = s.show_id
            WHERE ci.collection_uuid = @collectionUuid
              AND ci.removed_at IS NULL
              AND ci.upstream_identifier = s.upstream_identifier
              AND s.artist_id = @artistId
        ", new {collectionUuid = collection.uuid, artistId}));

        await artistService.TouchApiUpdatedAt(artistId);
    }

    public Task RecomputeCollectionYears(ArchiveCollection collection)
    {
        return db.WithWriteConnection(con => con.ExecuteAsync(@"
            DELETE FROM collection_years
            WHERE collection_uuid = @collectionUuid;

            INSERT INTO collection_years
                (
                    collection_uuid,
                    uuid,
                    year,
                    artist_count,
                    show_count,
                    source_count,
                    duration,
                    avg_duration,
                    avg_rating,
                    updated_at
                )
            SELECT
                ci.collection_uuid,
                md5(ci.collection_uuid::text || '::collection_year::' || COALESCE(ci.year::text, to_char(sh.date, 'YYYY')))::uuid,
                COALESCE(ci.year::text, to_char(sh.date, 'YYYY')) AS year,
                COUNT(DISTINCT ci.artist_uuid) AS artist_count,
                COUNT(DISTINCT ci.show_uuid) AS show_count,
                COUNT(DISTINCT ci.source_uuid) AS source_count,
                COALESCE(SUM(s.duration), 0)::bigint AS duration,
                AVG(sh.avg_duration) AS avg_duration,
                AVG(sh.avg_rating) AS avg_rating,
                timezone('utc'::text, now()) AS updated_at
            FROM collection_items ci
            JOIN sources s ON s.uuid = ci.source_uuid
            JOIN shows sh ON sh.uuid = ci.show_uuid
            WHERE ci.collection_uuid = @collectionUuid
              AND ci.removed_at IS NULL
              AND ci.source_uuid IS NOT NULL
            GROUP BY ci.collection_uuid, COALESCE(ci.year::text, to_char(sh.date, 'YYYY'))
        ", new {collectionUuid = collection.uuid}));
    }

    public Task CompleteImportRun(ArchiveCollection collection, DateTime importRunTimestamp)
    {
        return db.WithWriteConnection(con => con.ExecuteAsync(@"
            UPDATE collections
            SET
                last_imported_at = @importRunTimestamp,
                updated_at = timezone('utc'::text, now())
            WHERE uuid = @collectionUuid
        ", new {collectionUuid = collection.uuid, importRunTimestamp}));
    }

    public Task<CollectionArtistMapping?> FindMapping(Guid collectionUuid, string creatorName)
    {
        return db.WithConnection(con => con.QuerySingleOrDefaultAsync<CollectionArtistMapping>(@"
            SELECT *
            FROM collection_artist_mappings
            WHERE collection_uuid = @collectionUuid
              AND creator_name = @creatorName
        ", new {collectionUuid, creatorName}));
    }

    public Task<Artist?> FindArtistByUuid(Guid artistUuid)
    {
        return artistService.FindArtistByUuid(artistUuid);
    }

    public async Task<IReadOnlyList<Artist>> FindArtistsBySourceIdentifier(string upstreamIdentifier)
    {
        var artists = await QueryArtists(@"
            JOIN sources s ON s.artist_id = a.id
            WHERE s.upstream_identifier = @upstreamIdentifier
        ", new {upstreamIdentifier});

        return artists.ToList();
    }

    public async Task<Artist?> FindArtistByExactName(string name)
    {
        var artists = await QueryArtists(@"
            WHERE a.name = @name
            LIMIT 1
        ", new {name});

        return artists.FirstOrDefault();
    }

    public async Task<Artist?> FindArtistByNormalizedName(string normalizedName)
    {
        var artists = await QueryArtists(@"
            WHERE regexp_replace(lower(a.name), '[^a-z0-9]+', '', 'g') = @normalizedName
            ORDER BY a.featured DESC, a.name
            LIMIT 1
        ", new {normalizedName});

        return artists.FirstOrDefault();
    }

    public async Task<IReadOnlyList<string>> LoadArtistSlugs()
    {
        var slugs = await db.WithConnection(con => con.QueryAsync<string>(@"
            SELECT slug
            FROM artists
        "));

        return slugs.ToList();
    }

    public Task<SlimArtistWithFeatures> SaveArtist(SlimArtistWithFeatures artist)
    {
        return artistService.Save(artist);
    }

    public Task SaveMapping(CollectionArtistMapping mapping)
    {
        return db.WithWriteConnection(con => con.ExecuteAsync(@"
            INSERT INTO collection_artist_mappings
                (
                    collection_uuid,
                    creator_name,
                    artist_uuid,
                    canonical_name,
                    blocked,
                    block_reason,
                    decision_source,
                    updated_at
                )
            VALUES
                (
                    @collection_uuid,
                    @creator_name,
                    @artist_uuid,
                    @canonical_name,
                    @blocked,
                    @block_reason,
                    @decision_source,
                    timezone('utc'::text, now())
                )
            ON CONFLICT (collection_uuid, creator_name) DO UPDATE SET
                artist_uuid = EXCLUDED.artist_uuid,
                canonical_name = EXCLUDED.canonical_name,
                blocked = EXCLUDED.blocked,
                block_reason = EXCLUDED.block_reason,
                decision_source = EXCLUDED.decision_source,
                updated_at = EXCLUDED.updated_at
        ", mapping));
    }

    public Task TouchApiUpdatedAt(int artistId)
    {
        return artistService.TouchApiUpdatedAt(artistId);
    }

    private async Task<IEnumerable<Artist>> QueryArtists(string fromAndWhere, object parameters)
    {
        var artists = await db.WithConnection(con => con.QueryAsync<Artist, Features, Artist>($@"
            SELECT DISTINCT
                a.*, f.*
            FROM artists a
            LEFT JOIN features f ON f.artist_id = a.id
            {fromAndWhere}
        ", (artist, features) =>
        {
            artist.features = features;
            artist.upstream_sources = Array.Empty<ArtistUpstreamSource>();
            return artist;
        }, parameters));

        return artists;
    }
}
