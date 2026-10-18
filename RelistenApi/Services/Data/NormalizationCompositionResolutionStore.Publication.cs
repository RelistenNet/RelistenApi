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

public sealed partial class NormalizationCompositionResolutionStore
{
    internal Task<CompositionResolutionAttempt?> ReadForPublicationAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid attemptUuid,
        CancellationToken ct
    ) => Read(c, t, attemptUuid, false, ct);

    internal static void RequireTicket(
        CompositionResolutionStage stage,
        NormalizationWorkTicket ticket,
        string version
    )
    {
        var expected = stage.ReadSet;
        if (
            ticket.WorkUuid != stage.WorkUuid
            || ticket.PolicyVersion != version
            || !SetEquals(ticket.Inputs, expected.Inputs)
            || !SetEquals(ticket.Dependencies, expected.Dependencies)
            || !SetEquals(ticket.CatalogInputs ?? [], expected.CatalogInputs)
            || !SetEquals(ticket.SourceIdentities ?? [], expected.SourceIdentities)
            || ticket.MappingRegistry != expected.MappingRegistry
            || !SetEquals(ticket.ImportedVenues ?? [], expected.ImportedVenues)
            || !SetEquals(ticket.CandidateUniverses ?? [], expected.CandidateUniverses)
            || !SetEquals(ticket.OwnerObservations ?? [], expected.OwnerObservations)
            || !SetEquals(ticket.SourceUuids ?? [], expected.SourceUuids)
        )
        {
            throw new NormalizationConflictException(
                "Composition publication must consume the complete original stage read set."
            );
        }
    }

    /// <summary>Called under Work locks in the accepted writer's transaction, never as caller-provided authority.</summary>
    internal async Task<CompositionResolutionStage> RequireStageAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid attemptUuid,
        Guid workUuid,
        CompositionResolutionStageKind kind,
        CancellationToken ct
    )
    {
        var attempt = await RequireAttempt(c, t, attemptUuid, ct);
        var stage =
            attempt.Stages.SingleOrDefault(value => value.WorkUuid == workUuid)
            ?? throw new ArgumentException("Unknown composition resolution publication stage.");
        if (
            attempt.Result != null
            || attempt.Preparation == null
            || (
                attempt.Proposal == null
                && kind != CompositionResolutionStageKind.AcknowledgeDecision
            )
            || stage.Kind != kind
            || stage.Ticket == null
            || stage.Receipt != null
        )
        {
            throw new NormalizationConflictException(
                "Composition publication requires its unfinished admitted stage and saved correspondence receipt."
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
        var owner =
            kind == CompositionResolutionStageKind.CreateComposition
                ? new NormalizationSubjectKey(
                    "compositions",
                    "composition",
                    stage.Publication.Compositions.Single().Uuid
                )
                : new NormalizationSubjectKey(
                    "songs",
                    "artist_repertoire",
                    attempt.Preparation.Context.ArtistUuid
                );
        if (
            run == null
            || run.Domain != owner.Domain
            || run.Kind != owner.Kind
            || run.EntityUuid != owner.Uuid
            || run.State != "running"
            || !Same(JsonSerializer.Deserialize<NormalizationWorkTicket>(run.Ticket), stage.Ticket)
        )
        {
            throw new NormalizationConflictException(
                "Composition publication requires its exact running Work owner and ticket."
            );
        }
        RequireTicket(stage, stage.Ticket, attempt.ProcessingVersion);
        // Original provider resource generations survive all later acquisition and creation stages.
        // Derived candidate facts may change after our own creation; original source inputs may not.
        if (!attempt.Preparation.Context.ReadSet.Inputs.All(stage.ReadSet.Inputs.Contains))
        {
            throw new NormalizationConflictException(
                "Composition publication cannot replace its original source input generations."
            );
        }
        return stage;
    }

    internal static void RequireNegativeDecision(
        CompositionResolutionAttempt attempt,
        CompositionResolutionStage stage
    )
    {
        var prepared =
            attempt.Preparation
            ?? throw new NormalizationConflictException(
                "A negative stage requires original preparation."
            );
        var expected = prepared.UnsupportedDecision;
        if (expected == null && attempt.Proposal is { } proposal)
        {
            if (
                proposal.AttemptUuid != attempt.AttemptUuid
                || proposal.Claim != attempt.Claim
                || proposal.ProcessorVersion != attempt.ProcessingVersion
                || (
                    proposal.Decision.Action == CompositionCorrespondenceAction.ProposeAcceptance
                    && proposal.Policy != null
                )
            )
            {
                throw new NormalizationConflictException(
                    "The saved correspondence does not support a negative decision."
                );
            }
            expected = new(
                proposal.Policy == null
                    ? CompositionResolutionDisposition.Unresolved
                    : CompositionResolutionDisposition.Review,
                proposal.Decision.Reason
            );
        }
        if (
            expected == null
            || expected.Disposition
                is not (
                    CompositionResolutionDisposition.Unresolved
                    or CompositionResolutionDisposition.Review
                )
            || stage.Kind != CompositionResolutionStageKind.AcknowledgeDecision
            || stage.NegativeDecision != expected
            || prepared.PolicyState == null
            || JsonSerializer.Serialize(prepared.Policy)
                != JsonSerializer.Serialize(prepared.PolicyState.Active)
            || stage.DerivedContext == null
            || JsonSerializer.Serialize(stage.DerivedContext)
                != JsonSerializer.Serialize(attempt.Proposal?.Context ?? prepared.Context)
            || JsonSerializer.Serialize(stage.Publication)
                != JsonSerializer.Serialize(new MusicPublication())
        )
        {
            throw new NormalizationConflictException(
                "A negative stage must acknowledge its original decision without accepted music changes."
            );
        }
    }

    internal async Task WriteStagePublishedAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid attemptUuid,
        Guid workUuid,
        NormalizationPublicationResult receipt,
        CancellationToken ct
    )
    {
        var json = Serialize(receipt);
        if (
            receipt.WorkUuid != workUuid
            || receipt.Status != NormalizationPublicationStatus.Published
        )
        {
            throw new ArgumentException(
                "A stage receipt must identify its successful Work publication."
            );
        }
        var attempt = await RequireAttempt(c, t, attemptUuid, ct);
        var stage = attempt.Stages.Single(value => value.WorkUuid == workUuid);
        if (attempt.Result != null || stage.Ticket == null)
        {
            throw new NormalizationConflictException(
                "Only an unfinished admitted stage can record publication."
            );
        }
        if (stage.Receipt != null)
        {
            RequireSame(
                stage.Receipt,
                JsonSerializer.Deserialize<NormalizationPublicationResult>(json)!,
                "A stage publication receipt is immutable."
            );
            return;
        }
        await c.ExecuteAsync(
            new CommandDefinition(
                "UPDATE normalization.composition_resolution_stages SET receipt=@json::jsonb WHERE attempt_uuid=@attemptUuid AND work_uuid=@workUuid AND receipt IS NULL",
                new
                {
                    attemptUuid,
                    workUuid,
                    json,
                },
                t,
                cancellationToken: ct
            )
        );
    }

    private static bool SetEquals<T>(IEnumerable<T> left, IEnumerable<T> right) =>
        left.ToHashSet().SetEquals(right);

    private sealed record WorkRow(
        string Domain,
        string Kind,
        Guid EntityUuid,
        string State,
        string Ticket
    );
}
