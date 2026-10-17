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
/// Retains recording extraction attempts, their original preparation and typed proposals.
/// Attempt transactions never acquire work-owner locks. Publication holds work locks first,
/// then writes the attempt through its supplied transaction. No accepted domain rows are changed.
/// </summary>
public sealed partial class NormalizationRecordingProposalService(DbService db)
{
    private const string AttemptColumns = """
        attempt_uuid AS "AttemptUuid",source_uuid AS "SourceUuid",processing_version AS "ProcessingVersion",
        preparation::text AS "PreparationJson",disposition AS "Disposition",reason AS "Reason",
        initiating_handoff_uuid AS "InitiatingHandoffUuid"
        """;

    /// <summary>Creates a durable request or returns its exact header replay. Changed source, version or initiating handoff conflicts.</summary>
    public Task<NormalizationRecordingAttempt> GetOrCreateAttemptAsync(
        Guid attemptUuid,
        Guid sourceUuid,
        string processingVersion,
        CancellationToken ct = default,
        Guid? initiatingHandoffUuid = null
    )
    {
        RequireUuid(attemptUuid, nameof(attemptUuid));
        RequireUuid(sourceUuid, nameof(sourceUuid));
        ArgumentException.ThrowIfNullOrWhiteSpace(processingVersion);
        if (initiatingHandoffUuid == Guid.Empty)
        {
            throw new ArgumentException(
                "An initiating handoff UUID cannot be empty.",
                nameof(initiatingHandoffUuid)
            );
        }

        return InTransaction(
            async (connection, transaction) =>
            {
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO normalization.recording_processing_attempts(attempt_uuid,source_uuid,processing_version,initiating_handoff_uuid)
                        VALUES(@attemptUuid,@sourceUuid,@processingVersion,@initiatingHandoffUuid) ON CONFLICT DO NOTHING
                        """,
                        new
                        {
                            attemptUuid,
                            sourceUuid,
                            processingVersion,
                            initiatingHandoffUuid,
                        },
                        transaction,
                        cancellationToken: ct
                    )
                );
                var row = await RequireAttempt(connection, transaction, attemptUuid, ct);
                if (
                    row.SourceUuid != sourceUuid
                    || row.ProcessingVersion != processingVersion
                    || row.InitiatingHandoffUuid != initiatingHandoffUuid
                )
                {
                    throw new NormalizationConflictException(
                        "Attempt UUID already belongs to another recording or processing version."
                    );
                }
                await RequireInitiatingHandoff(connection, transaction, row, null, ct);
                return row.ToAttempt();
            },
            ct
        );
    }

    /// <summary>Returns the saved request, original preparation and terminal outcome, or null for an unknown attempt.</summary>
    public Task<NormalizationRecordingAttempt?> GetAttemptAsync(
        Guid attemptUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<NormalizationRecordingAttempt?>(async connection =>
            (
                await ReadAttempt((NpgsqlConnection)connection, null, attemptUuid, false, ct)
            )?.ToAttempt()
        );

    /// <summary>
    /// Saves the first preparation. Concurrent or later callers receive that winner without replacing
    /// its input receipt, producer clocks or admission revisions. Terminal attempts remain unchanged.
    /// </summary>
    public Task<NormalizationRecordingAttempt> SavePreparationAsync(
        Guid attemptUuid,
        NormalizationRecordingPreparation preparation,
        CancellationToken ct = default
    )
    {
        var json = JsonSerializer.Serialize(preparation);
        var saved = JsonSerializer.Deserialize<NormalizationRecordingPreparation>(json)!;
        return InTransaction(
            async (connection, transaction) =>
            {
                var row = await RequireAttempt(connection, transaction, attemptUuid, ct);
                if (row.PreparationJson != null || row.Disposition != null)
                {
                    return row.ToAttempt();
                }

                ValidateCurrentPreparation(row.ProcessingVersion, saved);
                ValidatePreparation(row.SourceUuid, saved);
                await RequirePreparationReceipt(connection, transaction, saved, ct);
                await RequireInitiatingHandoff(connection, transaction, row, saved, ct);
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE normalization.recording_processing_attempts
                        SET preparation=@json::jsonb,preparation_fetch_uuid=@FetchUuid WHERE attempt_uuid=@attemptUuid
                        """,
                        new
                        {
                            json,
                            saved.FetchUuid,
                            attemptUuid,
                        },
                        transaction,
                        cancellationToken: ct
                    )
                );
                row.PreparationJson = json;
                return row.ToAttempt();
            },
            ct
        );
    }

    /// <summary>
    /// Records Missing, Unresolved or Stale only if the observed preparation still matches, including
    /// an absent preparation. A concurrent preparation winner is returned unfinished so the caller
    /// can resume it. The first terminal outcome always wins. Publication uses the work transaction.
    /// </summary>
    public Task<NormalizationRecordingAttempt> CompleteAsync(
        NormalizationRecordingAttempt expected,
        NormalizationRecordingProcessingDisposition disposition,
        string reason,
        CancellationToken ct = default
    )
    {
        if (
            disposition
            is not (
                NormalizationRecordingProcessingDisposition.Missing
                or NormalizationRecordingProcessingDisposition.Unresolved
                or NormalizationRecordingProcessingDisposition.Stale
            )
        )
        {
            throw new ArgumentException(
                "Only missing, unresolved or stale attempts complete without publication.",
                nameof(disposition)
            );
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var expectedPreparation =
            expected.Preparation == null ? null : JsonSerializer.Serialize(expected.Preparation);

        return InTransaction(
            async (connection, transaction) =>
            {
                var row = await RequireAttempt(connection, transaction, expected.AttemptUuid, ct);
                if (
                    row.SourceUuid != expected.SourceUuid
                    || row.ProcessingVersion != expected.ProcessingVersion
                    || row.InitiatingHandoffUuid != expected.InitiatingHandoffUuid
                )
                {
                    throw new NormalizationConflictException(
                        "Attempt request does not match its saved header."
                    );
                }
                if (row.Disposition != null)
                {
                    return row.ToAttempt();
                }
                if (!SameJson(row.PreparationJson, expectedPreparation))
                {
                    return row.ToAttempt();
                }
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE normalization.recording_processing_attempts SET disposition=@state,reason=@reason,
                            completed_at=clock_timestamp() WHERE attempt_uuid=@attemptUuid
                        """,
                        new
                        {
                            state = disposition.ToString().ToLowerInvariant(),
                            reason,
                            attemptUuid = expected.AttemptUuid,
                        },
                        transaction,
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

    /// <summary>
    /// Returns the extraction owner's last successful batch, including when it has since become
    /// pending. This is retained extraction output, not a freshness proof or accepted identity data.
    /// </summary>
    public Task<NormalizationRecordingProposalBatch?> GetAsync(
        Guid sourceUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<NormalizationRecordingProposalBatch?>(async connection =>
        {
            var json = await connection.QuerySingleOrDefaultAsync<string>(
                new CommandDefinition(
                    """
                    SELECT attempt.batch::text FROM normalization.work_subjects owner
                    JOIN normalization.recording_processing_attempts attempt ON attempt.attempt_uuid=owner.published_work_uuid
                        AND attempt.source_uuid=owner.subject_uuid AND attempt.disposition='published'
                    WHERE owner.domain='extractions' AND owner.subject_kind='recording' AND owner.subject_uuid=@sourceUuid
                    """,
                    new { sourceUuid },
                    cancellationToken: ct
                )
            );
            return json == null
                ? null
                : JsonSerializer.Deserialize<NormalizationRecordingProposalBatch>(json);
        });

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

    private static async Task<AttemptRow> RequireAttempt(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid attemptUuid,
        CancellationToken ct
    ) =>
        await ReadAttempt(connection, transaction, attemptUuid, true, ct)
        ?? throw new ArgumentException(
            "Unknown recording processing attempt.",
            nameof(attemptUuid)
        );

    private static Task<AttemptRow?> ReadAttempt(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid attemptUuid,
        bool forUpdate,
        CancellationToken ct
    ) =>
        connection.QuerySingleOrDefaultAsync<AttemptRow>(
            new CommandDefinition(
                $"SELECT {AttemptColumns} FROM normalization.recording_processing_attempts WHERE attempt_uuid=@attemptUuid {(forUpdate ? "FOR UPDATE" : "")}",
                new { attemptUuid },
                transaction,
                cancellationToken: ct
            )
        );

    private static void RequireUuid(Guid uuid, string parameter)
    {
        if (uuid == Guid.Empty)
        {
            throw new ArgumentException("A UUID is required.", parameter);
        }
    }

    private static bool SameJson(string? first, string? second)
    {
        if (first == null || second == null)
        {
            return first == second;
        }
        using var firstDocument = JsonDocument.Parse(first);
        using var secondDocument = JsonDocument.Parse(second);
        return JsonElement.DeepEquals(firstDocument.RootElement, secondDocument.RootElement);
    }

    private sealed class AttemptRow
    {
        public Guid AttemptUuid { get; init; }
        public Guid SourceUuid { get; init; }
        public string ProcessingVersion { get; init; } = null!;
        public Guid? InitiatingHandoffUuid { get; init; }
        public string? PreparationJson { get; set; }
        public string? Disposition { get; set; }
        public string? Reason { get; set; }

        public NormalizationRecordingAttempt ToAttempt() =>
            new(
                AttemptUuid,
                SourceUuid,
                ProcessingVersion,
                PreparationJson == null
                    ? null
                    : JsonSerializer.Deserialize<NormalizationRecordingPreparation>(
                        PreparationJson
                    ),
                Disposition == null
                    ? null
                    : new(
                        AttemptUuid,
                        SourceUuid,
                        Enum.Parse<NormalizationRecordingProcessingDisposition>(Disposition, true),
                        Reason!
                    ),
                InitiatingHandoffUuid
            );
    }
}
