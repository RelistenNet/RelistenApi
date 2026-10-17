using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Ingestion.Providers.Archive;
using Relisten.Ingestion.Providers.ArchiveCollections;

namespace Relisten.Data;

/// <summary>
/// Owns durable collection discovery progress and provider inclusion evidence. A page, its
/// memberships and its cursor commit together. Discovery never changes canonical recordings,
/// artist assignments or the importer that owns an existing source.
/// </summary>
public sealed class CollectionDiscoveryService(DbService db, CatalogImportTransaction transactions)
{
    /// <summary>Resumes incomplete work, or begins another scan after a completed traversal.</summary>
    public Task<ArchiveCollectionDiscoveryCheckpoint> BeginOrResumeAsync(
        Guid selectorUuid,
        DateTimeOffset deadline,
        CancellationToken token = default
    ) =>
        transactions.RunAsync(
            deadline,
            token,
            async () =>
            {
                var selector = await LockSelectorAsync(selectorUuid);
                if (selector.enabled && selector.scan_state != "running")
                {
                    await db.WithWriteConnection(connection =>
                        connection.ExecuteAsync(
                            """
                            UPDATE normalization.collection_selectors SET scan_id=@scanId,scan_state='running',
                                cursor=NULL,seen_cursors='{}',pending_items='{}',discovered_count=0,
                                search_complete=false,last_error=NULL,completed_at=NULL,updated_at=now()
                            WHERE uuid=@selectorUuid
                            """,
                            new { selectorUuid, scanId = Guid.NewGuid() }
                        )
                    );
                    selector = await LockSelectorAsync(selectorUuid);
                }

                return Checkpoint(selector);
            }
        );

    /// <summary>
    /// Persists every distinct page identifier before advancing. Retried calls with a stale
    /// scan/cursor return current progress, including when the previous commit lost its reply.
    /// </summary>
    public Task<ArchiveCollectionDiscoveryCheckpoint> ApplyPageAsync(
        ArchiveCollectionDiscoveryCheckpoint expected,
        ArchiveCollectionSearchPage page,
        DateTimeOffset deadline,
        CancellationToken token = default
    ) =>
        transactions.RunAsync(
            deadline,
            token,
            async () =>
            {
                var selector = await LockSelectorAsync(expected.SelectorUuid);
                if (
                    !selector.enabled
                    || selector.scan_id != expected.ScanId
                    || selector.scan_state != "running"
                    || selector.cursor != expected.Cursor
                    || selector.search_complete
                    || selector.pending_items.Length > 0
                )
                {
                    return Checkpoint(selector);
                }

                if (page.Cursor != null && selector.seen_cursors.Contains(page.Cursor))
                {
                    throw new InvalidDataException(
                        "Archive collection discovery repeated a search cursor."
                    );
                }

                var identifiers = page
                    .Items.Select(item => item.Value)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var alreadySeen = (
                    await db.WithWriteConnection(connection =>
                        connection.QueryAsync<string>(
                            """
                            SELECT item_identifier FROM normalization.collection_recording_memberships
                            WHERE selector_uuid=@selectorUuid AND last_seen_scan_id=@scanId
                                AND item_identifier=ANY(@identifiers)
                            """,
                            new
                            {
                                selectorUuid = expected.SelectorUuid,
                                scanId = expected.ScanId,
                                identifiers,
                            }
                        )
                    )
                ).ToHashSet(StringComparer.Ordinal);
                var fresh = identifiers.Where(item => !alreadySeen.Contains(item)).ToArray();
                if (identifiers.Length > 0 && fresh.Length == 0)
                {
                    throw new InvalidDataException(
                        "Archive collection discovery returned no new identifiers."
                    );
                }

                // One set-based write handles the observed 5,000-item continuation pages.
                // Provider membership is independent of editorial and artist-wide bases.
                await db.WithWriteConnection(connection =>
                    connection.ExecuteAsync(
                        """
                        INSERT INTO normalization.archive_item_resolutions(item_identifier)
                        SELECT unnest(@identifiers::text[]) ON CONFLICT DO NOTHING;
                        INSERT INTO normalization.collection_recording_memberships
                            (collection_uuid,basis,selector_uuid,item_identifier,active,review_basis,reason,last_seen_scan_id)
                        SELECT @collectionUuid,'provider',@selectorUuid,item,true,
                            'archive_collection_discovery','Observed in the configured Archive audio collection.',@scanId
                        FROM unnest(@identifiers::text[]) AS item
                        ON CONFLICT(selector_uuid,item_identifier) WHERE basis='provider' DO UPDATE SET
                            last_seen_scan_id=EXCLUDED.last_seen_scan_id,updated_at=now();
                        UPDATE normalization.collection_selectors SET cursor=@cursor,
                            seen_cursors=CASE WHEN @cursor IS NULL THEN seen_cursors ELSE array_append(seen_cursors,@cursor) END,
                            pending_items=@fresh,discovered_count=discovered_count+@count,
                            search_complete=@complete,last_error=NULL,updated_at=now()
                        WHERE uuid=@selectorUuid;
                        """,
                        new
                        {
                            collectionUuid = selector.collection_uuid,
                            selectorUuid = expected.SelectorUuid,
                            scanId = expected.ScanId,
                            identifiers,
                            fresh,
                            count = fresh.Length,
                            cursor = page.Cursor,
                            complete = page.Cursor == null,
                        }
                    )
                );
                return Checkpoint(await LockSelectorAsync(expected.SelectorUuid));
            }
        );

    /// <summary>Removes only the expected settled prefix. Lost acknowledgements cannot consume a later batch.</summary>
    public Task<ArchiveCollectionDiscoveryCheckpoint> AcknowledgeItemsAsync(
        ArchiveCollectionDiscoveryCheckpoint expected,
        string[] settledItems,
        DateTimeOffset deadline,
        CancellationToken token = default
    ) =>
        transactions.RunAsync(
            deadline,
            token,
            async () =>
            {
                var selector = await LockSelectorAsync(expected.SelectorUuid);
                if (
                    selector.scan_id == expected.ScanId
                    && selector.scan_state == "running"
                    && settledItems.Length > 0
                    && selector.pending_items.Take(settledItems.Length).SequenceEqual(settledItems)
                )
                {
                    await db.WithWriteConnection(connection =>
                        connection.ExecuteAsync(
                            "UPDATE normalization.collection_selectors SET pending_items=@remaining,updated_at=now() WHERE uuid=@selectorUuid",
                            new
                            {
                                selectorUuid = expected.SelectorUuid,
                                remaining = selector
                                    .pending_items.Skip(settledItems.Length)
                                    .ToArray(),
                            }
                        )
                    );
                }
                return Checkpoint(await LockSelectorAsync(expected.SelectorUuid));
            }
        );

    /// <summary>Completes only terminal, settled scans. Absence never retires membership or deletes recordings.</summary>
    public Task<ArchiveCollectionDiscoveryCheckpoint> CompleteAsync(
        ArchiveCollectionDiscoveryCheckpoint expected,
        DateTimeOffset deadline,
        CancellationToken token = default
    ) =>
        transactions.RunAsync(
            deadline,
            token,
            async () =>
            {
                var selector = await LockSelectorAsync(expected.SelectorUuid);
                if (
                    selector.scan_id == expected.ScanId
                    && selector.scan_state == "running"
                    && selector.search_complete
                    && selector.pending_items.Length == 0
                )
                {
                    await db.WithWriteConnection(connection =>
                        connection.ExecuteAsync(
                            "UPDATE normalization.collection_selectors SET scan_state='complete',completed_at=now(),last_error=NULL,updated_at=now() WHERE uuid=@selectorUuid",
                            new { selectorUuid = expected.SelectorUuid }
                        )
                    );
                }
                return Checkpoint(await LockSelectorAsync(expected.SelectorUuid));
            }
        );

    /// <summary>Retains a failed traversal's diagnostic without discarding its cursor or pending identifiers.</summary>
    public Task RecordScanFailureAsync(
        ArchiveCollectionDiscoveryCheckpoint checkpoint,
        string error,
        CancellationToken token = default
    )
    {
        token.ThrowIfCancellationRequested();
        return db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                UPDATE normalization.collection_selectors SET last_error=@error,updated_at=now()
                WHERE uuid=@selectorUuid AND scan_id=@scanId AND scan_state='running'
                """,
                new
                {
                    selectorUuid = checkpoint.SelectorUuid,
                    scanId = checkpoint.ScanId,
                    error = error.Length <= 1000 ? error : error[..1000],
                }
            )
        );
    }

    private async Task<SelectorRow> LockSelectorAsync(Guid selectorUuid)
    {
        var row =
            await db.WithWriteConnection(connection =>
                connection.QuerySingleOrDefaultAsync<SelectorRow>(
                    """
                    SELECT s.*,p.name AS provider_name FROM normalization.collection_selectors s
                    JOIN upstream_sources p ON p.id=s.upstream_source_id WHERE s.uuid=@selectorUuid FOR UPDATE OF s
                    """,
                    new { selectorUuid }
                )
            )
            ?? throw new ArgumentException(
                "The collection selector does not exist.",
                nameof(selectorUuid)
            );
        if (row.provider_name != "archive.org")
        {
            throw new ArgumentException(
                "Collection discovery currently supports Archive.org selectors only."
            );
        }
        return row;
    }

    private static ArchiveCollectionDiscoveryCheckpoint Checkpoint(SelectorRow selector) =>
        new(
            selector.uuid,
            selector.scan_id ?? Guid.Empty,
            selector.upstream_identifier,
            selector.enabled,
            selector.cursor,
            selector.pending_items,
            selector.discovered_count,
            selector.search_complete,
            selector.scan_state == "complete"
        );

    private sealed class SelectorRow
    {
        public Guid uuid { get; set; }
        public Guid collection_uuid { get; set; }
        public string upstream_identifier { get; set; } = "";
        public string provider_name { get; set; } = "";
        public bool enabled { get; set; }
        public Guid? scan_id { get; set; }
        public string scan_state { get; set; } = "";
        public string? cursor { get; set; }
        public string[] seen_cursors { get; set; } = [];
        public string[] pending_items { get; set; } = [];
        public long discovered_count { get; set; }
        public bool search_complete { get; set; }
    }
}
