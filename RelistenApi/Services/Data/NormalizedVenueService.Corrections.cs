using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedVenueService
{
    public async Task<NormalizationReviewResult> ApplyIdentityDecisionAsync(
        VenueIdentityDecision command,
        string trustedOperator,
        CancellationToken cancellationToken = default
    )
    {
        var targetOwner = await db.WithWriteConnection(c =>
            NormalizedPeopleService.FindIdentityOwner(
                (Npgsql.NpgsqlConnection)c,
                null,
                "venues",
                "venue",
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
                await NormalizedPeopleService.ValidateOwnerAsync(
                    c,
                    t,
                    command.SubjectUuid,
                    "venues",
                    "venue",
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
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "SELECT pg_advisory_xact_lock(hashtextextended('normalization:venues:identity',0))",
                        transaction: t,
                        cancellationToken: ct
                    )
                );
                var rows = (
                    await c.QueryAsync<VenueRevision>(
                        new CommandDefinition(
                            "SELECT uuid AS \"Uuid\",revision AS \"Revision\",kind AS \"Kind\",parent_venue_uuid AS \"ParentVenueUuid\" FROM normalization.canonical_venues WHERE uuid=ANY(@ids) ORDER BY uuid FOR UPDATE",
                            new { ids = new[] { command.OriginalUuid, command.Target.Uuid } },
                            t,
                            cancellationToken: ct
                        )
                    )
                ).ToArray();
                var original = rows.SingleOrDefault(r => r.Uuid == command.OriginalUuid);
                if (original?.Revision != command.ExpectedOriginalRevision)
                {
                    throw new NormalizationConflictException(
                        "Original venue changed after review was prepared."
                    );
                }
                if (command.Action == "split")
                {
                    if (rows.Any(r => r.Uuid == command.Target.Uuid))
                    {
                        throw new NormalizationConflictException(
                            "Split requires a new target UUID."
                        );
                    }
                    await InsertVenue(c, t, command.Target, ct);
                }
                else
                {
                    var target = rows.SingleOrDefault(r => r.Uuid == command.Target.Uuid);
                    if (target?.Revision != command.ExpectedTargetRevision)
                    {
                        throw new NormalizationConflictException(
                            "Merge survivor changed after review was prepared."
                        );
                    }
                    if (
                        original!.Kind != target!.Kind
                        || original.ParentVenueUuid != target.ParentVenueUuid
                    )
                    {
                        throw new NormalizationConflictException(
                            "A site/room or different room parents cannot be merged."
                        );
                    }
                    if (
                        await c.ExecuteScalarAsync<bool>(
                            new CommandDefinition(
                                """
                                SELECT EXISTS(SELECT 1 FROM normalization.venue_external_identities a JOIN normalization.venue_external_identities b
                                    ON a.provider=b.provider AND a.resource_type=b.resource_type AND a.provider_key<>b.provider_key
                                    WHERE a.venue_uuid=@OriginalUuid AND b.venue_uuid=@target AND a.state='accepted' AND b.state='accepted')
                                """,
                                new { command.OriginalUuid, target = command.Target.Uuid },
                                t,
                                cancellationToken: ct
                            )
                        )
                    )
                    {
                        throw new NormalizationConflictException(
                            "Contradictory provider identities require correction before merge."
                        );
                    }
                    // Rooms retain their historical parent UUID. The parent's durable redirect
                    // supplies the current physical site without rewriting room identity/history.
                }
                if (
                    await c.ExecuteScalarAsync<bool>(
                        new CommandDefinition(
                            "SELECT EXISTS(SELECT 1 FROM normalization.venue_redirects WHERE retired_uuid=ANY(@ids))",
                            new { ids = new[] { command.OriginalUuid, command.Target.Uuid } },
                            t,
                            cancellationToken: ct
                        )
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "Use current survivor identities for corrections."
                    );
                }
                await MoveAssignments(c, t, command, false, command.ImportedAssignmentUuids, ct);
                await MoveAssignments(c, t, command, true, command.ShowAssignmentUuids, ct);
                if (command.Action == "merge")
                {
                    await c.ExecuteAsync(
                        new CommandDefinition(
                            "INSERT INTO normalization.venue_redirects(retired_uuid,survivor_uuid,decision_uuid) VALUES(@OriginalUuid,@target,@OperationUuid)",
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
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.canonical_venues SET revision=revision+1,updated_at=clock_timestamp() WHERE uuid=ANY(@ids)",
                        new { ids = new[] { command.OriginalUuid, command.Target.Uuid } },
                        t,
                        cancellationToken: ct
                    )
                );
            },
            cancellationToken,
            candidateWriteScopes: IdentityCandidateWriteScopes
        );
    }

    private static async Task MoveAssignments(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        VenueIdentityDecision command,
        bool isShow,
        IReadOnlyList<Guid> selectedIds,
        CancellationToken ct
    )
    {
        var table = isShow ? "show_venue_assignments" : "imported_venue_assignments";
        var evidenceColumn = isShow ? "show_assignment_uuid" : "imported_assignment_uuid";
        var actual = (
            await c.QueryAsync<Guid>(
                new CommandDefinition(
                    $"SELECT uuid FROM normalization.{table} WHERE canonical_venue_uuid=@OriginalUuid ORDER BY uuid FOR UPDATE",
                    command,
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var selected = selectedIds.Distinct().Order().ToArray();
        if (
            selected.Any(id => !actual.Contains(id))
            || (command.Action == "merge" && !selected.SequenceEqual(actual.Order()))
        )
        {
            throw new NormalizationConflictException(
                "Correction must explicitly list current assignments to move; merge requires all assignments."
            );
        }
        foreach (var id in selected)
        {
            await c.ExecuteAsync(
                new CommandDefinition(
                    $"""
                    INSERT INTO normalization.{table}_history SELECT *,@OperationUuid FROM normalization.{table} WHERE uuid=@id;
                    INSERT INTO normalization.venue_assignment_moves(decision_uuid,{evidenceColumn},from_venue_uuid,to_venue_uuid,before_revision,after_revision)
                        SELECT @OperationUuid,uuid,canonical_venue_uuid,@target,revision,revision+1 FROM normalization.{table} WHERE uuid=@id;
                    INSERT INTO normalization.venue_assignment_evidence({evidenceColumn},input_revision_uuid,json_pointer,start_code_point,end_code_point,quote,assignment_revision)
                        SELECT e.{evidenceColumn},e.input_revision_uuid,e.json_pointer,e.start_code_point,e.end_code_point,e.quote,a.revision+1
                        FROM normalization.venue_assignment_evidence e JOIN normalization.{table} a ON a.uuid=e.{evidenceColumn}
                        WHERE a.uuid=@id AND e.assignment_revision=a.revision;
                    UPDATE normalization.{table} SET canonical_venue_uuid=@target,revision=revision+1,basis='reviewed',decision_uuid=@OperationUuid,updated_at=clock_timestamp() WHERE uuid=@id
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
    }

    private sealed class VenueRevision
    {
        public Guid Uuid { get; init; }
        public long Revision { get; init; }
        public string Kind { get; init; } = null!;
        public Guid? ParentVenueUuid { get; init; }
    }
}
