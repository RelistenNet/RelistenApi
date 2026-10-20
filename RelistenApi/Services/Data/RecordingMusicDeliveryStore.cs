using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Stores orchestration coordinates only. Domain processors retain their own evidence,
/// allocations and publication receipts. Updates use the observed phase as their CAS.
/// </summary>
public sealed class RecordingMusicDeliveryStore(DbService db)
{
    private const string Columns = """
        handoff_uuid AS "HandoffUuid",work_uuid AS "WorkUuid",source_uuid AS "SourceUuid",
        artist_uuid AS "ArtistUuid",processing_version AS "ProcessingVersion",
        dictionary_attempt_uuid AS "DictionaryAttemptUuid",repertoire_attempt_uuid AS "RepertoireAttemptUuid",
        content_attempt_uuid AS "ContentAttemptUuid",phase AS "Phase",
        dictionary_fetch_uuid AS "DictionaryFetchUuid",result::text AS "ResultJson"
        """;

    public Task<RecordingMusicDelivery?> GetAsync(
        Guid handoffUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection(async c =>
            (
                await c.QuerySingleOrDefaultAsync<Row>(
                    new CommandDefinition(
                        $"SELECT {Columns} FROM normalization.recording_music_deliveries WHERE handoff_uuid=@handoffUuid",
                        new { handoffUuid },
                        cancellationToken: ct
                    )
                )
            )?.ToDelivery()
        );

    /// <summary>Lists unfinished deliveries, including those whose extraction is already acknowledged.</summary>
    public Task<RecordingMusicDeliveryPage> GetPendingAsync(
        int limit = 100,
        Guid? afterHandoffUuid = null,
        CancellationToken ct = default
    )
    {
        if (limit is < 1 or > 200 || afterHandoffUuid == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
        return db.WithWriteConnection(async c =>
        {
            var rows = (
                await c.QueryAsync<Row>(
                    new CommandDefinition(
                        $"""
                        SELECT {Columns} FROM normalization.recording_music_deliveries
                        WHERE result IS NULL AND (@afterHandoffUuid::uuid IS NULL OR handoff_uuid>@afterHandoffUuid)
                        ORDER BY handoff_uuid LIMIT @take
                        """,
                        new { afterHandoffUuid, take = limit + 1 },
                        cancellationToken: ct
                    )
                )
            ).ToArray();
            return new RecordingMusicDeliveryPage(
                rows.Take(limit).Select(row => row.ToDelivery()).ToArray(),
                rows.Length > limit ? rows[limit - 1].HandoffUuid : null
            );
        });
    }

    internal async Task<RecordingMusicDelivery> GetOrCreateAsync(
        Guid handoffUuid,
        Guid sourceUuid,
        Guid artistUuid,
        string version,
        CancellationToken ct
    )
    {
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO normalization.recording_music_deliveries(
                        handoff_uuid,work_uuid,source_uuid,artist_uuid,processing_version,
                        dictionary_attempt_uuid,repertoire_attempt_uuid,content_attempt_uuid)
                    VALUES(@handoffUuid,@workUuid,@sourceUuid,@artistUuid,@version,@dictionary,@repertoire,@contents)
                    ON CONFLICT(handoff_uuid) DO NOTHING
                    """,
                    new
                    {
                        handoffUuid,
                        workUuid = Guid.NewGuid(),
                        sourceUuid,
                        artistUuid,
                        version,
                        dictionary = Guid.NewGuid(),
                        repertoire = Guid.NewGuid(),
                        contents = Guid.NewGuid(),
                    },
                    cancellationToken: ct
                )
            )
        );
        var saved = (await GetAsync(handoffUuid, ct))!;
        if (saved.SourceUuid != sourceUuid || saved.ArtistUuid != artistUuid)
        {
            throw new NormalizationConflictException(
                "Music delivery belongs to another authenticated recording."
            );
        }
        return saved;
    }

    internal async Task<RecordingMusicDelivery> AdvanceAsync(
        RecordingMusicDelivery expected,
        RecordingMusicDeliveryPhase next,
        CancellationToken ct,
        RecordingMusicDeliveryResult? result = null
    )
    {
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE normalization.recording_music_deliveries
                    SET phase=@next,result=@result::jsonb,updated_at=clock_timestamp()
                    WHERE handoff_uuid=@HandoffUuid AND phase=@phase AND result IS NULL
                    """,
                    new
                    {
                        expected.HandoffUuid,
                        phase = (int)expected.Phase,
                        next = (int)next,
                        result = result == null ? null : JsonSerializer.Serialize(result),
                    },
                    cancellationToken: ct
                )
            )
        );
        return (await GetAsync(expected.HandoffUuid, ct))!;
    }

    /// <summary>
    /// Selects and saves one genuine dictionary capture under an artist lock. The callback
    /// only reads/captures local catalog evidence; it must not run Work or accepted writers.
    /// </summary>
    internal Task<RecordingMusicDelivery> CaptureDictionaryAsync(
        RecordingMusicDelivery expected,
        Func<CancellationToken, Task<Guid>> selectCapture,
        CancellationToken ct
    ) =>
        db.WithWriteConnection(async connection =>
        {
            // This explicit transaction is not ambient: the existing capture service still
            // commits retained evidence independently. Disposal releases the advisory lock on
            // cancellation or failure. No importer transaction or network request enters here.
            var c = (NpgsqlConnection)connection;
            await using var transaction = await c.BeginTransactionAsync(ct);
            await c.ExecuteAsync(
                new CommandDefinition(
                    "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
                    new
                    {
                        key = "normalization:recording-music-dictionary:"
                            + expected.ArtistUuid.ToString("D"),
                    },
                    transaction,
                    cancellationToken: ct
                )
            );
            var current = (
                await c.QuerySingleAsync<Row>(
                    new CommandDefinition(
                        $"SELECT {Columns} FROM normalization.recording_music_deliveries WHERE handoff_uuid=@HandoffUuid FOR UPDATE",
                        expected,
                        transaction,
                        cancellationToken: ct
                    )
                )
            ).ToDelivery();
            if (current.Phase != RecordingMusicDeliveryPhase.DictionaryCapture)
            {
                await transaction.CommitAsync(ct);
                return current;
            }
            // Recheck the current producer after taking the artist lock. Another recording
            // may already have captured this dictionary while this delivery was waiting.
            var fetchUuid = await selectCapture(ct);
            await c.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE normalization.recording_music_deliveries
                    SET dictionary_fetch_uuid=@fetchUuid,phase=@phase,updated_at=clock_timestamp()
                    WHERE handoff_uuid=@HandoffUuid
                    """,
                    new
                    {
                        current.HandoffUuid,
                        fetchUuid,
                        phase = (int)RecordingMusicDeliveryPhase.DictionaryResolution,
                    },
                    transaction,
                    cancellationToken: ct
                )
            );
            await transaction.CommitAsync(ct);
            return current with
            {
                DictionaryFetchUuid = fetchUuid,
                Phase = RecordingMusicDeliveryPhase.DictionaryResolution,
            };
        });

    /// <summary>Finds an actual producer receipt; the source service must still validate its catalog fences.</summary>
    internal Task<Guid?> ReadCurrentDictionaryFetchAsync(Guid artistUuid, CancellationToken ct) =>
        db.WithWriteConnection(c =>
            c.QuerySingleOrDefaultAsync<Guid?>(
                new CommandDefinition(
                    """
                    SELECT f.fetch_uuid FROM normalization.input_resources p
                    JOIN normalization.input_fetches f ON f.resource_uuid=p.uuid AND f.input_revision_uuid=p.current_revision_uuid
                        AND f.resource_generation=p.generation AND f.accepted_as_current
                    WHERE p.provider='legacy_catalog' AND p.resource_type='artist_repertoire' AND p.provider_key=@key
                    ORDER BY f.sequence DESC LIMIT 1
                    """,
                    new { key = artistUuid.ToString("D") },
                    cancellationToken: ct
                )
            )
        );

    private sealed class Row
    {
        public Guid HandoffUuid { get; init; }
        public Guid WorkUuid { get; init; }
        public Guid SourceUuid { get; init; }
        public Guid ArtistUuid { get; init; }
        public string ProcessingVersion { get; init; } = "";
        public Guid DictionaryAttemptUuid { get; init; }
        public Guid RepertoireAttemptUuid { get; init; }
        public Guid ContentAttemptUuid { get; init; }
        public int Phase { get; init; }
        public Guid? DictionaryFetchUuid { get; init; }
        public string? ResultJson { get; init; }

        public RecordingMusicDelivery ToDelivery() =>
            new(
                HandoffUuid,
                WorkUuid,
                SourceUuid,
                ArtistUuid,
                ProcessingVersion,
                DictionaryAttemptUuid,
                RepertoireAttemptUuid,
                ContentAttemptUuid,
                (RecordingMusicDeliveryPhase)Phase,
                DictionaryFetchUuid,
                ResultJson == null
                    ? null
                    : JsonSerializer.Deserialize<RecordingMusicDeliveryResult>(ResultJson)
            );
    }
}
