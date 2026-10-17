using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationPeopleResolutionStore
{
    /// <summary>Checkpoints proposals before publication; stale work and callback rollback retain this history.</summary>
    public Task<NormalizationPeopleResolutionAttempt> SaveProposalsAsync(
        NormalizationPeopleResolutionBatch batch,
        CancellationToken ct = default
    )
    {
        var json = Serialize(batch);
        var frozen = JsonSerializer.Deserialize<NormalizationPeopleResolutionBatch>(json)!;
        return InTransaction(
            async (c, t) =>
            {
                var work = await ReadWork(c, t, frozen.AttemptUuid, ct);
                var row = await Require(c, t, frozen.AttemptUuid, ct);
                RequireBatchHeader(row, frozen);
                if (row.Disposition != null)
                {
                    return row.ToAttempt();
                }
                RequireWork(work, frozen);
                if (!Same(row.PreparationJson, JsonSerializer.Serialize(frozen.Preparation)))
                {
                    throw new NormalizationConflictException(
                        "Resolution proposals require their exact saved preparation."
                    );
                }
                var previous = await ReadBatch(c, t, frozen.AttemptUuid, ct);
                if (previous != null)
                {
                    if (!Same(previous, json))
                    {
                        throw new NormalizationConflictException(
                            "A different resolution checkpoint already belongs to this attempt."
                        );
                    }
                    return row.ToAttempt();
                }
                if (
                    !NormalizationPeopleResolutionReadSet.IsComplete(
                        frozen.Ticket,
                        frozen.Preparation
                    )
                )
                {
                    throw new ArgumentException(
                        "People resolution proposals must consume the complete original read set."
                    );
                }
                await ValidatePreparation(c, t, row.ToAttempt(), frozen.Preparation, ct);
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE normalization.people_resolution_attempts SET batch=@json::jsonb,work_uuid=attempt_uuid WHERE attempt_uuid=@AttemptUuid
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
        NormalizationPeopleResolutionBatch batch,
        CancellationToken ct = default
    )
    {
        var json = Serialize(batch);
        var work = await ReadWork(c, t, batch.AttemptUuid, ct);
        RequireWork(work, batch);
        var row = await Require(c, t, batch.AttemptUuid, ct);
        RequireBatchHeader(row, batch);
        if (
            work!.State != "running"
            || row.Disposition != null
            || !Same(row.PreparationJson, JsonSerializer.Serialize(batch.Preparation))
            || !Same(await ReadBatch(c, t, batch.AttemptUuid, ct), json)
        )
        {
            throw new NormalizationConflictException(
                "Resolution publication requires its exact unfinished checkpoint."
            );
        }
        await c.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE normalization.people_resolution_attempts SET disposition='published',reason='resolution_proposals',completed_at=clock_timestamp()
                WHERE attempt_uuid=@AttemptUuid
                """,
                new { batch.AttemptUuid },
                t,
                cancellationToken: ct
            )
        );
    }

    private static void RequireBatchHeader(Row row, NormalizationPeopleResolutionBatch batch)
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

    private static void RequireWork(WorkRow? work, NormalizationPeopleResolutionBatch batch)
    {
        if (
            work == null
            || work.Domain != "people"
            || work.Kind != "recording_resolution"
            || work.SourceUuid != batch.SourceUuid
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
