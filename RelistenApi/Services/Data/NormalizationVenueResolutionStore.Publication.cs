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

public sealed partial class NormalizationVenueResolutionStore
{
    internal Task<VenueResolutionAttempt?> ReadForPublicationAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid attemptUuid,
        CancellationToken ct
    ) => Read(c, t, attemptUuid, false, ct);

    internal static void RequireTicket(
        VenueResolutionStage stage,
        NormalizationWorkTicket ticket,
        string version
    )
    {
        var expected = stage.ReadSet;
        if (
            ticket.WorkUuid != stage.WorkUuid
            || ticket.PolicyVersion != version
            || (
                stage.OwnerMembership.Owner != null
                && ticket.SubjectUuid != stage.OwnerMembership.Owner.SubjectUuid
            )
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
                "Venue publication must consume its complete original stage read set and owner."
            );
        }
    }

    /// <summary>Runs under Work locks in the accepted writer transaction, never on caller-supplied proof JSON.</summary>
    internal async Task<VenueResolutionStage> RequireStageAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid attemptUuid,
        Guid workUuid,
        VenueResolutionStageKind kind,
        CancellationToken ct
    )
    {
        var attempt = await RequireAttempt(c, t, attemptUuid, ct);
        var stage =
            attempt.Stages.SingleOrDefault(value => value.WorkUuid == workUuid)
            ?? throw new ArgumentException("Unknown venue resolution publication stage.");
        if (
            attempt.Result != null
            || attempt.Preparation == null
            || (
                attempt.Decision == null
                && kind
                    is not (
                        VenueResolutionStageKind.AcknowledgeMissing
                        or VenueResolutionStageKind.AcknowledgeDecision
                        or VenueResolutionStageKind.SupersedeObservation
                    )
            )
            || stage.Kind != kind
            || stage.Ticket == null
            || stage.Receipt != null
        )
        {
            throw new NormalizationConflictException(
                "Venue publication requires its unfinished admitted stage and saved decision."
            );
        }
        RequireStageShape(attempt, stage);
        var run = await c.QuerySingleOrDefaultAsync<WorkRow>(
            new CommandDefinition(
                "SELECT s.domain AS \"Domain\",s.subject_kind AS \"Kind\",s.subject_uuid AS \"EntityUuid\",r.state AS \"State\",r.ticket::text AS \"Ticket\" FROM normalization.work_runs r JOIN normalization.work_subjects s ON s.uuid=r.subject_uuid WHERE r.uuid=@workUuid",
                new { workUuid },
                t,
                cancellationToken: ct
            )
        );
        var owner = StageOwner(attempt, stage);
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
                "Venue publication requires its exact running Work owner and ticket."
            );
        }
        RequireTicket(stage, stage.Ticket, attempt.ProcessingVersion);
        return stage;
    }

    private static NormalizationSubjectKey StageOwner(
        VenueResolutionAttempt attempt,
        VenueResolutionStage stage
    ) =>
        stage.Kind switch
        {
            VenueResolutionStageKind.CreateVenue => new(
                "venues",
                "venue",
                stage.Publication.Venues.Single().Uuid
            ),
            VenueResolutionStageKind.AcknowledgeMissing => new(
                "venues",
                "imported_venue",
                attempt.Request.ImportedVenueUuid!.Value
            ),
            VenueResolutionStageKind.SupersedeObservation => VenueResolutionOwner.ForObservation(
                attempt.Preparation!.Source
            ),
            VenueResolutionStageKind.AcknowledgeDecision => VenueResolutionOwner.For(
                attempt.Request,
                attempt.Preparation!.Source
            ),
            _ => new(
                "venues",
                attempt.Preparation!.Source.Claim!.IsShow ? "show" : "imported_venue",
                attempt.Preparation.Source.Claim.CatalogUuid!.Value
            ),
        };

    private static void RequireStageShape(
        VenueResolutionAttempt attempt,
        VenueResolutionStage stage
    )
    {
        var preparation = attempt.Preparation!;
        if (!Enum.IsDefined(stage.Kind))
        {
            throw new NormalizationConflictException("Unknown venue publication stage kind.");
        }
        if (stage.Kind == VenueResolutionStageKind.SupersedeObservation)
        {
            RequireObservationRetirementShape(attempt, stage);
        }
        else if (stage.Kind == VenueResolutionStageKind.AcknowledgeMissing)
        {
            if (
                attempt.Request.Kind != VenueResolutionRequestKind.ImportedVenue
                || preparation.Source.Claim != null
                || preparation.Source.Absence is not { Exists: false } absence
                || absence.ImportedVenueUuid != attempt.Request.ImportedVenueUuid
                || !stage.ReadSet.ImportedVenues.Contains(
                    new(absence.ImportedVenueUuid, absence.Generation)
                )
                || !Same(stage.Publication, new VenuePublication([], [], [], [], [], []))
            )
            {
                throw new NormalizationConflictException(
                    "Missing acknowledgment requires its original absence clock and empty publication."
                );
            }
        }
        else if (stage.Kind == VenueResolutionStageKind.AcknowledgeDecision)
        {
            if (
                preparation.Source.Claim == null
                || !preparation.Context.SourceCurrent
                || stage.DerivedContext != null
                || !Same(stage.DecisionReadSet, preparation.ReadSet)
                || !Same(stage.Publication, new VenuePublication([], [], [], [], [], []))
                || (
                    attempt.Decision != null
                    && attempt.Decision.Decision.Action != VenueCorrespondenceAction.NeedsReview
                )
                || (
                    preparation.Status == VenueResolutionPreparationStatus.Ready
                        ? attempt.Decision?.Decision.Action != VenueCorrespondenceAction.NeedsReview
                        : preparation.Status
                            is not (
                                VenueResolutionPreparationStatus.SourceUnbound
                                or VenueResolutionPreparationStatus.UnsupportedQuery
                            )
                )
            )
            {
                throw new NormalizationConflictException(
                    "Decision acknowledgment requires its original unresolved or evaluated negative source and empty publication."
                );
            }
        }
        else if (
            preparation.Source.Claim?.CatalogUuid == null
            || attempt.Decision == null
            || !Same(stage.DecisionReadSet, attempt.Decision.ReadSet)
        )
        {
            throw new NormalizationConflictException(
                "Venue publication requires its bound original claim and decision read set."
            );
        }
        if (
            stage.Kind == VenueResolutionStageKind.CreateVenue
            && (
                stage.Publication.Venues.Count != 1
                || stage.Publication.ImportedVenues.Count != 0
                || stage.Publication.Shows.Count != 0
            )
        )
        {
            throw new NormalizationConflictException(
                "Creation publishes one venue identity without assignments."
            );
        }
        if (
            (
                stage.OwnerMembership.Owner != null
                && stage.OwnerMembership.Owner.Subject != StageOwner(attempt, stage)
            )
            || stage.Kind != VenueResolutionStageKind.SupersedeObservation
                && !preparation.ReadSet.Inputs.All(stage.ReadSet.Inputs.Contains)
        )
        {
            throw new NormalizationConflictException(
                "Venue stages preserve the original source generations and exact publication owner."
            );
        }
    }

    internal async Task WriteStagePublishedAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid attemptUuid,
        Guid workUuid,
        NormalizationPublicationResult receipt,
        CancellationToken ct,
        VenueResolutionAssignmentOutcome? outcome = null
    )
    {
        var json = Serialize(receipt);
        var outcomeJson = outcome == null ? null : Serialize(outcome);
        var frozenOutcome =
            outcomeJson == null
                ? null
                : JsonSerializer.Deserialize<VenueResolutionAssignmentOutcome>(outcomeJson);
        if (
            receipt.WorkUuid != workUuid
            || receipt.Status != NormalizationPublicationStatus.Published
        )
        {
            throw new ArgumentException(
                "A stage receipt identifies its successful Work publication."
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
        if (
            stage.Kind == VenueResolutionStageKind.Assignment
            && (
                frozenOutcome == null
                || frozenOutcome.TargetVenueUuid != attempt.Decision!.TargetVenueUuid
            )
        )
        {
            throw new NormalizationConflictException(
                "Assignment publication requires its observed accepted and pending outcome."
            );
        }
        if (stage.Receipt != null)
        {
            RequireSame(stage.Outcome, frozenOutcome, "A stage assignment outcome is immutable.");
            RequireSame(
                stage.Receipt,
                JsonSerializer.Deserialize<NormalizationPublicationResult>(json)!,
                "A stage publication receipt is immutable."
            );
            return;
        }
        await c.ExecuteAsync(
            new CommandDefinition(
                "UPDATE normalization.venue_resolution_stages SET receipt=@json::jsonb,outcome=@outcomeJson::jsonb WHERE attempt_uuid=@attemptUuid AND work_uuid=@workUuid AND receipt IS NULL",
                new
                {
                    attemptUuid,
                    workUuid,
                    json,
                    outcomeJson,
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
