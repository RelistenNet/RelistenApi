using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;

namespace Relisten.Data;

/// <summary>
/// Owns collection identity and independent inclusion bases. Membership never changes catalog
/// artists, sources, tracks, or ingestion ownership. Reads use the current catalog without Redis.
/// </summary>
public sealed partial class CollectionService(DbService db)
{
    /// <summary>Creates or edits a collection. Its UUID and slug remain stable; UUID-shaped slugs are reserved for UUID lookup.</summary>
    public async Task<RelistenCollection> SaveCollectionAsync(CollectionDefinition definition)
    {
        RequireUuid(definition.Uuid, nameof(definition.Uuid));
        RequireText(definition.Slug, nameof(definition.Slug));
        RequireText(definition.Name, nameof(definition.Name));
        if (Guid.TryParse(definition.Slug, out _))
        {
            throw new ArgumentException(
                "A collection slug cannot be a UUID.",
                nameof(definition.Slug)
            );
        }
        var saved = await db.WithWriteConnection(connection =>
            connection.QuerySingleOrDefaultAsync<RelistenCollection>(
                """
                INSERT INTO collections(uuid,slug,name,description,published) VALUES(@Uuid,@Slug,@Name,@Description,@Published)
                ON CONFLICT(uuid) DO UPDATE SET name=EXCLUDED.name,description=EXCLUDED.description,
                    published=EXCLUDED.published,updated_at=now() WHERE collections.slug=EXCLUDED.slug
                RETURNING *
                """,
                definition
            )
        );
        if (saved == null)
        {
            throw new ArgumentException("A collection's slug cannot change after creation.");
        }

        return await db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<RelistenCollection>(
                CollectionWithCounts + " WHERE c.uuid=@uuid",
                new { uuid = saved.uuid }
            )
        );
    }

    /// <summary>Registers a discovery selector without modifying artist-to-provider mappings. Existing selector identity is fixed.</summary>
    public async Task<CollectionSelector> SaveSelectorAsync(CollectionSelectorDefinition definition)
    {
        RequireUuid(definition.Uuid, nameof(definition.Uuid));
        RequireUuid(definition.CollectionUuid, nameof(definition.CollectionUuid));
        RequireText(definition.UpstreamIdentifier, nameof(definition.UpstreamIdentifier));
        var saved = await db.WithWriteConnection(connection =>
            connection.QuerySingleOrDefaultAsync<CollectionSelector>(
                """
                INSERT INTO normalization.collection_selectors(uuid,collection_uuid,upstream_source_id,upstream_identifier,enabled)
                VALUES(@Uuid,@CollectionUuid,@UpstreamSourceId,@UpstreamIdentifier,@Enabled)
                ON CONFLICT(uuid) DO UPDATE SET enabled=EXCLUDED.enabled,updated_at=now()
                WHERE collection_selectors.collection_uuid=EXCLUDED.collection_uuid
                    AND collection_selectors.upstream_source_id=EXCLUDED.upstream_source_id
                    AND collection_selectors.upstream_identifier=EXCLUDED.upstream_identifier
                RETURNING *
                """,
                definition
            )
        );
        return saved
            ?? throw new ArgumentException(
                "A selector's collection and provider identity cannot change."
            );
    }

    /// <summary>Returns operator selector state, including disabled selectors and unpublished collections.</summary>
    public Task<CollectionSelector?> GetSelectorAsync(Guid selectorUuid) =>
        db.WithWriteConnection(connection =>
            connection.QuerySingleOrDefaultAsync<CollectionSelector>(
                "SELECT * FROM normalization.collection_selectors WHERE uuid=@selectorUuid",
                new { selectorUuid }
            )
        );

    /// <summary>Includes the artist's present and later recordings, or retires that inclusion without affecting selected recordings.</summary>
    public Task SetArtistMembershipAsync(
        Guid collectionUuid,
        Guid artistUuid,
        bool active,
        string reason
    ) =>
        ChangeMembershipAsync(
            reason,
            async () =>
            {
                await RequireCollectionAsync(collectionUuid);
                var known = await db.WithWriteConnection(connection =>
                    connection.QuerySingleAsync<bool>(
                        """
                        SELECT EXISTS(SELECT 1 FROM artists WHERE uuid=@artistUuid) OR EXISTS(
                            SELECT 1 FROM normalization.collection_artist_memberships
                            WHERE collection_uuid=@collectionUuid AND artist_uuid=@artistUuid)
                        """,
                        new { collectionUuid, artistUuid }
                    )
                );
                if (!known)
                {
                    throw new ArgumentException(
                        "The artist does not exist in the catalog or retained membership."
                    );
                }
                await db.WithWriteConnection(connection =>
                    connection.ExecuteAsync(
                        """
                        INSERT INTO normalization.collection_artist_memberships(collection_uuid,artist_uuid,active,review_basis,reason,retired_at)
                        VALUES(@collectionUuid,@artistUuid,@active,@reason,@reason,CASE WHEN @active THEN NULL ELSE now() END)
                        ON CONFLICT(collection_uuid,artist_uuid) DO UPDATE SET active=EXCLUDED.active,reason=EXCLUDED.reason,
                            review_basis=CASE WHEN EXCLUDED.active THEN EXCLUDED.review_basis ELSE collection_artist_memberships.review_basis END,
                            retired_at=EXCLUDED.retired_at,updated_at=now()
                        """,
                        new
                        {
                            collectionUuid,
                            artistUuid,
                            active,
                            reason,
                        }
                    )
                );
            }
        );

    /// <summary>Selects one canonical source directly. Retiring this editorial basis leaves provider and artist-wide inclusion intact.</summary>
    public Task SetEditorialRecordingMembershipAsync(
        Guid collectionUuid,
        Guid sourceUuid,
        bool active,
        string reason
    ) =>
        ChangeMembershipAsync(
            reason,
            async () =>
            {
                await RequireCollectionAsync(collectionUuid);
                var known = await db.WithWriteConnection(connection =>
                    connection.QuerySingleAsync<bool>(
                        """
                        SELECT EXISTS(SELECT 1 FROM sources WHERE uuid=@sourceUuid) OR EXISTS(
                            SELECT 1 FROM normalization.collection_recording_memberships
                            WHERE collection_uuid=@collectionUuid AND basis='editorial' AND source_uuid=@sourceUuid)
                        """,
                        new { collectionUuid, sourceUuid }
                    )
                );
                if (!known)
                {
                    throw new ArgumentException(
                        "The source does not exist in the catalog or retained membership."
                    );
                }
                await db.WithWriteConnection(connection =>
                    connection.ExecuteAsync(
                        """
                        INSERT INTO normalization.collection_recording_memberships(collection_uuid,basis,source_uuid,active,review_basis,reason,retired_at)
                        VALUES(@collectionUuid,'editorial',@sourceUuid,@active,@reason,@reason,CASE WHEN @active THEN NULL ELSE now() END)
                        ON CONFLICT(collection_uuid,source_uuid) WHERE basis='editorial' DO UPDATE SET active=EXCLUDED.active,reason=EXCLUDED.reason,
                            review_basis=CASE WHEN EXCLUDED.active THEN EXCLUDED.review_basis ELSE collection_recording_memberships.review_basis END,
                            retired_at=EXCLUDED.retired_at,updated_at=now()
                        """,
                        new
                        {
                            collectionUuid,
                            sourceUuid,
                            active,
                            reason,
                        }
                    )
                );
            }
        );

    /// <summary>Retains an Archive inclusion even before the item's performer or canonical source is known.</summary>
    public Task SetProviderMembershipAsync(
        Guid selectorUuid,
        string itemIdentifier,
        bool active,
        string reason,
        Guid? scanId = null
    ) =>
        ChangeMembershipAsync(
            reason,
            async () =>
            {
                RequireText(itemIdentifier, nameof(itemIdentifier));
                var selector =
                    await GetSelectorAsync(selectorUuid)
                    ?? throw new ArgumentException("The selector does not exist.");
                if (
                    !await db.WithWriteConnection(connection =>
                        connection.QuerySingleAsync<bool>(
                            "SELECT EXISTS(SELECT 1 FROM upstream_sources WHERE id=@providerId AND name='archive.org')",
                            new { providerId = selector.upstream_source_id }
                        )
                    )
                )
                {
                    throw new ArgumentException(
                        "Provider-backed membership currently supports Archive.org selectors only."
                    );
                }
                await db.WithWriteConnection(connection =>
                    connection.ExecuteAsync(
                        """
                        INSERT INTO normalization.archive_item_resolutions(item_identifier) VALUES(@itemIdentifier) ON CONFLICT DO NOTHING;
                        INSERT INTO normalization.collection_recording_memberships
                            (collection_uuid,basis,selector_uuid,item_identifier,active,review_basis,reason,last_seen_scan_id,retired_at)
                        VALUES(@collectionUuid,'provider',@selectorUuid,@itemIdentifier,@active,@reason,@reason,@scanId,
                            CASE WHEN @active THEN NULL ELSE now() END)
                        ON CONFLICT(selector_uuid,item_identifier) WHERE basis='provider' DO UPDATE SET active=EXCLUDED.active,reason=EXCLUDED.reason,
                            last_seen_scan_id=COALESCE(EXCLUDED.last_seen_scan_id,collection_recording_memberships.last_seen_scan_id),
                            retired_at=EXCLUDED.retired_at,updated_at=now()
                        """,
                        new
                        {
                            collectionUuid = selector.collection_uuid,
                            selectorUuid,
                            itemIdentifier,
                            active,
                            reason,
                            scanId,
                        }
                    )
                );
            }
        );

    private async Task ChangeMembershipAsync(string reason, Func<Task> apply)
    {
        RequireText(reason, nameof(reason));
        await new CatalogImportTransaction(db).RunAsync(
            DateTimeOffset.UtcNow.AddMinutes(5),
            CancellationToken.None,
            async () =>
            {
                await apply();
                return true;
            }
        );
    }

    private async Task RequireCollectionAsync(Guid collectionUuid)
    {
        RequireUuid(collectionUuid, nameof(collectionUuid));
        if (
            !await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM collections WHERE uuid=@collectionUuid)",
                    new { collectionUuid }
                )
            )
        )
        {
            throw new ArgumentException("The collection does not exist.");
        }
    }

    private static void RequireUuid(Guid value, string name)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("UUID must not be empty.", name);
        }
    }

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A nonempty value is required.", name);
        }
    }
}
