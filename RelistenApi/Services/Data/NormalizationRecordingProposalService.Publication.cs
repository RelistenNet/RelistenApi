using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRecordingProposalService
{
    /// <summary>
    /// Records the Published outcome for an exact extraction checkpoint inside NormalizationWorkService.TryPublishAsync.
    /// The caller owns freshness and work locks. This method must not run in another transaction.
    /// </summary>
    internal async Task WritePublishedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizationRecordingProposalBatch batch,
        CancellationToken ct = default
    )
    {
        var json = JsonSerializer.Serialize(batch);
        var frozen = JsonSerializer.Deserialize<NormalizationRecordingProposalBatch>(json)!;
        // Work publication already holds owner locks. Read its durable identity before taking the
        // attempt row; ordinary attempt operations never take work locks in the opposite order.
        var work = await ReadExtractionWork(connection, transaction, frozen.AttemptUuid, ct);
        ValidateExtractionWork(frozen, work);
        if (work!.State != "running")
        {
            throw new NormalizationConflictException(
                "Only a running extraction work publication can become current."
            );
        }

        var row = await RequireAttempt(connection, transaction, frozen.AttemptUuid, ct);
        if (
            row.SourceUuid != frozen.SourceUuid
            || row.ProcessingVersion != frozen.ProcessingVersion
            || !SameJson(row.PreparationJson, JsonSerializer.Serialize(frozen.Preparation))
        )
        {
            throw new NormalizationConflictException(
                "Published extraction does not match its saved recording preparation."
            );
        }
        if (row.Disposition != null)
        {
            throw new NormalizationConflictException(
                "A terminal recording attempt cannot publish another result."
            );
        }
        if (!SameJson(await ReadBatch(connection, transaction, frozen.AttemptUuid, ct), json))
        {
            throw new NormalizationConflictException(
                "Publication requires the exact completed extraction checkpoint."
            );
        }
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE normalization.recording_processing_attempts SET disposition='published',reason='local_extraction',
                    completed_at=clock_timestamp()
                WHERE attempt_uuid=@AttemptUuid
                """,
                new { frozen.AttemptUuid },
                transaction,
                cancellationToken: ct
            )
        );
    }
}
