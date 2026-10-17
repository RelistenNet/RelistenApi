using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedPeopleService
{
    public async Task<NormalizationReviewResult> ReverseMergeAsync(
        IdentityMergeReversal command,
        string trustedOperator,
        CancellationToken cancellationToken = default
    )
    {
        var targetOwner = await db.WithWriteConnection(c =>
            NormalizedPeopleService.FindIdentityOwner(
                (Npgsql.NpgsqlConnection)c,
                null,
                "people",
                "person",
                command.SurvivorUuid,
                cancellationToken
            )
        );
        return await work.ApplyReviewAsync(
            new NormalizationReviewCommand(
                command.OperationUuid,
                command.SubjectUuid,
                command.ExpectedEditorialRevision,
                "split",
                JsonSerializer.SerializeToElement(command),
                command.Reason,
                trustedOperator,
                AdditionalSubjectUuids: [targetOwner]
            ),
            async (c, t, ct) =>
            {
                await ValidateOwnerAsync(
                    c,
                    t,
                    command.SubjectUuid,
                    "people",
                    "person",
                    command.OriginalUuid,
                    ct
                );
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "SELECT pg_advisory_xact_lock(hashtextextended('normalization:people:identity',0))",
                        transaction: t,
                        cancellationToken: ct
                    )
                );
                var rows = (
                    await c.QueryAsync<EntityRevision>(
                        new CommandDefinition(
                            "SELECT uuid AS \"Uuid\",revision AS \"Revision\" FROM normalization.people WHERE uuid=ANY(@ids) ORDER BY uuid FOR UPDATE",
                            new { ids = new[] { command.OriginalUuid, command.SurvivorUuid } },
                            t,
                            cancellationToken: ct
                        )
                    )
                ).ToArray();
                if (
                    rows.SingleOrDefault(r => r.Uuid == command.OriginalUuid)?.Revision
                        != command.ExpectedOriginalRevision
                    || rows.SingleOrDefault(r => r.Uuid == command.SurvivorUuid)?.Revision
                        != command.ExpectedSurvivorRevision
                )
                {
                    throw new NormalizationConflictException(
                        "Identity changed after merge reversal was prepared."
                    );
                }
                if (
                    !await c.ExecuteScalarAsync<bool>(
                        new CommandDefinition(
                            "SELECT EXISTS(SELECT 1 FROM normalization.person_redirects WHERE retired_uuid=@OriginalUuid AND survivor_uuid=@SurvivorUuid AND decision_uuid=@OriginalMergeOperationUuid)",
                            command,
                            t,
                            cancellationToken: ct
                        )
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "The original merge redirect is no longer current."
                    );
                }
                var moves = (
                    await c.QueryAsync<CreditMove>(
                        new CommandDefinition(
                            "SELECT credit_uuid AS \"CreditUuid\",after_revision AS \"AfterRevision\" FROM normalization.person_credit_moves WHERE decision_uuid=@OriginalMergeOperationUuid AND from_person_uuid=@OriginalUuid AND to_person_uuid=@SurvivorUuid ORDER BY credit_uuid",
                            command,
                            t,
                            cancellationToken: ct
                        )
                    )
                ).ToArray();
                foreach (var move in moves)
                {
                    if (
                        !await c.ExecuteScalarAsync<bool>(
                            new CommandDefinition(
                                "SELECT revision=@AfterRevision AND person_uuid=@SurvivorUuid FROM normalization.recording_credits WHERE uuid=@CreditUuid FOR UPDATE",
                                new
                                {
                                    move.AfterRevision,
                                    move.CreditUuid,
                                    command.SurvivorUuid,
                                },
                                t,
                                cancellationToken: ct
                            )
                        )
                    )
                    {
                        throw new NormalizationConflictException(
                            "A moved credit was corrected after the merge; reversal cannot overwrite it."
                        );
                    }
                    await c.ExecuteAsync(
                        new CommandDefinition(
                            """
                            INSERT INTO normalization.recording_credit_history SELECT *,@OperationUuid FROM normalization.recording_credits WHERE uuid=@CreditUuid;
                            INSERT INTO normalization.person_credit_moves(decision_uuid,credit_uuid,from_person_uuid,to_person_uuid,before_revision,after_revision)
                                SELECT @OperationUuid,uuid,person_uuid,@OriginalUuid,revision,revision+1 FROM normalization.recording_credits WHERE uuid=@CreditUuid;
                            INSERT INTO normalization.recording_credit_evidence(credit_uuid,input_revision_uuid,json_pointer,start_code_point,end_code_point,quote,credit_revision)
                                SELECT e.credit_uuid,e.input_revision_uuid,e.json_pointer,e.start_code_point,e.end_code_point,e.quote,a.revision+1
                                FROM normalization.recording_credit_evidence e JOIN normalization.recording_credits a ON a.uuid=e.credit_uuid
                                    WHERE a.uuid=@CreditUuid AND e.credit_revision=a.revision;
                            UPDATE normalization.recording_credits a SET person_uuid=@OriginalUuid,revision=revision+1,basis='reviewed',decision_uuid=@OperationUuid,updated_at=clock_timestamp(),
                            state=(SELECT h.state FROM normalization.recording_credit_history h WHERE h.uuid=a.uuid AND h.superseding_decision_uuid=@OriginalMergeOperationUuid)
                            WHERE a.uuid=@CreditUuid
                            """,
                            new
                            {
                                command.OperationUuid,
                                command.OriginalUuid,
                                command.OriginalMergeOperationUuid,
                                move.CreditUuid,
                            },
                            t,
                            cancellationToken: ct
                        )
                    );
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "DELETE FROM normalization.person_redirects WHERE retired_uuid=@OriginalUuid; UPDATE normalization.people SET revision=revision+1,updated_at=clock_timestamp() WHERE uuid IN (@OriginalUuid,@SurvivorUuid)",
                        command,
                        t,
                        cancellationToken: ct
                    )
                );
            },
            cancellationToken,
            candidateWriteScopes: IdentityCandidateWriteScopes
        );
    }

    private sealed class CreditMove
    {
        public Guid CreditUuid { get; init; }
        public long AfterRevision { get; init; }
    }
}
