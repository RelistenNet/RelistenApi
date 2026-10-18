using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class SourceActorResolutionStore
{
    /// <summary>Checkpoints proposals before publication; stale work and callback rollback retain this history.</summary>
    internal Task<SourceActorResolutionAttempt> SaveProposalsAsync(
        SourceActorResolutionBatch batch,
        CancellationToken ct = default
    )
    {
        return InTransaction(
            async (c, t) =>
            {
                var work = await ReadWork(c, t, batch.AttemptUuid, ct);
                var row = await Require(c, t, batch.AttemptUuid, ct);
                RequireBatchHeader(row, batch);
                if (row.Disposition != null)
                {
                    return row.ToAttempt();
                }
                RequireWork(work, batch);
                if (
                    !SourceActorResolutionCheckpoint.EquivalentPreparation(
                        row.PreparationJson == null
                            ? null
                            : SourceActorResolutionCheckpoint.ReadPreparation(row.PreparationJson),
                        batch.Preparation
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "Resolution proposals require their exact saved preparation."
                    );
                }
                var previous = await ReadBatch(c, t, batch.AttemptUuid, ct);
                if (previous != null)
                {
                    if (
                        !SourceActorResolutionCheckpoint.EquivalentBatch(
                            SourceActorResolutionCheckpoint.ReadBatch(previous),
                            batch
                        )
                    )
                    {
                        throw new NormalizationConflictException(
                            "A different resolution checkpoint already belongs to this attempt."
                        );
                    }
                    return row.ToAttempt();
                }
                var json = SourceActorResolutionCheckpoint.WriteBatch(batch);
                var frozen = SourceActorResolutionCheckpoint.ReadBatch(json);
                if (!SourceActorResolutionReadSet.IsComplete(frozen.Ticket, frozen.Preparation))
                {
                    throw new ArgumentException(
                        "Source actor resolution proposals must consume the complete original read set."
                    );
                }
                await ValidatePreparation(c, t, row.ToAttempt(), frozen.Preparation, ct);
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE normalization.source_actor_resolution_attempts SET batch=@json::jsonb,work_uuid=attempt_uuid WHERE attempt_uuid=@AttemptUuid
                        """,
                        new { json, frozen.AttemptUuid },
                        t,
                        cancellationToken: ct
                    )
                );
                return row.ToAttempt();
            },
            ct
        );
    }

    /// <summary>Called only by Work publication, with its locks and freshness checks already held.</summary>
    internal async Task WritePublishedAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorResolutionBatch batch,
        CancellationToken ct = default
    )
    {
        // Publication changes state only. The saved checkpoint already passed its original
        // write budget; publication must not encode a replacement checkpoint for recovery.
        var work = await ReadWork(c, t, batch.AttemptUuid, ct);
        RequireWork(work, batch);
        var row = await Require(c, t, batch.AttemptUuid, ct);
        var savedBatch = await ReadBatch(c, t, batch.AttemptUuid, ct);
        RequireBatchHeader(row, batch);
        if (
            work!.State != "running"
            || row.Disposition != null
            || !SourceActorResolutionCheckpoint.EquivalentPreparation(
                row.PreparationJson == null
                    ? null
                    : SourceActorResolutionCheckpoint.ReadPreparation(row.PreparationJson),
                batch.Preparation
            )
            || savedBatch == null
            || !SourceActorResolutionCheckpoint.EquivalentBatch(
                SourceActorResolutionCheckpoint.ReadBatch(savedBatch),
                batch
            )
        )
        {
            throw new NormalizationConflictException(
                "Resolution publication requires its exact unfinished checkpoint."
            );
        }

        // Work already holds the complete original read-set locks. The typed proof check
        // authenticates source, policy and review authority without restamping the checkpoint.
        await RequireCurrentProofAsync(c, t, batch, ct);
        await c.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE normalization.source_actor_resolution_attempts SET disposition='published',reason='resolution_proposals',completed_at=clock_timestamp()
                WHERE attempt_uuid=@AttemptUuid
                """,
                new { batch.AttemptUuid },
                t,
                cancellationToken: ct
            )
        );
    }

    private static void RequireBatchHeader(Row row, SourceActorResolutionBatch batch)
    {
        if (
            row.SourceUuid != batch.SourceUuid
            || row.ExtractionAttemptUuid != batch.ExtractionAttemptUuid
            || row.ProcessingVersion != batch.ProcessingVersion
        )
        {
            throw new NormalizationConflictException(
                "Resolution batch does not match its attempt header."
            );
        }
    }

    private static Task<WorkRow?> ReadWork(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid attemptUuid,
        CancellationToken ct
    ) =>
        c.QuerySingleOrDefaultAsync<WorkRow>(
            new CommandDefinition(
                """
                SELECT s.domain AS "Domain",s.subject_kind AS "Kind",s.subject_uuid AS "SourceUuid",r.state AS "State",r.ticket::text AS "TicketJson"
                FROM normalization.work_runs r JOIN normalization.work_subjects s ON s.uuid=r.subject_uuid WHERE r.uuid=@attemptUuid
                """,
                new { attemptUuid },
                t,
                cancellationToken: ct
            )
        );

    private static void RequireWork(WorkRow? work, SourceActorResolutionBatch batch)
    {
        var owner = SourceActorResolutionService.Owner(batch.SourceUuid);
        if (
            work == null
            || work.Domain != owner.Domain
            || work.Kind != owner.Kind
            || work.SourceUuid != owner.Uuid
            || batch.Ticket.WorkUuid != batch.AttemptUuid
            || batch.Ticket.PolicyVersion != batch.ProcessingVersion
            || !Same(work.TicketJson, JsonSerializer.Serialize(batch.Ticket))
        )
        {
            throw new NormalizationConflictException(
                "Resolution proposals require their exact separate proposal-owner ticket."
            );
        }
    }

    private sealed record WorkRow(
        string Domain,
        string Kind,
        Guid SourceUuid,
        string State,
        string TicketJson
    );
}
