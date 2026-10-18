using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class SourceActorAliasApplicationService
{
    private async Task<PreparedSource> ReadTrustedSourceAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorResolutionBatch proposal,
        CancellationToken ct,
        SourceActorAliasApplicationIndex? committed = null
    )
    {
        var extraction = await SourceActorResolutionStore.ReadPublishedExtractionAsync(
            c,
            t,
            proposal.ExtractionAttemptUuid,
            proposal.SourceUuid,
            ct
        );
        var inputs = await SourceActorResolutionStore.ReadOriginalInputsAsync(c, t, extraction, ct);
        var reviews = await SourceActorResolutionStore.ReadSourceReviewsAsync(
            c,
            t,
            extraction,
            inputs,
            ct
        );
        var correspondence = await SourceActorReviewedEvidence.ReadCorrespondencesAsync(
            c,
            t,
            extraction,
            inputs,
            reviews,
            ct
        );
        reviews = reviews with
        {
            Correspondences = correspondence.Count == 0 ? null : correspondence,
        };
        var current = await SourceIsCurrent(c, t, extraction, ct);
        committed ??= new SourceActorAliasApplicationIndex([]);
        var supportIds = committed
            .Claims.Values.SelectMany(value => value.Outcomes)
            .Where(value => value.SupportUuid.HasValue)
            .Select(value => value.SupportUuid!.Value)
            .Distinct()
            .ToArray();
        var activeSupports = (
            await c.QueryAsync<Guid>(
                new CommandDefinition(
                    """
                    SELECT support.uuid FROM normalization.person_alias_contributor_support support
                    JOIN normalization.source_actor_alias_assignments assignment ON assignment.uuid=support.assignment_uuid
                    WHERE support.uuid=ANY(@supportIds) AND support.retired_work_uuid IS NULL AND assignment.retired_work_uuid IS NULL
                    """,
                    new { supportIds },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToHashSet();
        var carried = new List<SourceActorAliasClaimReceiptReference>();
        var targetUuids = new List<Guid>();
        var planned = new List<SourceActorAliasPlannedClaim>();
        var preparedInputs = new List<SourceActorResolutionInput>();
        var reference = new SourceActorAliasProposalReference(
            proposal.AttemptUuid,
            proposal.ExtractionAttemptUuid,
            proposal.ProcessingVersion
        );
        for (var inputIndex = 0; inputIndex < inputs.Length; inputIndex++)
        {
            var input = inputs[inputIndex];
            var scopes = new List<SourceActorResolutionScope>();
            for (var scopeIndex = 0; scopeIndex < input.Scopes.Count; scopeIndex++)
            {
                var scope = input.Scopes[scopeIndex];
                var claims = new List<SourceActorResolutionClaim>();
                var resource = extraction
                    .Ticket.Inputs.Single(value =>
                        value.RevisionUuid == scope.OriginalScope.InputRevisionUuid
                    )
                    .ResourceUuid;
                for (
                    var assertionIndex = 0;
                    assertionIndex < scope.OriginalScope.Extraction.Assertions.Count;
                    assertionIndex++
                )
                {
                    ct.ThrowIfCancellationRequested();
                    var assertion = scope.OriginalScope.Extraction.Assertions[assertionIndex];
                    var coordinates = new SourceActorAliasCoordinates(
                        extraction.SourceUuid,
                        input.OriginalInput.Contributor,
                        resource,
                        scope.OriginalScope.InputRevisionUuid,
                        assertion.NameEvidence.JsonPointer,
                        scope.OriginalScope.ProducerVersion,
                        assertion.AssertionUuid,
                        assertion.ActorReferenceUuid,
                        assertion.FieldSha256
                    );
                    var claimReference = new SourceActorAliasClaimReference(
                        reference,
                        inputIndex,
                        scopeIndex,
                        assertionIndex
                    );
                    if (current && committed.Claims.TryGetValue(claimReference, out var prior))
                    {
                        var accepted = prior.Acceptance;
                        if (
                            !CanCarrySourceClaim(accepted, coordinates, reviews)
                            || prior.Outcomes.Any(value =>
                                value.SupportUuid.HasValue
                                && !activeSupports.Contains(value.SupportUuid.Value)
                            )
                        )
                        {
                            throw new NormalizationConflictException(
                                "A committed source claim's authority changed; start a new application for the new decision."
                            );
                        }
                        var carriedPerson = prior.Stage.OriginalPersonUuid;
                        carried.Add(new(claimReference, prior.Stage.StageUuid, carriedPerson));
                        planned.Add(
                            new(
                                claimReference,
                                SourceActorAliasApplicationDisposition.Complete,
                                "committed_source_claim_receipt",
                                carriedPerson
                            )
                        );
                        targetUuids.Add(carriedPerson);
                        continue;
                    }
                    var exact = reviews.Reviews.SingleOrDefault(value =>
                        value.Coordinates == coordinates
                    );
                    var derived = correspondence
                        .Where(value => value.Current.Coordinates == coordinates)
                        .ToArray();
                    var artist = input.OriginalInput.Binding.Recording.Value!.ArtistUuid;
                    // Exact human authority authenticates the whole original field, even when
                    // the same retained revision was extracted again under another attempt ID.
                    var human =
                        exact == null
                            ? null
                            : await SourceActorReviewedEvidence.ReadReviewPreparationAsync(
                                c,
                                t,
                                exact,
                                ct
                            );
                    var supportedReview = exact == null || human != null;
                    if (human != null)
                    {
                        var currentField = SourceActorReviewEvidence.SelectField(
                            extraction,
                            inputs,
                            coordinates
                        );
                        if (
                            !SourceActorReviewEvidence.Same(
                                currentField with
                                {
                                    ExtractionAttemptUuid = human
                                        .OriginalField
                                        .ExtractionAttemptUuid,
                                },
                                human.OriginalField
                            )
                        )
                        {
                            throw new NormalizationConflictException(
                                "Exact source review differs from its complete original field."
                            );
                        }
                    }
                    var authority =
                        current
                        && supportedReview
                        && (
                            exact?.State == "accepted"
                            || derived.Length > 0
                                && derived
                                    .Select(value => value.OriginalReview.SelectedPersonUuid)
                                    .Distinct()
                                    .Count() == 1
                        );
                    var claim = decisions.Compare(
                        coordinates,
                        assertion,
                        assertionIndex,
                        artist,
                        null,
                        null,
                        proposal.Preparation.PolicyState,
                        reviews
                    );
                    if (!supportedReview)
                    {
                        claim = claim with
                        {
                            Decision = new(
                                SourceActorResolutionAction.Review,
                                "unsupported_source_review_protocol",
                                null,
                                null
                            ),
                        };
                    }
                    if (
                        !current
                        && claim.Decision.Action
                            is not (
                                SourceActorResolutionAction.Rejected
                                or SourceActorResolutionAction.Retracted
                            )
                    )
                    {
                        claim = claim with
                        {
                            Decision = new(
                                SourceActorResolutionAction.Unresolved,
                                "source_proof_is_historical",
                                null,
                                null
                            ),
                        };
                    }
                    claims.Add(claim);
                    var selected = authority ? claim.Decision.PersonUuid : null;
                    if (selected is Guid person)
                    {
                        targetUuids.Add(person);
                    }
                    planned.Add(
                        new(
                            new(reference, inputIndex, scopeIndex, assertionIndex),
                            selected.HasValue ? SourceActorAliasApplicationDisposition.Complete
                                : claim.Decision.Action == SourceActorResolutionAction.Review
                                    ? SourceActorAliasApplicationDisposition.Review
                                : SourceActorAliasApplicationDisposition.Unresolved,
                            claim.Decision.Reason,
                            selected
                        )
                    );
                }
                scopes.Add(scope with { Claims = claims.ToArray() });
            }
            preparedInputs.Add(input with { Scopes = scopes.ToArray() });
        }
        // All claim authority above is authenticated before acquiring target components.
        // The batch retains each original in source order within this same RR snapshot.
        var targets = await SourceActorReviewTargets.ReadManyAsync(c, t, targetUuids, ct);
        return new(
            extraction,
            preparedInputs.ToArray(),
            reviews,
            [],
            null,
            targets,
            planned.ToArray(),
            current,
            proposal.Preparation.PolicyState,
            carried
        );
    }

    internal static async Task<bool> SourceIsCurrent(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorExtractionConsumption extraction,
        CancellationToken ct
    )
    {
        var expected = extraction.Ticket;
        var inputRows = (
            await c.QueryAsync<NormalizationInputDependency>(
                new CommandDefinition(
                    """
                    SELECT uuid AS "ResourceUuid",current_revision_uuid AS "RevisionUuid",generation AS "Generation"
                    FROM normalization.input_resources WHERE uuid=ANY(@ids) ORDER BY uuid
                    """,
                    new { ids = expected.Inputs.Select(value => value.ResourceUuid).ToArray() },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var owner = await ReadOwner(c, t, expected.SubjectUuid, ct);
        var catalog = await ObserveCatalog(c, t, expected, ct);
        var expectedOwners = expected
            .Dependencies.Select(value => new NormalizationSubjectObservation(
                value.SubjectUuid,
                value.DesiredGeneration,
                value.EditorialRevision,
                value.PublishedWorkUuid
            ))
            .Concat(expected.OwnerObservations ?? [])
            .ToArray();
        var currentOwners = new List<NormalizationSubjectObservation>();
        foreach (var dependency in expectedOwners)
        {
            currentOwners.Add(await ReadOwner(c, t, dependency.SubjectUuid, ct));
        }
        return SameSet(expectedOwners, currentOwners)
            && SameSet(inputRows, expected.Inputs)
            && owner == SourceActorResolutionStore.ExtractionObservation(extraction)
            && SameSet(catalog.CatalogInputs ?? [], expected.CatalogInputs ?? [])
            && SameSet(catalog.SourceIdentities ?? [], expected.SourceIdentities ?? [])
            && SameSet(catalog.SourceUuids ?? [], expected.SourceUuids ?? [])
            && SameSet(catalog.ImportedVenues ?? [], expected.ImportedVenues ?? [])
            && catalog.MappingRegistry == expected.MappingRegistry
            && !await AdmissionCandidateFactsChanged(c, t, expected.CandidateUniverses ?? [], ct);
    }

    internal static bool CanCarrySourceClaim(
        SourceActorAliasAcceptanceReference authority,
        SourceActorAliasCoordinates coordinates,
        SourceActorResolutionReviewSnapshot reviews
    )
    {
        if (authority.Kind == SourceActorAliasAuthorityKind.Machine)
        {
            return !reviews.Reviews.Any(value => SameField(value.Coordinates, coordinates));
        }
        bool SameAuthority(SourceActorAliasReviewState review) =>
            review.State == "accepted"
            && review.Uuid == authority.ReviewStateUuid
            && review.ReviewRevision == authority.ReviewRevision
            && review.LastOperationUuid == authority.ReviewOperationUuid
            && review.SelectedPersonUuid == authority.OriginalSelectedPersonUuid;
        return authority.Kind
                is (
                    SourceActorAliasAuthorityKind.Reviewed
                    or SourceActorAliasAuthorityKind.Correspondence
                )
            && (
                reviews.Reviews.Any(value =>
                    value.Coordinates == coordinates && SameAuthority(value)
                )
                || (reviews.Correspondences ?? []).Any(value =>
                    value.Current.Coordinates == coordinates && SameAuthority(value.OriginalReview)
                )
            );
    }

    private static bool SameField(
        SourceActorAliasCoordinates left,
        SourceActorAliasCoordinates right
    ) =>
        left.SourceUuid == right.SourceUuid
        && left.Contributor == right.Contributor
        && left.ResourceUuid == right.ResourceUuid
        && left.FieldJsonPointer == right.FieldJsonPointer;

    internal sealed record PreparedSource(
        SourceActorExtractionConsumption Extraction,
        IReadOnlyList<SourceActorResolutionInput> Inputs,
        SourceActorResolutionReviewSnapshot Reviews,
        IReadOnlyList<NormalizationCandidateFactsSnapshot<PersonCandidateFacts>> Pools,
        ActivePeopleAcceptancePolicy? Policy,
        IReadOnlyList<SourceActorReviewTarget> Targets,
        IReadOnlyList<SourceActorAliasPlannedClaim> Claims,
        bool Current,
        PeopleAcceptancePolicyState OriginalPolicyState,
        IReadOnlyList<SourceActorAliasClaimReceiptReference> CarriedClaims
    );
}
