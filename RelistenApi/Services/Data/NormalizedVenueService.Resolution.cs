using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedVenueService
{
    /// <summary>Publish only a processor-owned saved stage; caller-held decisions are not authority.</summary>
    public async Task<NormalizationPublicationResult> PublishVenueResolutionAsync(
        Guid workUuid,
        Guid resolutionAttemptUuid,
        CancellationToken ct = default
    )
    {
        var store = new NormalizationVenueResolutionStore(db);
        var original =
            await store.GetAsync(resolutionAttemptUuid, ct)
            ?? throw new ArgumentException("Unknown venue resolution attempt.");
        var requested = original.Stages.SingleOrDefault(stage => stage.WorkUuid == workUuid);
        // Terminal Work replay skips the callback, so bind the request to its saved stage first.
        if (requested?.Ticket == null)
        {
            throw new NormalizationConflictException(
                "Publication requires this resolution's admitted stage."
            );
        }
        return await work.TryPublishAsync(
            workUuid,
            async (c, t, token) =>
            {
                var stage = await store.RequireStageAsync(
                    c,
                    t,
                    resolutionAttemptUuid,
                    workUuid,
                    requested.Kind,
                    token
                );
                var attempt = (
                    await store.ReadForPublicationAsync(c, t, resolutionAttemptUuid, token)
                )!;
                VenueResolutionAssignmentOutcome? outcome = null;
                if (stage.Kind == VenueResolutionStageKind.SupersedeObservation)
                {
                    await NormalizationVenueResolutionStore.RequireObservationHandoffAsync(
                        c,
                        t,
                        attempt,
                        token
                    );
                }
                else if (stage.Kind == VenueResolutionStageKind.AcknowledgeDecision)
                {
                    RequireVenueNegativeAcknowledgment(attempt, stage);
                    await VenueResolutionPolicyService.RequireStateAsync(
                        c,
                        t,
                        new(
                            attempt.Preparation!.Policy,
                            attempt.Preparation.PolicyOwnerObservation!
                        ),
                        token
                    );
                }
                else if (stage.Kind != VenueResolutionStageKind.AcknowledgeMissing)
                {
                    RequireVenueResolutionProof(attempt, stage);
                    await VenueResolutionPolicyService.RequireActiveAsync(
                        c,
                        t,
                        attempt.Decision!.Policy!,
                        token
                    );
                    var acceptedEvidence = attempt
                        .Decision.Facts.Candidates.SelectMany(candidate =>
                            candidate
                                .Names.SelectMany(value => value.Provenance.Evidence)
                                .Concat(
                                    candidate.Locations.SelectMany(value =>
                                        value.Provenance.Evidence
                                    )
                                )
                                .Concat(
                                    candidate.ExternalIdentities.SelectMany(value =>
                                        value.Provenance.Evidence
                                    )
                                )
                        )
                        .Distinct()
                        .ToArray();
                    await ApplyVenuePublicationAsync(
                        c,
                        t,
                        workUuid,
                        stage.Publication,
                        token,
                        acceptedEvidence
                    );
                    if (stage.Kind == VenueResolutionStageKind.Assignment)
                    {
                        outcome = await ReadVenueResolutionOutcomeAsync(c, t, attempt, token);
                    }
                }
                await store.WriteStagePublishedAsync(
                    c,
                    t,
                    resolutionAttemptUuid,
                    workUuid,
                    new(workUuid, NormalizationPublicationStatus.Published, false),
                    token,
                    outcome
                );
            },
            ct,
            candidateWriteScopes: requested.Kind
                is VenueResolutionStageKind.AcknowledgeMissing
                    or VenueResolutionStageKind.AcknowledgeDecision
                    or VenueResolutionStageKind.SupersedeObservation
                ? []
                : IdentityCandidateWriteScopes
        );
    }

    private static void RequireVenueNegativeAcknowledgment(
        VenueResolutionAttempt attempt,
        VenueResolutionStage stage
    )
    {
        RequireVenueAcknowledgmentSource(attempt, stage);
        if (attempt.Preparation!.Status == VenueResolutionPreparationStatus.Ready)
        {
            RequireVenueNegativeDecision(attempt);
        }
    }

    private static void RequireVenueAcknowledgmentSource(
        VenueResolutionAttempt attempt,
        VenueResolutionStage stage
    )
    {
        var preparation = attempt.Preparation!;
        var source = preparation.Source;
        var claim = source.Claim!;
        var context = preparation.Context;
        var registry = preparation.PolicyOwnerObservation;
        if (
            attempt.ProcessingVersion != VenueResolutionPolicyEvaluator.ProcessorVersion
            || !context.SourceCurrent
            || registry == null
            || !stage.ReadSet.OwnerObservations.Contains(registry)
            || !stage.ReadSet.Inputs.Any(value =>
                value.RevisionUuid == claim.Evidence.InputRevisionUuid
            )
        )
        {
            throw new NormalizationConflictException(
                "A negative acknowledgment requires its original source and observed policy registry state."
            );
        }
        RequireVenueStageReadSet(preparation.ReadSet, stage);
        RequireVenueStageReadSet(
            new(
                [],
                [],
                context.ReadSet.CatalogInputs,
                [],
                null,
                [],
                context.ReadSet.CandidateUniverses,
                context.ReadSet.OwnerObservations,
                []
            ),
            stage
        );
        if (source.OriginalTicket is { } original)
        {
            RequireVenueStageReadSet(
                new(
                    original.Inputs,
                    original.Dependencies,
                    original.CatalogInputs ?? [],
                    original.SourceIdentities ?? [],
                    original.MappingRegistry,
                    original.ImportedVenues ?? [],
                    original.CandidateUniverses ?? [],
                    original.OwnerObservations ?? [],
                    original.SourceUuids ?? []
                ),
                stage
            );
        }
        if (source.ImportedBinding is { } imported)
        {
            RequireVenueStageReadSet(
                new([], [], imported.CatalogInputs, [], null, imported.ImportedVenues, [], [], []),
                stage
            );
        }
        if (
            source.ProposalOwner is { } proposal
            && !stage.ReadSet.OwnerObservations.Contains(proposal)
        )
        {
            throw new NormalizationConflictException(
                "The original source proposal observation is missing."
            );
        }
    }

    private static void RequireVenueNegativeDecision(VenueResolutionAttempt attempt)
    {
        var preparation = attempt.Preparation!;
        var claim = preparation.Source.Claim!;
        var context = preparation.Context;
        var proof = attempt.Decision!;
        var expected =
            preparation.Policy == null
                ? new VenueCorrespondenceDecision(
                    VenueCorrespondenceAction.NeedsReview,
                    null,
                    "",
                    "acceptance_policy_inactive",
                    []
                )
                : new VenueResolutionPolicyEvaluator().Evaluate(
                    proof.Facts,
                    preparation.Policy.Policy
                );
        if (
            proof.AttemptUuid != attempt.AttemptUuid
            || proof.ProcessorVersion != attempt.ProcessingVersion
            || !NormalizationVenueResolutionStore.Same(proof.Claim, claim)
            || !NormalizationVenueResolutionStore.Same(proof.Policy, preparation.Policy)
            || !NormalizationVenueResolutionStore.Same(proof.ReadSet, preparation.ReadSet)
            || !NormalizationVenueResolutionStore.Same(
                proof.Facts.OriginalVenueObservation,
                claim.Observation
            )
            || proof.Facts.SourceScope
                != (
                    claim.IsShow
                        ? VenueResolutionSourceScope.Show
                        : VenueResolutionSourceScope.Imported
                )
            || proof.Facts.Source.ObservedDate != claim.ObservedDate?.Value
            || proof.Facts.Complete != context.Complete
            || !NormalizationVenueResolutionStore.Same(
                proof.Facts.Candidates,
                context
                    .Candidates.Facts.Concat(context.AssignmentTargets)
                    .DistinctBy(value => value.Header.Uuid)
                    .OrderBy(value => value.Header.Uuid)
                    .ToArray()
            )
            || !NormalizationVenueResolutionStore.Same(proof.Facts.Assignments, context.Assignments)
            || proof.Decision.Action != VenueCorrespondenceAction.NeedsReview
            || !NormalizationVenueResolutionStore.Same(proof.Decision, expected)
        )
        {
            throw new NormalizationConflictException(
                "Decision acknowledgment requires the exact saved inactive-policy draft or evaluated negative."
            );
        }
    }

    private static void RequireVenueResolutionProof(
        VenueResolutionAttempt attempt,
        VenueResolutionStage stage
    )
    {
        var preparation = attempt.Preparation!;
        var proof = attempt.Decision!;
        var claim = preparation.Source.Claim!;
        var context = preparation.Context;
        if (
            attempt.ProcessingVersion != VenueResolutionPolicyEvaluator.ProcessorVersion
            || proof.ProcessorVersion != attempt.ProcessingVersion
            || proof.AttemptUuid != attempt.AttemptUuid
            || proof.Policy == null
            || proof.TargetVenueUuid == null
            || !NormalizationVenueResolutionStore.Same(proof.Claim, claim)
            || !NormalizationVenueResolutionStore.Same(proof.Policy, preparation.Policy)
            || !stage.ReadSet.OwnerObservations.Contains(proof.Policy.OwnerObservation)
            || !NormalizationVenueResolutionStore.Same(proof.ReadSet, preparation.ReadSet)
            || !NormalizationVenueResolutionStore.Same(
                proof.Facts.OriginalVenueObservation,
                claim.Observation
            )
            || proof.Facts.SourceScope
                != (
                    claim.IsShow
                        ? VenueResolutionSourceScope.Show
                        : VenueResolutionSourceScope.Imported
                )
            || proof.Facts.Source.ObservedDate != claim.ObservedDate?.Value
            || proof.Facts.Complete != context.Complete
            || context.Candidates.Version != NormalizationCandidateService.FactsVersion
            || !NormalizationVenueResolutionStore.Same(
                proof.Facts.Candidates,
                context
                    .Candidates.Facts.Concat(context.AssignmentTargets)
                    .DistinctBy(value => value.Header.Uuid)
                    .OrderBy(value => value.Header.Uuid)
                    .ToArray()
            )
            || !NormalizationVenueResolutionStore.Same(proof.Facts.Assignments, context.Assignments)
            || !NormalizationVenueResolutionStore.Same(
                new VenueResolutionPolicyEvaluator().Evaluate(proof.Facts, proof.Policy.Policy),
                proof.Decision
            )
            || proof.Decision.Action == VenueCorrespondenceAction.NeedsReview
        )
        {
            throw new NormalizationConflictException(
                "A supported evaluated venue processor decision is required."
            );
        }
        RequireVenueStageReadSet(proof.ReadSet, stage);
        if (stage.Kind == VenueResolutionStageKind.CreateVenue)
        {
            RequireVenueCreationProof(proof, stage);
        }
        else
        {
            RequireVenueAssignmentProof(attempt, stage);
        }
    }

    private static void RequireVenueStageReadSet(
        VenueResolutionReadSet original,
        VenueResolutionStage stage
    )
    {
        var consumed = stage.ReadSet;
        var universes = original.CandidateUniverses;
        if (stage.DerivedContext is { } derived)
        {
            // Own creation advances the global venue pool. Only that derived candidate
            // snapshot may replace an evaluation epoch; raw and source clocks stay original.
            var derivedKeys = derived
                .ReadSet.CandidateUniverses.Select(value => value.Universe)
                .ToHashSet();
            universes = universes
                .Where(value => !derivedKeys.Contains(value.Universe))
                .Concat(derived.ReadSet.CandidateUniverses)
                .ToArray();
        }
        if (
            !original.Inputs.All(consumed.Inputs.Contains)
            || !original.Dependencies.All(consumed.Dependencies.Contains)
            || !original.CatalogInputs.All(consumed.CatalogInputs.Contains)
            || !original.SourceIdentities.All(consumed.SourceIdentities.Contains)
            || original.MappingRegistry != null
                && original.MappingRegistry != consumed.MappingRegistry
            || !original.ImportedVenues.All(consumed.ImportedVenues.Contains)
            || !universes.All(consumed.CandidateUniverses.Contains)
            || !original.OwnerObservations.All(value =>
                consumed.OwnerObservations.Contains(value)
                || consumed.Dependencies.Contains(
                    new(
                        value.SubjectUuid,
                        value.DesiredGeneration,
                        value.EditorialRevision,
                        value.PublishedWorkUuid
                    )
                )
            )
            || !original.SourceUuids.All(consumed.SourceUuids.Contains)
        )
        {
            throw new NormalizationConflictException(
                "Venue publication cannot replace its original source, owner or evaluation clocks."
            );
        }
    }

    private static void RequireVenueCreationProof(
        VenueResolutionDecision proof,
        VenueResolutionStage stage
    )
    {
        var creation = proof.Decision.Creation;
        var publication = stage.Publication;
        if (
            proof.Decision.Action != VenueCorrespondenceAction.ProposeCreation
            || creation == null
            || publication.Venues.Count != 1
            || publication.Names.Count != 1
            || publication.ExternalIdentities.Count != 0
            || publication.ImportedVenues.Count != 0
            || publication.Shows.Count != 0
            || publication.Venues[0]
                != new CanonicalVenue(
                    proof.TargetVenueUuid!.Value,
                    creation.DisplayName,
                    creation.Kind,
                    creation.ParentTarget?.VenueUuid
                )
        )
        {
            throw new NormalizationConflictException(
                "Venue creation differs from its saved evaluated proposal."
            );
        }
        var name = publication.Names[0];
        if (
            name.Uuid == Guid.Empty
            || name
                != new VenueNameAssertion(
                    name.Uuid,
                    proof.TargetVenueUuid.Value,
                    creation.DisplayName,
                    null,
                    null,
                    null,
                    creation.IdentityEvidence
                )
        )
        {
            throw new NormalizationConflictException(
                "Venue creation requires its exact evidenced display-name assertion."
            );
        }
        var source = proof.Claim.Observation;
        if (source.OriginalLocality is { } locality && !string.IsNullOrWhiteSpace(locality.Value))
        {
            var location = publication.Locations.SingleOrDefault();
            if (
                location == null
                || location.Uuid == Guid.Empty
                || location
                    != new VenueLocationAssertion(
                        location.Uuid,
                        proof.TargetVenueUuid.Value,
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
                        source.CoordinateKind == ObservedCoordinateKind.Venue
                            ? "provider_venue"
                            : null,
                        null,
                        null,
                        locality.Evidence
                    )
            )
            {
                throw new NormalizationConflictException(
                    "Venue creation cannot infer geography, dates or a venue pin from the source."
                );
            }
        }
        else if (publication.Locations.Count != 0)
        {
            throw new NormalizationConflictException(
                "Venue creation has no source locality assertion."
            );
        }
    }

    private static void RequireVenueAssignmentProof(
        VenueResolutionAttempt attempt,
        VenueResolutionStage stage
    )
    {
        var proof = attempt.Decision!;
        var claim = proof.Claim;
        var publication = stage.Publication;
        var assignments = claim.IsShow ? publication.Shows : publication.ImportedVenues;
        var other = claim.IsShow ? publication.ImportedVenues : publication.Shows;
        var evidence =
            proof.Decision.Action == VenueCorrespondenceAction.ProposeCreation
                ? new[] { proof.Decision.Creation!.IdentityEvidence, claim.Evidence }
                    .Distinct()
                    .ToArray()
                : proof
                    .Decision.Candidates.Single(value => value.VenueUuid == proof.TargetVenueUuid)
                    .Evidence.Append(claim.Evidence)
                    .Distinct()
                    .ToArray();
        if (
            publication.Venues.Count != 0
            || publication.Names.Count != 0
            || publication.Locations.Count != 0
            || publication.ExternalIdentities.Count != 0
            || other.Count != 0
            || assignments.Count > 1
            || proof.Decision.Action == VenueCorrespondenceAction.ProposeMatch
                && proof.Decision.VenueUuid != proof.TargetVenueUuid
        )
        {
            throw new NormalizationConflictException(
                "Venue assignment cannot publish unrelated domain changes."
            );
        }
        // A no-op stage may acknowledge an unchanged assignment; its effective target is read
        // after publication and recorded independently of the successful Work receipt.
        if (assignments.Count == 0)
        {
            if (
                proof.ExpectedAssignmentRevision <= 0
                || !attempt.Preparation!.Context.Assignments.Any(value =>
                    !value.IsHistorical
                    && value.AssignmentUuid == proof.AssignmentUuid
                    && value.Revision == proof.ExpectedAssignmentRevision
                    && value.State == "accepted"
                    && value.ResolvedVenueUuid == proof.TargetVenueUuid
                )
            )
            {
                throw new NormalizationConflictException(
                    "An empty assignment stage requires an existing assignment revision."
                );
            }
        }
        else
        {
            var assignment = assignments[0];
            if (
                assignment.Uuid != proof.AssignmentUuid
                || assignment.CatalogUuid != claim.CatalogUuid
                || assignment.VenueUuid != proof.TargetVenueUuid
                || assignment.State != "accepted"
                || assignment.Basis != "validated_match"
                || assignment.ReasonCode != "evaluated_venue_correspondence"
                || !assignment.Evidence.SequenceEqual(evidence)
            )
            {
                throw new NormalizationConflictException(
                    "Venue assignment differs from its saved evaluated decision."
                );
            }
        }
        if (proof.Decision.Action == VenueCorrespondenceAction.ProposeCreation)
        {
            RequirePostCreationVenueContext(attempt, stage);
        }
    }

    private static void RequirePostCreationVenueContext(
        VenueResolutionAttempt attempt,
        VenueResolutionStage stage
    )
    {
        var proof = attempt.Decision!;
        var derived = stage.DerivedContext;
        var creation = proof.Decision.Creation!;
        var original = attempt.Preparation!.Context;
        var createdStage = attempt.Stages.SingleOrDefault(value =>
            value.Kind == VenueResolutionStageKind.CreateVenue
        );
        var target = derived?.Candidates.Facts.SingleOrDefault(value =>
            value.Header.Uuid == proof.TargetVenueUuid
        );
        if (
            createdStage?.Receipt?.Status != NormalizationPublicationStatus.Published
            || derived == null
            || !derived.Complete
            || target == null
            || target.Header.DisplayText != creation.DisplayName
            || target.Header.Kind != creation.Kind
            || !target.Names.Any(value =>
                value.Wording == creation.DisplayName
                && value.Provenance.Evidence.Contains(creation.IdentityEvidence)
            )
            || !NormalizationVenueResolutionStore.Same(derived.Assignments, original.Assignments)
            || !derived
                .Candidates.Facts.Select(value => value.Header.Uuid)
                .ToHashSet()
                .SetEquals(
                    original
                        .Candidates.Facts.Select(value => value.Header.Uuid)
                        .Append(proof.TargetVenueUuid!.Value)
                )
            || original.Candidates.Facts.Any(value =>
                !NormalizationVenueResolutionStore.Same(
                    value,
                    derived.Candidates.Facts.SingleOrDefault(current =>
                        current.Header.Uuid == value.Header.Uuid
                    )
                )
            )
            || !derived.ReadSet.CandidateUniverses.All(stage.ReadSet.CandidateUniverses.Contains)
            || !derived.ReadSet.OwnerObservations.All(stage.ReadSet.OwnerObservations.Contains)
            || !derived.ReadSet.CatalogInputs.All(stage.ReadSet.CatalogInputs.Contains)
        )
        {
            throw new NormalizationConflictException(
                "Post-creation venue assignment requires unchanged alternatives and the exact newly accepted identity."
            );
        }
    }

    private static async Task<VenueResolutionAssignmentOutcome> ReadVenueResolutionOutcomeAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        VenueResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        var proof = attempt.Decision!;
        var table = proof.Claim.IsShow ? "show_venue_assignments" : "imported_venue_assignments";
        var column = proof.Claim.IsShow ? "show_uuid" : "imported_venue_uuid";
        var rows = (
            await c.QueryAsync<ResolutionAssignmentRow>(
                new CommandDefinition(
                    $"SELECT a.uuid AS \"Uuid\",r.resolved_uuid AS \"VenueUuid\",a.state AS \"State\" FROM normalization.{table} a JOIN normalization.venue_identity_resolution r ON r.original_uuid=a.canonical_venue_uuid WHERE a.{column}=@CatalogUuid AND a.state IN ('accepted','pending') ORDER BY a.uuid",
                    new { proof.Claim.CatalogUuid },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var accepted = rows.SingleOrDefault(value => value.State == "accepted");
        return new(
            proof.TargetVenueUuid!.Value,
            accepted?.Uuid,
            accepted?.VenueUuid,
            rows.Where(value =>
                    value.State == "pending" && value.VenueUuid == proof.TargetVenueUuid
                )
                .Select(value => value.Uuid)
                .ToArray()
        );
    }

    private sealed record ResolutionAssignmentRow(Guid Uuid, Guid VenueUuid, string State);
}
