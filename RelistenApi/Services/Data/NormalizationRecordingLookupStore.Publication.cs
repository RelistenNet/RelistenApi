using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRecordingLookupStore
{
    /// <summary>
    /// Called only inside WorkService publication. It saves the outcome and a bound recording's
    /// delivery together with the lookup watermark. It changes no extraction or accepted data.
    /// </summary>
    internal async Task<NormalizationRecordingLookupResult> WritePublishedAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationRecordingLookupAttempt expected,
        NormalizationWorkTicket ticket,
        CancellationToken ct = default
    )
    {
        expected = JsonSerializer.Deserialize<NormalizationRecordingLookupAttempt>(
            JsonSerializer.Serialize(expected)
        )!;
        ticket = JsonSerializer.Deserialize<NormalizationWorkTicket>(
            JsonSerializer.Serialize(ticket)
        )!;

        // Read the work receipt before the attempt lock; ordinary store operations never
        // acquire work-owner locks, so they cannot reverse the publication lock order.
        var work = await c.QuerySingleOrDefaultAsync<LookupWorkRow>(
            new CommandDefinition(
                """
                SELECT r.ticket::text AS "TicketJson",r.state AS "State",s.domain AS "Domain",
                    s.subject_kind AS "Kind",s.subject_uuid AS "EntityUuid"
                FROM normalization.work_runs r JOIN normalization.work_subjects s ON s.uuid=r.subject_uuid
                WHERE r.uuid=@WorkUuid
                """,
                new { ticket.WorkUuid },
                t,
                cancellationToken: ct
            )
        );
        if (
            work == null
            || work.State != "running"
            || work.Domain != "lookups"
            || work.Kind != "recording"
            || work.EntityUuid != expected.Registration.LookupUuid
            || ticket.WorkUuid != expected.AttemptUuid
            || ticket.PolicyVersion != expected.ProcessingVersion
            || !SameJson(work.TicketJson, JsonSerializer.Serialize(ticket))
        )
        {
            throw new NormalizationConflictException(
                "Lookup publication does not match its running work receipt and owner."
            );
        }
        var row = await RequireAttempt(c, t, expected.AttemptUuid, ct);
        RequireHeader(row, expected);
        if (
            row.ResultJson != null
            || row.PreparationJson == null
            || !SameJson(row.PreparationJson, JsonSerializer.Serialize(expected.Preparation))
        )
        {
            throw new NormalizationConflictException(
                "Lookup publication does not match its unfinished saved preparation."
            );
        }
        var preparation = row.ToAttempt().Preparation!;
        if (!NormalizationRecordingLookupReadSet.IsComplete(ticket, preparation))
        {
            throw new NormalizationConflictException(
                "Lookup publication must consume its exact complete prepared read set."
            );
        }
        var binding = preparation.Binding?.Recording;
        var status = binding?.Status ?? NormalizationCatalogBindingStatus.Missing;
        var handoffUuid =
            status == NormalizationCatalogBindingStatus.Bound ? row.AttemptUuid : (Guid?)null;
        var result = new NormalizationRecordingLookupResult(
            row.AttemptUuid,
            row.LookupUuid,
            NormalizationRecordingLookupDisposition.Published,
            status,
            binding?.Reason ?? "recording_missing",
            handoffUuid
        );
        await c.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE normalization.recording_lookup_attempts SET result=@json::jsonb,disposition='Published',
                    publication_work_uuid=attempt_uuid,completed_at=clock_timestamp() WHERE attempt_uuid=@AttemptUuid
                """,
                new { json = JsonSerializer.Serialize(result), row.AttemptUuid },
                t,
                cancellationToken: ct
            )
        );
        if (handoffUuid != null)
        {
            await c.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO normalization.recording_lookup_handoffs(uuid,lookup_attempt_uuid,processing_attempt_uuid)
                    VALUES(@handoffUuid,@AttemptUuid,@ProcessingAttemptUuid)
                    """,
                    new
                    {
                        handoffUuid,
                        row.AttemptUuid,
                        preparation.ProcessingAttemptUuid,
                    },
                    t,
                    cancellationToken: ct
                )
            );
        }
        return result;
    }

    private sealed record LookupWorkRow(
        string TicketJson,
        string State,
        string Domain,
        string Kind,
        Guid EntityUuid
    );
}
