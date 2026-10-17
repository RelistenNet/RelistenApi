using System;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Owns immutable bounded selections and failure receipts. Creating a page does not start an
/// attempt or hold catalog/work locks after returning. Source and artist UUIDs remain historical
/// identifiers when catalog membership changes or rows disappear.
/// </summary>
public sealed partial class NormalizationRecordingPageStore(DbService db)
{
    /// <summary>
    /// Retains up to limit recordings in UUID order, with one lookahead for HasMoreAtSelection.
    /// An exact page UUID replay returns its original selection, including after artist deletion.
    /// Changed request fields conflict. An unknown artist on a new request throws ArgumentException;
    /// a known artist with no recordings produces a valid empty page. Limit is 1–100.
    /// </summary>
    public Task<NormalizationRecordingPage> GetOrCreateAsync(
        Guid pageUuid,
        Guid? artistUuid,
        Guid? afterSourceUuid,
        int limit,
        string processingVersion,
        CancellationToken ct = default
    )
    {
        RequireUuid(pageUuid, nameof(pageUuid));
        RequireOptionalUuid(artistUuid, nameof(artistUuid));
        RequireOptionalUuid(afterSourceUuid, nameof(afterSourceUuid));
        RequireLimit(limit);
        ArgumentException.ThrowIfNullOrWhiteSpace(processingVersion);

        return InTransaction(
            async (connection, transaction) =>
            {
                await LockReceipt(
                    connection,
                    transaction,
                    $"normalization:recording-page:{pageUuid:D}",
                    ct
                );
                var existing = await ReadPage(connection, transaction, pageUuid, ct);
                if (existing != null)
                {
                    if (
                        existing.ArtistUuid != artistUuid
                        || existing.AfterSourceUuid != afterSourceUuid
                        || existing.Limit != limit
                        || existing.ProcessingVersion != processingVersion
                    )
                    {
                        throw new NormalizationConflictException(
                            "Page UUID already belongs to a different recording selection request."
                        );
                    }
                    return existing;
                }

                var selected = await SelectRecordings(
                    connection,
                    transaction,
                    artistUuid,
                    afterSourceUuid,
                    limit,
                    ct
                );
                var items = selected
                    .SourceUuids.Take(limit)
                    .Select(
                        (source, position) =>
                            new NormalizationRecordingPageItem(
                                position,
                                source,
                                AttemptUuid(pageUuid, source)
                            )
                    )
                    .ToArray();
                var page = new NormalizationRecordingPage(
                    pageUuid,
                    artistUuid,
                    afterSourceUuid,
                    limit,
                    processingVersion,
                    selected.SelectedAt,
                    selected.SourceUuids.Length > limit,
                    items
                );
                await InsertPage(connection, transaction, page, ct);
                return page;
            },
            ct
        );
    }

    /// <summary>Reads a retained selection, or null for an unknown page UUID, without consulting the current catalog.</summary>
    public Task<NormalizationRecordingPage?> GetAsync(Guid pageUuid, CancellationToken ct = default)
    {
        RequireUuid(pageUuid, nameof(pageUuid));
        return db.WithWriteConnection(connection =>
            ReadPage((NpgsqlConnection)connection, null, pageUuid, ct)
        );
    }

    private static async Task<SelectionRow> SelectRecordings(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid? artistUuid,
        Guid? afterSourceUuid,
        int limit,
        CancellationToken ct
    )
    {
        // Existence and membership use one statement snapshot. Read-committed isolation lets a
        // creator waiting on the page lock see the committed winner before selecting anything.
        var predicates = new[]
        {
            artistUuid == null ? null : "s.artist_id=(SELECT id FROM requested_artist)",
            afterSourceUuid == null ? null : "s.uuid>@afterSourceUuid",
        }
            .Where(predicate => predicate != null)
            .ToArray();
        var where = predicates.Length == 0 ? "" : "WHERE " + string.Join(" AND ", predicates);
        var selected = await connection.QuerySingleAsync<SelectionRow>(
            new CommandDefinition(
                $"""
                WITH requested_artist AS (SELECT id FROM artists WHERE uuid=@artistUuid), selected AS MATERIALIZED (
                    SELECT s.uuid FROM sources s {where} ORDER BY s.uuid LIMIT @take
                )
                SELECT (@artistUuid::uuid IS NULL OR EXISTS(SELECT 1 FROM requested_artist)) AS "ArtistExists",
                    statement_timestamp() AS "SelectedAt",
                    ARRAY(SELECT uuid FROM selected ORDER BY uuid) AS "SourceUuids"
                """,
                new
                {
                    artistUuid,
                    afterSourceUuid,
                    take = limit + 1,
                },
                transaction,
                cancellationToken: ct
            )
        );
        if (!selected.ArtistExists)
        {
            throw new ArgumentException("Catalog artist was not found.", nameof(artistUuid));
        }
        return selected;
    }

    private static Task InsertPage(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizationRecordingPage page,
        CancellationToken ct
    ) =>
        connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO normalization.recording_processing_pages(page_uuid,artist_uuid,after_source_uuid,
                    selection_limit,processing_version,selected_at,has_more)
                VALUES(@PageUuid,@ArtistUuid,@AfterSourceUuid,@Limit,@ProcessingVersion,@SelectedAt,@HasMoreAtSelection);
                INSERT INTO normalization.recording_processing_page_items(page_uuid,position,source_uuid,attempt_uuid)
                SELECT @PageUuid,item.position,item.source_uuid,item.attempt_uuid
                FROM unnest(@positions::integer[],@sources::uuid[],@attempts::uuid[]) item(position,source_uuid,attempt_uuid)
                """,
                new
                {
                    page.PageUuid,
                    page.ArtistUuid,
                    page.AfterSourceUuid,
                    page.Limit,
                    page.ProcessingVersion,
                    page.SelectedAt,
                    page.HasMoreAtSelection,
                    positions = page.Items.Select(item => item.Position).ToArray(),
                    sources = page.Items.Select(item => item.SourceUuid).ToArray(),
                    attempts = page.Items.Select(item => item.AttemptUuid).ToArray(),
                },
                transaction,
                cancellationToken: ct
            )
        );

    private static async Task<NormalizationRecordingPage?> ReadPage(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid pageUuid,
        CancellationToken ct
    )
    {
        using var rows = await connection.QueryMultipleAsync(
            new CommandDefinition(
                """
                SELECT page_uuid AS "PageUuid",artist_uuid AS "ArtistUuid",after_source_uuid AS "AfterSourceUuid",
                    selection_limit AS "Limit",processing_version AS "ProcessingVersion",selected_at AS "SelectedAt",has_more AS "HasMore"
                FROM normalization.recording_processing_pages WHERE page_uuid=@pageUuid;
                SELECT position AS "Position",source_uuid AS "SourceUuid",attempt_uuid AS "AttemptUuid"
                FROM normalization.recording_processing_page_items WHERE page_uuid=@pageUuid ORDER BY position;
                """,
                new { pageUuid },
                transaction,
                cancellationToken: ct
            )
        );
        var header = await rows.ReadSingleOrDefaultAsync<PageRow>();
        var items = (await rows.ReadAsync<NormalizationRecordingPageItem>()).ToArray();
        return header == null
            ? null
            : new(
                header.PageUuid,
                header.ArtistUuid,
                header.AfterSourceUuid,
                header.Limit,
                header.ProcessingVersion,
                header.SelectedAt,
                header.HasMore,
                items
            );
    }

    private static Guid AttemptUuid(Guid pageUuid, Guid sourceUuid)
    {
        // This namespace version is durable identity policy. Changing it cannot change already
        // planned attempts; a new processing version also requires a new page UUID.
        var hash = SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    Version = "recording-page-attempt-v1",
                    pageUuid,
                    sourceUuid,
                }
            )
        );
        hash[6] = (byte)((hash[6] & 0x0f) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    private Task<T> InTransaction<T>(
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> action,
        CancellationToken ct
    ) =>
        db.WithWriteConnection(async connection =>
        {
            ct.ThrowIfCancellationRequested();
            await using var transaction = await (
                (NpgsqlConnection)connection
            ).BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var result = await action((NpgsqlConnection)connection, transaction);
            await transaction.CommitAsync(ct);
            return result;
        });

    private static Task LockReceipt(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string key,
        CancellationToken ct
    ) =>
        connection.ExecuteAsync(
            new CommandDefinition(
                "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
                new { key },
                transaction,
                cancellationToken: ct
            )
        );

    private static void RequireUuid(Guid uuid, string parameter)
    {
        if (uuid == Guid.Empty)
        {
            throw new ArgumentException("A nonempty UUID is required.", parameter);
        }
    }

    private static void RequireOptionalUuid(Guid? uuid, string parameter)
    {
        if (uuid.HasValue)
        {
            RequireUuid(uuid.Value, parameter);
        }
    }

    private static void RequireLimit(int limit)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Use a page size from 1 to 100.");
        }
    }

    private sealed class SelectionRow
    {
        public bool ArtistExists { get; set; }
        public DateTime SelectedAt { get; set; }
        public Guid[] SourceUuids { get; set; } = [];
    }

    private sealed record PageRow(
        Guid PageUuid,
        Guid? ArtistUuid,
        Guid? AfterSourceUuid,
        int Limit,
        string ProcessingVersion,
        DateTime SelectedAt,
        bool HasMore
    );
}
