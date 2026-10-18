using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRepertoireResolutionStore
{
    /// <summary>
    /// Work admission can commit before its exact original-stamp comparison fails. Retain
    /// that actual admission so the processor can release its transient artist contribution.
    /// The failure never grants publication authority or replaces the stage's original read set.
    /// </summary>
    internal Task<RepertoireResolutionAttempt> FailAdmissionAsync(
        RepertoireResolutionAttempt expected,
        Guid stageUuid,
        NormalizationWorkTicket actualTicket,
        string reason,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1024)
        {
            throw new ArgumentException("A failed admission requires a bounded reason.");
        }
        var json = Serialize(actualTicket);
        var frozen = JsonSerializer.Deserialize<NormalizationWorkTicket>(json)!;

        return InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, expected.AttemptUuid, ct);
                if (current.Result != null || !SameCheckpoint(current, expected))
                {
                    return current;
                }

                var stage =
                    await ReadStage(c, t, current.AttemptUuid, stageUuid, ct)
                    ?? throw new ArgumentException("Unknown repertoire stage.");
                if (
                    stage.Kind
                        is not (
                            RepertoireResolutionStageKind.AcceptedWrite
                            or RepertoireResolutionStageKind.RetireArtistContribution
                        )
                    || stage.Receipt != null
                    || stage.Superseded
                    || stage.WorkUuid != frozen.WorkUuid
                )
                {
                    throw new NormalizationConflictException(
                        "Only an unfinished artist stage can retain its failed admission."
                    );
                }

                var run = await c.QuerySingleOrDefaultAsync<WorkRow>(
                    new CommandDefinition(
                        "SELECT s.domain AS \"Domain\",s.subject_kind AS \"Kind\",s.subject_uuid AS \"Uuid\",r.state AS \"State\",r.ticket::text AS \"Ticket\" FROM normalization.work_runs r JOIN normalization.work_subjects s ON s.uuid=r.subject_uuid WHERE r.uuid=@workUuid",
                        new { workUuid = frozen.WorkUuid },
                        t,
                        cancellationToken: ct
                    )
                );
                if (
                    run == null
                    || run.State is not ("running" or "stale")
                    || stage.Owner != new NormalizationSubjectKey(run.Domain, run.Kind, run.Uuid)
                    || !Same(
                        frozen,
                        JsonSerializer.Deserialize<NormalizationWorkTicket>(run.Ticket)
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "Failed admission requires the exact saved unfinished Work ticket and stage owner."
                    );
                }

                if (stage.Ticket != null)
                {
                    RequireSame(
                        stage.Ticket,
                        frozen,
                        "A failed admission cannot replace another saved ticket."
                    );
                }
                if (stage.FailureReason != null)
                {
                    RequireSame(
                        stage.FailureReason,
                        reason,
                        "A stage failure reason is immutable."
                    );
                    if (stage.Ticket != null)
                    {
                        return current;
                    }
                }

                // Do not call RequireTicket here: its mismatch with the original source stamps
                // is the failure being recorded. A failed stage is rejected by both admission
                // replay and the accepted writer; only conditional retirement can follow it.
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.repertoire_resolution_stages SET ticket=@json::jsonb,admitted_work_uuid=work_uuid,failure_reason=@reason WHERE attempt_uuid=@attemptUuid AND stage_uuid=@stageUuid",
                        new
                        {
                            attemptUuid = current.AttemptUuid,
                            stageUuid,
                            json,
                            reason,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                await AdvanceCheckpoint(c, t, current.AttemptUuid, ct);
                return (await Read(c, t, current.AttemptUuid, false, ct))!;
            },
            ct
        );
    }
}
