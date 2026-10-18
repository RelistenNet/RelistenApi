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

public sealed partial class PeopleAcceptanceApplicationStore
{
    internal Task<PeopleAcceptanceStage> RequireIdentityStageAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid applicationUuid,
        Guid workUuid,
        Guid personUuid,
        CancellationToken ct
    ) => RequireStage(c, t, applicationUuid, workUuid, personUuid, ct);

    internal Task<PeopleAcceptanceStage> RequireCreditStageAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid applicationUuid,
        Guid workUuid,
        CancellationToken ct
    ) => RequireStage(c, t, applicationUuid, workUuid, null, ct);

    private static async Task<PeopleAcceptanceStage> RequireStage(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid applicationUuid,
        Guid workUuid,
        Guid? personUuid,
        CancellationToken ct
    )
    {
        var app =
            await Read(c, t, applicationUuid, true, ct)
            ?? throw new ArgumentException("Unknown people acceptance application.");
        var stage =
            app.Stages.SingleOrDefault(value => value.WorkUuid == workUuid)
            ?? throw new ArgumentException("Unknown people acceptance stage.");
        if (
            app.Result != null
            || stage.PersonUuid != personUuid
            || stage.Ticket == null
            || stage.Receipt != null
            || app.Plan == null
        )
        {
            throw new NormalizationConflictException(
                "Acceptance requires its unfinished admitted stage."
            );
        }
        var run = await c.QuerySingleOrDefaultAsync<WorkRow>(
            new CommandDefinition(
                "SELECT s.domain AS \"Domain\",s.subject_kind AS \"Kind\",s.subject_uuid AS \"EntityUuid\",r.state AS \"State\",r.ticket::text AS \"Ticket\" FROM normalization.work_runs r JOIN normalization.work_subjects s ON s.uuid=r.subject_uuid WHERE r.uuid=@workUuid",
                new { workUuid },
                t,
                cancellationToken: ct
            )
        );
        if (
            run == null
            || run.Domain != "people"
            || run.Kind != (personUuid.HasValue ? "person" : "recording")
            || run.EntityUuid != (personUuid ?? app.SourceUuid)
            || run.State != "running"
            || !Same(JsonSerializer.Deserialize<NormalizationWorkTicket>(run.Ticket), stage.Ticket)
        )
        {
            throw new NormalizationConflictException(
                "Acceptance publication requires its exact Work owner and ticket."
            );
        }
        PeopleAcceptanceReadSet.RequireTicket(stage, stage.Ticket, app.SourceUuid, app.Version);
        // Original resource generations fence A→B→A input changes; equal retained bytes alone
        // are not permission to replace the proposal's original provider observation.
        var originalRevisions = app
            .Plan.Proposal.Ticket.Inputs.Select(input => input.RevisionUuid)
            .ToHashSet();
        if (
            !stage
                .Ticket.Inputs.Where(input => originalRevisions.Contains(input.RevisionUuid))
                .ToHashSet()
                .SetEquals(app.Plan.Proposal.Ticket.Inputs)
        )
        {
            throw new NormalizationConflictException(
                "Acceptance must retain the proposal's original input generations."
            );
        }
        await RequireDecisionPeerReceiptAsync(c, t, stage, ct);
        if (
            stage.DecisionOnly
            && (
                stage.PolicyState == null
                || !Same(stage.PolicyState, app.Plan.PolicyState)
                || !Same(stage.PolicyState.Active, stage.Policy)
                || !(stage.Ticket.OwnerObservations ?? []).Contains(
                    stage.PolicyState.OwnerObservation
                )
            )
        )
        {
            throw new NormalizationConflictException(
                "Decision acknowledgment requires its original registry state."
            );
        }
        if (stage.Reconciliation is { } reconciliation)
        {
            foreach (var replacement in reconciliation.Contributors)
            {
                foreach (var scope in replacement.CompleteScopes)
                {
                    var coverage = stage.ScopeCoverage?.SingleOrDefault(value =>
                        value.Contributor == replacement.Contributor && value.Scope == scope
                    );
                    if (
                        coverage is not { Complete: true }
                        || coverage.Fields.Count == 0
                        || coverage.Fields.Any(field =>
                            !stage
                                .Facts.Inputs.Single(input =>
                                    input.OriginalInput.Contributor == replacement.Contributor
                                )
                                .OriginalInput.Binding.Inputs.Any(input =>
                                    input.InputRevisionUuid == field.InputRevisionUuid
                                )
                        )
                    )
                    {
                        throw new ArgumentException(
                            "Contributor retirement requires its saved current-field coverage proof."
                        );
                    }
                }
            }
        }
        return stage;
    }

    private static async Task RequireDecisionPeerReceiptAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        PeopleAcceptanceStage stage,
        CancellationToken ct
    )
    {
        if (stage.PeerTicket is not { } peer)
        {
            return;
        }
        var ticket = stage.Ticket!;
        var savedPeer = await ReadPublishedTicket(c, t, peer.WorkUuid, ticket.SubjectUuid, ct);
        if (
            !stage.DecisionOnly
            || ticket.PreviousPublishedWorkUuid != peer.WorkUuid
            || savedPeer == null
            || !Same(savedPeer, peer)
        )
        {
            throw new NormalizationConflictException(
                "Decision peers require their immutable published owner receipt."
            );
        }
    }

    internal async Task WriteStagePublishedAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid applicationUuid,
        Guid workUuid,
        IReadOnlyList<PeopleAcceptanceCreditWriteOutcome>? credits,
        CancellationToken ct
    )
    {
        var app = (await Read(c, t, applicationUuid, true, ct))!;
        var stage = app.Stages.Single(value => value.WorkUuid == workUuid);
        var receipt = new PeopleAcceptanceStageReceipt(workUuid, stage.PersonUuid, credits ?? []);
        await c.ExecuteAsync(
            new CommandDefinition(
                "UPDATE normalization.people_acceptance_stages SET receipt=@json::jsonb WHERE application_uuid=@applicationUuid AND work_uuid=@workUuid AND receipt IS NULL",
                new
                {
                    applicationUuid,
                    workUuid,
                    json = Serialize(receipt),
                },
                t,
                cancellationToken: ct
            )
        );
    }

    private sealed record WorkRow(
        string Domain,
        string Kind,
        Guid EntityUuid,
        string State,
        string Ticket
    );
}
