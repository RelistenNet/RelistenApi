using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationVenueResolutionProcessor
{
    private async Task<VenueResolutionAttempt> PrepareMissingStageAsync(
        VenueResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        var preparation = attempt.Preparation!;
        var read = MergeVenueReadSets([
            preparation.ReadSet,
            VenuePeerCoordinationReadSet(
                preparation.Context,
                attempt.Request.ImportedVenueUuid!.Value
            ),
        ]);
        return await store.SaveStageAsync(
            attempt.AttemptUuid,
            new(
                StableUuid(attempt.AttemptUuid, "missing-work"),
                VenueResolutionStageKind.AcknowledgeMissing,
                EmptyPublication(),
                read,
                preparation.ReadSet,
                preparation.Context.OwnerMembership ?? new(null, []),
                await ReadStageInputReferencesAsync(read.Inputs, ct)
            ),
            ct
        );
    }

    private async Task<VenueResolutionAttempt> PrepareObservationRetirementAsync(
        VenueResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        var preparation = attempt.Preparation!;
        var subscription = preparation.Context.ObservationSubscription!;
        var owner = VenueResolutionOwner.ForObservation(preparation.Source);
        var previous = preparation.Context with
        {
            OwnerMembership = subscription.Membership,
            OwnerBindings = subscription.Bindings,
            OwnerInputReferences = subscription.InputReferences,
        };
        // The bound stage has already enrolled the new authenticated source under its show.
        // Retire only our obsolete watcher. No old raw clock is refreshed or reinterpreted;
        // the callback authenticates that immutable handoff receipt before clearing these keys.
        var read = VenuePeerCoordinationReadSet(previous, owner.Uuid);
        return await store.SaveStageAsync(
            attempt.AttemptUuid,
            new(
                StableUuid(attempt.AttemptUuid, "retire-observation-work"),
                VenueResolutionStageKind.SupersedeObservation,
                EmptyPublication(),
                read,
                preparation.ReadSet,
                subscription.Membership,
                await ReadStageInputReferencesAsync(read.Inputs, ct)
            ),
            ct
        );
    }

    private async Task<VenueResolutionAttempt> PrepareNegativeStageAsync(
        VenueResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        var preparation = attempt.Preparation!;
        var owner = VenueResolutionOwner.For(attempt.Request, preparation.Source);
        var read = MergeVenueReadSets([
            preparation.ReadSet,
            VenuePeerCoordinationReadSet(preparation.Context, owner.Uuid),
        ]);
        return await store.SaveStageAsync(
            attempt.AttemptUuid,
            new(
                StableUuid(attempt.AttemptUuid, "decision-work"),
                VenueResolutionStageKind.AcknowledgeDecision,
                EmptyPublication(),
                read,
                preparation.ReadSet,
                preparation.Context.OwnerMembership ?? new(null, []),
                await ReadStageInputReferencesAsync(read.Inputs, ct)
            ),
            ct
        );
    }

    private async Task<VenueResolutionAttempt> PrepareStageAsync(
        VenueResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        var proof = attempt.Decision!;
        if (proof.Decision.Action == VenueCorrespondenceAction.NeedsReview || proof.Policy == null)
        {
            return await PrepareNegativeStageAsync(attempt, ct);
        }
        var created = attempt.Stages.Any(stage =>
            stage.Kind == VenueResolutionStageKind.CreateVenue
        );
        var create = proof.Decision.Action == VenueCorrespondenceAction.ProposeCreation && !created;
        var context = attempt.Preparation!.Context;
        if (created)
        {
            var source = attempt.Preparation.Source;
            context = source.Claim!.IsShow
                ? await candidates.FindShowVenueResolutionContextAsync(
                    source,
                    Query(source.Claim.Observation),
                    cancellationToken: ct
                )
                : await candidates.FindImportedVenueResolverContextAsync(
                    source.Claim.CatalogUuid!.Value,
                    Query(source.Claim.Observation),
                    cancellationToken: ct
                );
            RequireCreatedVenueContext(attempt, context);
        }
        var decisionRead = create
            ? proof.ReadSet
            : AssignmentDecisionReadSet(attempt, created ? context : null);
        var read = create
            ? decisionRead
            : MergeVenueReadSets([
                decisionRead,
                VenuePeerCoordinationReadSet(context, proof.Claim.CatalogUuid!.Value),
            ]);
        var stage = new VenueResolutionStage(
            StableUuid(attempt.AttemptUuid, create ? "create-work" : "assignment-work"),
            create ? VenueResolutionStageKind.CreateVenue : VenueResolutionStageKind.Assignment,
            create ? CreationPublication(proof) : AssignmentPublication(proof),
            read,
            proof.ReadSet,
            create ? new(null, []) : context.OwnerMembership ?? new(null, []),
            await ReadStageInputReferencesAsync(read.Inputs, ct),
            DerivedContext: created ? context : null
        );
        return await store.SaveStageAsync(attempt.AttemptUuid, stage, ct);
    }

    private static void RequireCreatedVenueContext(
        VenueResolutionAttempt attempt,
        VenueResolutionContext context
    )
    {
        var proof = attempt.Decision!;
        var original = attempt.Preparation!.Context;
        var creation = proof.Decision.Creation!;
        var target = context.Candidates.Facts.SingleOrDefault(value =>
            value.Header.Uuid == proof.TargetVenueUuid
        );
        if (
            !context.Complete
            || target == null
            || target.Header.DisplayText != creation.DisplayName
            || target.Header.Kind != creation.Kind
            || !target.Names.Any(value =>
                value.Wording == creation.DisplayName
                && value.Provenance.Evidence.Contains(creation.IdentityEvidence)
            )
            || !NormalizationVenueResolutionStore.Same(original.Assignments, context.Assignments)
            || !context
                .Candidates.Facts.Select(value => value.Header.Uuid)
                .ToHashSet()
                .SetEquals(
                    original
                        .Candidates.Facts.Select(value => value.Header.Uuid)
                        .Append(proof.TargetVenueUuid!.Value)
                )
        )
        {
            throw new NormalizationConflictException(
                "Post-creation venue alternatives or assignment authority changed."
            );
        }
        // Only current accepted candidate facts may be reread after our own identity write.
        // The original raw claim, binding, policy observation and source proposal stay frozen.
        foreach (var previous in original.Candidates.Facts)
        {
            if (
                !NormalizationVenueResolutionStore.Same(
                    previous,
                    context.Candidates.Facts.SingleOrDefault(value =>
                        value.Header.Uuid == previous.Header.Uuid
                    )
                )
            )
            {
                throw new NormalizationConflictException(
                    "A venue alternative changed after creation."
                );
            }
        }
    }

    private static VenuePublication AssignmentPublication(VenueResolutionDecision proof)
    {
        // An already accepted target is acknowledged under its exact owner/row observations;
        // a new processor attempt does not replace its original authority or citations.
        if (proof.ExpectedAssignmentRevision > 0)
        {
            return EmptyPublication();
        }
        var evidence =
            proof.Decision.Action == VenueCorrespondenceAction.ProposeCreation
                ? new[] { proof.Decision.Creation!.IdentityEvidence, proof.Claim.Evidence }
                    .Distinct()
                    .ToArray()
                : proof
                    .Decision.Candidates.Single(candidate =>
                        candidate.VenueUuid == proof.TargetVenueUuid
                    )
                    .Evidence.Append(proof.Claim.Evidence)
                    .Distinct()
                    .ToArray();
        var assignment = new VenueAssignment(
            proof.AssignmentUuid,
            proof.Claim.CatalogUuid!.Value,
            proof.TargetVenueUuid!.Value,
            evidence,
            "accepted",
            "validated_match",
            "evaluated_venue_correspondence"
        );
        return new(
            [],
            [],
            [],
            [],
            proof.Claim.IsShow ? [] : [assignment],
            proof.Claim.IsShow ? [assignment] : []
        );
    }

    private static VenuePublication CreationPublication(VenueResolutionDecision proof)
    {
        var creation = proof.Decision.Creation!;
        var source = proof.Claim.Observation;
        var uuid = proof.TargetVenueUuid!.Value;
        var location =
            source.OriginalLocality is { } locality && !string.IsNullOrWhiteSpace(locality.Value)
                ? new VenueLocationAssertion(
                    StableUuid(proof.AttemptUuid, "location"),
                    uuid,
                    locality.Value,
                    source.Country?.Value,
                    source.Region?.Value,
                    source.City?.Value,
                    source.Address?.Value,
                    source.CoordinateKind == ObservedCoordinateKind.Venue
                        ? source.Latitude?.Value
                        : null,
                    source.CoordinateKind == ObservedCoordinateKind.Venue
                        ? source.Longitude?.Value
                        : null,
                    source.CoordinateKind == ObservedCoordinateKind.Venue ? "provider_venue" : null,
                    null,
                    null,
                    locality.Evidence
                )
                : null;
        return new(
            [new(uuid, creation.DisplayName, creation.Kind, creation.ParentTarget?.VenueUuid)],
            [
                new(
                    StableUuid(proof.AttemptUuid, "name"),
                    uuid,
                    creation.DisplayName,
                    null,
                    null,
                    null,
                    creation.IdentityEvidence
                ),
            ],
            location == null ? [] : [location],
            [],
            [],
            []
        );
    }

    private static VenuePublication EmptyPublication() => new([], [], [], [], [], []);
}
