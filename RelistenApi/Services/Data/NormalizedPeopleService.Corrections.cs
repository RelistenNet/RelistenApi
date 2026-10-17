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
    public async Task<NormalizationReviewResult> ApplyIdentityDecisionAsync(
        PersonIdentityDecision command,
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
                command.Target.Uuid,
                cancellationToken
            )
        );
        return await work.ApplyReviewAsync(
            new NormalizationReviewCommand(
                command.OperationUuid,
                command.SubjectUuid,
                command.ExpectedEditorialRevision,
                command.Action,
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
                if (
                    command.Action is not ("merge" or "split")
                    || command.OriginalUuid == command.Target.Uuid
                )
                {
                    throw new ArgumentException(
                        "Identity correction requires distinct original and target identities."
                    );
                }
                // Serialize redirect topology changes before taking entity locks. Sorted locks prevent
                // opposing merge operations from each owning one person while waiting for the other.
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "SELECT pg_advisory_xact_lock(hashtextextended('normalization:people:identity',0))",
                        transaction: t,
                        cancellationToken: ct
                    )
                );
                var revisions = (
                    await c.QueryAsync<EntityRevision>(
                        new CommandDefinition(
                            "SELECT uuid AS \"Uuid\",revision AS \"Revision\" FROM normalization.people WHERE uuid=ANY(@ids) ORDER BY uuid FOR UPDATE",
                            new { ids = new[] { command.OriginalUuid, command.Target.Uuid } },
                            t,
                            cancellationToken: ct
                        )
                    )
                ).ToArray();
                if (
                    revisions.SingleOrDefault(r => r.Uuid == command.OriginalUuid)?.Revision
                    != command.ExpectedOriginalRevision
                )
                {
                    throw new NormalizationConflictException(
                        "Original person changed after review was prepared."
                    );
                }
                if (command.Action == "split")
                {
                    if (revisions.Any(r => r.Uuid == command.Target.Uuid))
                    {
                        throw new NormalizationConflictException(
                            "Split requires a new target UUID."
                        );
                    }
                    await InsertPerson(c, t, command.Target, ct);
                }
                else if (
                    revisions.SingleOrDefault(r => r.Uuid == command.Target.Uuid)?.Revision
                    != command.ExpectedTargetRevision
                )
                {
                    throw new NormalizationConflictException(
                        "Merge survivor changed after review was prepared."
                    );
                }
                if (
                    await c.ExecuteScalarAsync<bool>(
                        new CommandDefinition(
                            "SELECT EXISTS(SELECT 1 FROM normalization.person_redirects WHERE retired_uuid=ANY(@ids))",
                            new { ids = new[] { command.OriginalUuid, command.Target.Uuid } },
                            t,
                            cancellationToken: ct
                        )
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "Use the current survivor identities for corrections."
                    );
                }
                var actual = (
                    await c.QueryAsync<Guid>(
                        new CommandDefinition(
                            "SELECT uuid FROM normalization.recording_credits WHERE person_uuid=@OriginalUuid ORDER BY uuid FOR UPDATE",
                            command,
                            t,
                            cancellationToken: ct
                        )
                    )
                ).ToArray();
                var selected = command.CreditUuids.Distinct().Order().ToArray();
                if (
                    selected.Any(id => !actual.Contains(id))
                    || (command.Action == "merge" && !selected.SequenceEqual(actual.Order()))
                )
                {
                    throw new NormalizationConflictException(
                        "Correction must explicitly list the current relationships to move; merge requires all relationships."
                    );
                }
                if (command.Action == "merge")
                {
                    var contradictory = await c.ExecuteScalarAsync<bool>(
                        new CommandDefinition(
                            """
                            SELECT EXISTS(SELECT 1 FROM normalization.person_external_identities a JOIN normalization.person_external_identities b
                                ON a.provider=b.provider AND a.resource_type=b.resource_type AND a.provider_key<>b.provider_key
                                WHERE a.person_uuid=@OriginalUuid AND b.person_uuid=@target AND a.state='accepted' AND b.state='accepted')
                            """,
                            new { command.OriginalUuid, target = command.Target.Uuid },
                            t,
                            cancellationToken: ct
                        )
                    );
                    if (contradictory)
                    {
                        throw new NormalizationConflictException(
                            "Contradictory provider identities require correction before merge."
                        );
                    }
                    await c.ExecuteAsync(
                        new CommandDefinition(
                            "INSERT INTO normalization.person_redirects(retired_uuid,survivor_uuid,decision_uuid) VALUES(@OriginalUuid,@target,@OperationUuid)",
                            new
                            {
                                command.OriginalUuid,
                                target = command.Target.Uuid,
                                command.OperationUuid,
                            },
                            t,
                            cancellationToken: ct
                        )
                    );
                }
                foreach (var id in selected)
                {
                    await c.ExecuteAsync(
                        new CommandDefinition(
                            """
                            INSERT INTO normalization.recording_credit_history SELECT *,@OperationUuid FROM normalization.recording_credits WHERE uuid=@id;
                            INSERT INTO normalization.person_credit_moves(decision_uuid,credit_uuid,from_person_uuid,to_person_uuid,before_revision,after_revision)
                                SELECT @OperationUuid,uuid,person_uuid,@target,revision,revision+1 FROM normalization.recording_credits WHERE uuid=@id;
                            INSERT INTO normalization.recording_credit_evidence(credit_uuid,input_revision_uuid,json_pointer,start_code_point,end_code_point,quote,credit_revision)
                                SELECT e.credit_uuid,e.input_revision_uuid,e.json_pointer,e.start_code_point,e.end_code_point,e.quote,a.revision+1
                                FROM normalization.recording_credit_evidence e JOIN normalization.recording_credits a ON a.uuid=e.credit_uuid
                                WHERE a.uuid=@id AND e.credit_revision=a.revision;
                            UPDATE normalization.recording_credits a SET person_uuid=@target,revision=revision+1,basis='reviewed',decision_uuid=@OperationUuid,updated_at=clock_timestamp(),
                                state=CASE WHEN a.state='accepted' AND EXISTS(SELECT 1 FROM normalization.recording_credits b WHERE b.uuid<>a.uuid AND b.state='accepted' AND b.person_uuid=@target
                                    AND b.source_uuid=a.source_uuid AND b.role=a.role AND b.scope_track_uuid IS NOT DISTINCT FROM a.scope_track_uuid) THEN 'superseded' ELSE a.state END
                            WHERE a.uuid=@id
                            """,
                            new
                            {
                                command.OperationUuid,
                                id,
                                target = command.Target.Uuid,
                            },
                            t,
                            cancellationToken: ct
                        )
                    );
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.people SET revision=revision+1,updated_at=clock_timestamp() WHERE uuid=ANY(@ids)",
                        new { ids = new[] { command.OriginalUuid, command.Target.Uuid } },
                        t,
                        cancellationToken: ct
                    )
                );
            },
            cancellationToken
        );
    }

    private sealed class EntityRevision
    {
        public Guid Uuid { get; init; }
        public long Revision { get; init; }
    }
}
