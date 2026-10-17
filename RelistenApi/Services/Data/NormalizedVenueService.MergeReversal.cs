using System;
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
                "venues",
                "venue",
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
                await NormalizedPeopleService.ValidateOwnerAsync(
                    c,
                    t,
                    command.SubjectUuid,
                    "venues",
                    "venue",
                    command.OriginalUuid,
                    ct
                );
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
                            "SELECT uuid AS \"Uuid\",revision AS \"Revision\" FROM normalization.canonical_venues WHERE uuid=ANY(@ids) ORDER BY uuid FOR UPDATE",
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
                            "SELECT EXISTS(SELECT 1 FROM normalization.venue_redirects WHERE retired_uuid=@OriginalUuid AND survivor_uuid=@SurvivorUuid AND decision_uuid=@OriginalMergeOperationUuid)",
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
                await ReverseAssignmentMoves(c, t, command, false, ct);
                await ReverseAssignmentMoves(c, t, command, true, ct);
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "DELETE FROM normalization.venue_redirects WHERE retired_uuid=@OriginalUuid; UPDATE normalization.canonical_venues SET revision=revision+1,updated_at=clock_timestamp() WHERE uuid IN (@OriginalUuid,@SurvivorUuid)",
                        command,
                        t,
                        cancellationToken: ct
                    )
                );
            },
            cancellationToken
        );
    }

    private static async Task ReverseAssignmentMoves(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        IdentityMergeReversal command,
        bool isShow,
        CancellationToken ct
    )
    {
        var table = isShow ? "show_venue_assignments" : "imported_venue_assignments";
        var evidenceColumn = isShow ? "show_assignment_uuid" : "imported_assignment_uuid";
        var moves = (
            await c.QueryAsync<AssignmentMove>(
                new CommandDefinition(
                    $"SELECT {evidenceColumn} AS \"AssignmentUuid\",after_revision AS \"AfterRevision\" FROM normalization.venue_assignment_moves WHERE decision_uuid=@OriginalMergeOperationUuid AND from_venue_uuid=@OriginalUuid AND to_venue_uuid=@SurvivorUuid AND {evidenceColumn} IS NOT NULL ORDER BY {evidenceColumn}",
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
                        $"SELECT revision=@AfterRevision AND canonical_venue_uuid=@SurvivorUuid FROM normalization.{table} WHERE uuid=@AssignmentUuid FOR UPDATE",
                        new
                        {
                            move.AfterRevision,
                            move.AssignmentUuid,
                            command.SurvivorUuid,
                        },
                        t,
                        cancellationToken: ct
                    )
                )
            )
            {
                throw new NormalizationConflictException(
                    "A moved assignment was corrected after merge; reversal cannot overwrite it."
                );
            }
            await c.ExecuteAsync(
                new CommandDefinition(
                    $"""
                    INSERT INTO normalization.{table}_history SELECT *,@OperationUuid FROM normalization.{table} WHERE uuid=@AssignmentUuid;
                    INSERT INTO normalization.venue_assignment_moves(decision_uuid,{evidenceColumn},from_venue_uuid,to_venue_uuid,before_revision,after_revision)
                        SELECT @OperationUuid,uuid,canonical_venue_uuid,@OriginalUuid,revision,revision+1 FROM normalization.{table} WHERE uuid=@AssignmentUuid;
                    INSERT INTO normalization.venue_assignment_evidence({evidenceColumn},input_revision_uuid,json_pointer,start_code_point,end_code_point,quote,assignment_revision)
                        SELECT e.{evidenceColumn},e.input_revision_uuid,e.json_pointer,e.start_code_point,e.end_code_point,e.quote,a.revision+1
                        FROM normalization.venue_assignment_evidence e JOIN normalization.{table} a ON a.uuid=e.{evidenceColumn}
                        WHERE a.uuid=@AssignmentUuid AND e.assignment_revision=a.revision;
                    UPDATE normalization.{table} SET canonical_venue_uuid=@OriginalUuid,revision=revision+1,basis='reviewed',decision_uuid=@OperationUuid,updated_at=clock_timestamp() WHERE uuid=@AssignmentUuid
                    """,
                    new
                    {
                        command.OperationUuid,
                        command.OriginalUuid,
                        move.AssignmentUuid,
                    },
                    t,
                    cancellationToken: ct
                )
            );
        }
    }

    private sealed class AssignmentMove
    {
        public Guid AssignmentUuid { get; init; }
        public long AfterRevision { get; init; }
    }
}
