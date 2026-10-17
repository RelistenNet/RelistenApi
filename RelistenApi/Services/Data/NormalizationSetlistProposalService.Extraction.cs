using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationSetlistProposalService
{
    /// <summary>
    /// Saves the first exact extraction checkpoint. Failure rolls back the checkpoint, while
    /// a later publication failure leaves it available for recovery with the same attempt UUID.
    /// </summary>
    public Task<NormalizationSetlistProcessingAttempt> SaveExtractionAsync(
        NormalizationSetlistProposalBatch batch,
        CancellationToken ct = default
    )
    {
        var json = SerializeCheckpoint(batch);
        var frozen = JsonSerializer.Deserialize<NormalizationSetlistProposalBatch>(json)!;
        return InTransaction(
            async (c, t) =>
            {
                // Read work identity before the attempt lock. This read takes no owner lock;
                // publication alone enters through WorkService with freshness locks held first.
                var work = await ReadExtractionWork(c, t, frozen.AttemptUuid, ct);
                var row = await RequireAttempt(c, t, frozen.AttemptUuid, ct);
                RequireHeader(row, frozen.RegistrationUuid, frozen.ProcessingVersion);
                if (row.Disposition != null)
                {
                    return row.ToAttempt();
                }
                ValidateExtractionWork(frozen, work);
                RequireSavedPreparation(row, frozen);
                var previous = await ReadBatch(c, t, frozen.AttemptUuid, ct);
                if (previous != null)
                {
                    if (!SameJson(previous, json))
                    {
                        throw new NormalizationConflictException(
                            "A different setlist extraction already belongs to this attempt."
                        );
                    }
                    return row.ToAttempt();
                }
                await ValidateBatch(c, t, row.ToAttempt(), frozen, ct);
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE normalization.setlist_processing_attempts SET batch=@json::jsonb,extraction_work_uuid=attempt_uuid
                        WHERE attempt_uuid=@AttemptUuid
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

    public Task<NormalizationSetlistProposalBatch?> GetAttemptBatchAsync(
        Guid attemptUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<NormalizationSetlistProposalBatch?>(async c =>
        {
            var json = await ReadBatch((NpgsqlConnection)c, null, attemptUuid, ct);
            return json == null
                ? null
                : JsonSerializer.Deserialize<NormalizationSetlistProposalBatch>(json);
        });

    /// <summary>Called only inside the exact WorkService publication transaction, after its freshness fences.</summary>
    internal async Task WritePublishedAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationSetlistProposalBatch batch,
        CancellationToken ct = default
    )
    {
        var json = SerializeCheckpoint(batch);
        var frozen = JsonSerializer.Deserialize<NormalizationSetlistProposalBatch>(json)!;
        var work = await ReadExtractionWork(c, t, frozen.AttemptUuid, ct);
        ValidateExtractionWork(frozen, work);
        if (work!.State != "running")
        {
            throw new NormalizationConflictException(
                "Only running setlist extraction work can publish."
            );
        }
        var row = await RequireAttempt(c, t, frozen.AttemptUuid, ct);
        RequireHeader(row, frozen.RegistrationUuid, frozen.ProcessingVersion);
        RequireSavedPreparation(row, frozen);
        if (
            row.Disposition != null
            || !SameJson(await ReadBatch(c, t, frozen.AttemptUuid, ct), json)
        )
        {
            throw new NormalizationConflictException(
                "Setlist publication requires its exact nonterminal extraction checkpoint."
            );
        }
        await c.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE normalization.setlist_processing_attempts SET disposition='published',reason='local_extraction',completed_at=clock_timestamp()
                WHERE attempt_uuid=@AttemptUuid
                """,
                new { frozen.AttemptUuid },
                t,
                cancellationToken: ct
            )
        );
    }

    private static async Task ValidateBatch(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationSetlistProcessingAttempt attempt,
        NormalizationSetlistProposalBatch batch,
        CancellationToken ct
    )
    {
        if (!NormalizationSetlistReadSet.IsComplete(batch.Ticket, batch.Preparation))
        {
            throw new ArgumentException(
                "Setlist proposals must consume the complete saved binding read set."
            );
        }
        if (
            batch.Extractions.Count != batch.Preparation.Inputs.Count
            || batch.Extractions.Select(item => item.InputRevisionUuid).Distinct().Count()
                != batch.Extractions.Count
            || !batch
                .Extractions.Select(item => item.InputRevisionUuid)
                .ToHashSet()
                .SetEquals(batch.Preparation.Inputs.Select(item => item.Input.InputRevisionUuid))
        )
        {
            throw new ArgumentException(
                "Setlist proposals require one extraction for every prepared input."
            );
        }
        var ids = batch
            .Extractions.SelectMany(item => item.Accounts)
            .SelectMany(item => item.Passages)
            .Select(item => item.ProposalUuid)
            .ToArray();
        if (ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length)
        {
            throw new ArgumentException(
                "Setlist passage proposal UUIDs must be distinct and nonempty."
            );
        }
        var expectedExtractions = await ValidatePreparation(c, t, attempt, batch.Preparation, ct);
        foreach (var expected in expectedExtractions)
        {
            ct.ThrowIfCancellationRequested();
            var actual = batch.Extractions.Single(item =>
                item.InputRevisionUuid == expected.InputRevisionUuid
            );
            // Reproduction preserves empty sets, duplicate accounts, unknown positions and
            // all decoder issues. A valid citation alone cannot authorize omitting or adding claims.
            if (!SameJson(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual)))
            {
                throw new ArgumentException(
                    "Setlist proposals must preserve the exact complete extraction from their retained input."
                );
            }
        }
    }

    private static void RequireSavedPreparation(
        AttemptRow row,
        NormalizationSetlistProposalBatch batch
    )
    {
        if (!SameJson(row.PreparationJson, JsonSerializer.Serialize(batch.Preparation)))
        {
            throw new NormalizationConflictException(
                "Setlist extraction must use its original saved preparation."
            );
        }
    }

    private static void ValidateExtractionWork(
        NormalizationSetlistProposalBatch batch,
        ExtractionWorkRow? work
    )
    {
        if (
            work == null
            || work.Domain != "extractions"
            || work.Kind != "setlist"
            || work.RegistrationUuid != batch.RegistrationUuid
            || batch.Ticket.WorkUuid != batch.AttemptUuid
            || batch.Ticket.PolicyVersion != batch.ProcessingVersion
            || !SameJson(work.TicketJson, JsonSerializer.Serialize(batch.Ticket))
        )
        {
            throw new NormalizationConflictException(
                "Setlist proposals require their exact setlist-owner extraction ticket."
            );
        }
    }

    private static Task<string?> ReadBatch(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid attemptUuid,
        CancellationToken ct
    ) =>
        c.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(
                "SELECT batch::text FROM normalization.setlist_processing_attempts WHERE attempt_uuid=@attemptUuid",
                new { attemptUuid },
                t,
                cancellationToken: ct
            )
        );

    private static Task<ExtractionWorkRow?> ReadExtractionWork(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid attemptUuid,
        CancellationToken ct
    ) =>
        c.QuerySingleOrDefaultAsync<ExtractionWorkRow>(
            new CommandDefinition(
                """
                SELECT s.domain AS "Domain",s.subject_kind AS "Kind",s.subject_uuid AS "RegistrationUuid",
                    r.state AS "State",r.ticket::text AS "TicketJson"
                FROM normalization.work_runs r JOIN normalization.work_subjects s ON s.uuid=r.subject_uuid
                WHERE r.uuid=@attemptUuid
                """,
                new { attemptUuid },
                t,
                cancellationToken: ct
            )
        );

    private sealed record ExtractionWorkRow(
        string Domain,
        string Kind,
        Guid RegistrationUuid,
        string State,
        string TicketJson
    );
}
