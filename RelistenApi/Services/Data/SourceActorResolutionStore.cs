using System;
using System.Data;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Retains source actor identity proposals, not accepted assignments or canonical aliases.
/// Ordinary attempt transactions never acquire Work locks; publication enters with those
/// locks already held. First preparations and checkpoints survive stale work and cancellation.
/// </summary>
public sealed partial class SourceActorResolutionStore(DbService db)
{
    public const int MaximumClaims = 1000;
    public const int MaximumCheckpointBytes =
        SourceActorResolutionCheckpoint.MaximumCheckpointBytes;
    private const string Columns = """
        attempt_uuid AS "AttemptUuid",source_uuid AS "SourceUuid",extraction_attempt_uuid AS "ExtractionAttemptUuid",
        processing_version AS "ProcessingVersion",preparation::text AS "PreparationJson",
        disposition AS "Disposition",reason AS "Reason"
        """;

    /// <summary>Creates an exact request against a successfully published extraction; changed headers conflict.</summary>
    internal Task<SourceActorResolutionAttempt> GetOrCreateAttemptAsync(
        Guid attemptUuid,
        Guid extractionAttemptUuid,
        string processingVersion,
        CancellationToken ct = default
    )
    {
        if (attemptUuid == Guid.Empty || extractionAttemptUuid == Guid.Empty)
        {
            throw new ArgumentException(
                "Stable resolution and extraction attempt UUIDs are required."
            );
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(processingVersion);
        return InTransaction(
            async (c, t) =>
            {
                var source =
                    await c.QuerySingleOrDefaultAsync<Guid?>(
                        new CommandDefinition(
                            """
                            SELECT source_uuid FROM normalization.recording_processing_attempts
                            WHERE attempt_uuid=@extractionAttemptUuid AND disposition='published' AND batch IS NOT NULL
                            """,
                            new { extractionAttemptUuid },
                            t,
                            cancellationToken: ct
                        )
                    )
                    ?? throw new ArgumentException(
                        "Source actor resolution requires a published recording extraction."
                    );
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO normalization.source_actor_resolution_attempts(attempt_uuid,source_uuid,extraction_attempt_uuid,processing_version)
                        VALUES(@attemptUuid,@source,@extractionAttemptUuid,@processingVersion) ON CONFLICT DO NOTHING
                        """,
                        new
                        {
                            attemptUuid,
                            source,
                            extractionAttemptUuid,
                            processingVersion,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                var row = await Require(c, t, attemptUuid, ct);
                if (
                    row.SourceUuid != source
                    || row.ExtractionAttemptUuid != extractionAttemptUuid
                    || row.ProcessingVersion != processingVersion
                )
                {
                    throw new NormalizationConflictException(
                        "Resolution attempt already belongs to another extraction or version."
                    );
                }
                return row.ToAttempt();
            },
            ct
        );
    }

    /// <summary>Reads the original request, saved preparation and terminal result without refreshing their proof.</summary>
    public Task<SourceActorResolutionAttempt?> GetAttemptAsync(
        Guid attemptUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<SourceActorResolutionAttempt?>(async c =>
            (await Read((NpgsqlConnection)c, null, attemptUuid, false, ct))?.ToAttempt()
        );

    /// <summary>Reads the last successful proposal batch, even when later work is pending or stale.</summary>
    public Task<SourceActorResolutionBatch?> GetAsync(
        Guid sourceUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<SourceActorResolutionBatch?>(async c =>
        {
            var json = await c.QuerySingleOrDefaultAsync<string>(
                new CommandDefinition(
                    """
                    SELECT a.batch::text FROM normalization.work_subjects s
                    JOIN normalization.source_actor_resolution_attempts a ON a.attempt_uuid=s.published_work_uuid
                        AND a.source_uuid=s.subject_uuid AND a.disposition='published'
                    WHERE s.domain='people' AND s.subject_kind='source_actor_resolution' AND s.subject_uuid=@sourceUuid
                    """,
                    new { sourceUuid },
                    cancellationToken: ct
                )
            );
            return json == null ? null : SourceActorResolutionCheckpoint.ReadBatch(json);
        });

    /// <summary>Returns historical computed proposals independently of current publication or terminal outcome.</summary>
    public Task<SourceActorResolutionBatch?> GetAttemptBatchAsync(
        Guid attemptUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<SourceActorResolutionBatch?>(async c =>
        {
            var json = await ReadBatch((NpgsqlConnection)c, null, attemptUuid, ct);
            return json == null ? null : SourceActorResolutionCheckpoint.ReadBatch(json);
        });

    /// <summary>First preparation wins; a concurrent caller resumes that original instead of replacing its facts.</summary>
    internal Task<SourceActorResolutionAttempt> SavePreparationAsync(
        Guid attemptUuid,
        SourceActorResolutionPreparation preparation,
        CancellationToken ct = default
    )
    {
        return InTransaction(
            async (c, t) =>
            {
                var row = await Require(c, t, attemptUuid, ct);
                if (row.PreparationJson != null || row.Disposition != null)
                {
                    return row.ToAttempt();
                }
                // Return the retained winner before encoding the competing preparation.
                // Recovery must not replace its original facts or apply a newer write budget.
                var json = SourceActorResolutionCheckpoint.WritePreparation(preparation);
                var frozen = SourceActorResolutionCheckpoint.ReadPreparation(json);
                await ValidatePreparation(c, t, row.ToAttempt(), frozen, ct);
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.source_actor_resolution_attempts SET preparation=@json::jsonb WHERE attempt_uuid=@attemptUuid",
                        new { json, attemptUuid },
                        t,
                        cancellationToken: ct
                    )
                );
                row.PreparationJson = json;
                return row.ToAttempt();
            },
            ct
        );
    }

    /// <summary>Completes only the observed preparation; a concurrent winner remains resumable.</summary>
    internal Task<SourceActorResolutionAttempt> CompleteAsync(
        SourceActorResolutionAttempt expected,
        SourceActorResolutionDisposition disposition,
        string reason,
        CancellationToken ct = default
    )
    {
        if (
            disposition
            is not (
                SourceActorResolutionDisposition.Unresolved
                or SourceActorResolutionDisposition.Stale
            )
        )
        {
            throw new ArgumentException(
                "Only unresolved or stale work completes outside publication."
            );
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return InTransaction(
            async (c, t) =>
            {
                var row = await Require(c, t, expected.AttemptUuid, ct);
                if (
                    row.ExtractionAttemptUuid != expected.ExtractionAttemptUuid
                    || row.SourceUuid != expected.SourceUuid
                    || row.ProcessingVersion != expected.ProcessingVersion
                )
                {
                    throw new NormalizationConflictException(
                        "Resolution completion does not match its original request."
                    );
                }
                if (
                    row.Disposition != null
                    || !SourceActorResolutionCheckpoint.EquivalentPreparation(
                        row.PreparationJson == null
                            ? null
                            : SourceActorResolutionCheckpoint.ReadPreparation(row.PreparationJson),
                        expected.Preparation
                    )
                )
                {
                    return row.ToAttempt();
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE normalization.source_actor_resolution_attempts SET disposition=@state,reason=@reason,completed_at=clock_timestamp()
                        WHERE attempt_uuid=@AttemptUuid
                        """,
                        new
                        {
                            expected.AttemptUuid,
                            state = disposition.ToString().ToLowerInvariant(),
                            reason,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                row.Disposition = disposition.ToString();
                row.Reason = reason;
                return row.ToAttempt();
            },
            ct
        );
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

    private static Task<Row?> Read(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid attemptUuid,
        bool locked,
        CancellationToken ct
    ) =>
        c.QuerySingleOrDefaultAsync<Row>(
            new CommandDefinition(
                $"SELECT {Columns} FROM normalization.source_actor_resolution_attempts WHERE attempt_uuid=@attemptUuid {(locked ? "FOR UPDATE" : "")}",
                new { attemptUuid },
                t,
                cancellationToken: ct
            )
        );

    private static async Task<Row> Require(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid uuid,
        CancellationToken ct
    ) =>
        await Read(c, t, uuid, true, ct)
        ?? throw new ArgumentException("Unknown source actor resolution attempt.");

    private static Task<string?> ReadBatch(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid attemptUuid,
        CancellationToken ct
    ) =>
        c.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(
                "SELECT batch::text FROM normalization.source_actor_resolution_attempts WHERE attempt_uuid=@attemptUuid",
                new { attemptUuid },
                t,
                cancellationToken: ct
            )
        );

    private static bool Same(string? left, string? right) =>
        left == null || right == null
            ? left == right
            : JsonElement.DeepEquals(
                JsonSerializer.Deserialize<JsonElement>(left),
                JsonSerializer.Deserialize<JsonElement>(right)
            );

    private sealed class Row
    {
        public Guid AttemptUuid { get; init; }
        public Guid SourceUuid { get; init; }
        public Guid ExtractionAttemptUuid { get; init; }
        public string ProcessingVersion { get; init; } = null!;
        public string? PreparationJson { get; set; }
        public string? Disposition { get; set; }
        public string? Reason { get; set; }

        public SourceActorResolutionAttempt ToAttempt() =>
            new(
                AttemptUuid,
                SourceUuid,
                ExtractionAttemptUuid,
                ProcessingVersion,
                PreparationJson == null
                    ? null
                    : SourceActorResolutionCheckpoint.ReadPreparation(PreparationJson),
                Disposition == null
                    ? null
                    : new(
                        AttemptUuid,
                        SourceUuid,
                        Enum.Parse<SourceActorResolutionDisposition>(Disposition, true),
                        Reason!
                    )
            );
    }
}
