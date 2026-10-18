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
    private async Task<PreparedSource> ReadSourceAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorResolutionBatch proposal,
        CancellationToken ct,
        bool evaluateMachine = true,
        SourceActorAliasApplication? application = null
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
        var attempted =
            application
                ?.Stages.Where(stage => stage.Receipt != null)
                .SelectMany(stage =>
                    stage.Receipt!.Names.Select(name => (Stage: stage, Name: name))
                )
                .GroupBy(value => value.Name.Claim)
                .ToDictionary(group => group.Key, group => group.ToArray())
            ?? [];
        var supportIds = attempted
            .Values.SelectMany(value => value)
            .Where(value => value.Name.SupportUuid.HasValue)
            .Select(value => value.Name.SupportUuid!.Value)
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
        var policy = default(ActivePeopleAcceptancePolicy);
        var pinned = proposal.Preparation.PolicyState.Active;
        var pools = new List<NormalizationCandidateFactsSnapshot<PersonCandidateFacts>>();
        var cache = new Dictionary<string, int>(StringComparer.Ordinal);
        var targets = new Dictionary<Guid, SourceActorReviewTarget>();
        var planned = new List<SourceActorAliasPlannedClaim>();
        var preparedInputs = new List<SourceActorResolutionInput>();
        var reference = new SourceActorAliasProposalReference(
            proposal.AttemptUuid,
            proposal.ExtractionAttemptUuid,
            proposal.ProcessingVersion
        );
        PeopleAcceptancePolicyState? policyState = null;
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
                    if (current && attempted.TryGetValue(claimReference, out var prior))
                    {
                        var stages = prior
                            .Select(value => value.Stage)
                            .DistinctBy(stage => stage.StageUuid)
                            .ToArray();
                        var accepted = stages
                            .Single()
                            .Acceptances.Single(value => value.Claim == claimReference);
                        if (
                            !CanCarrySourceClaim(accepted, coordinates, reviews)
                            || prior.Any(value =>
                                value.Name.SupportUuid.HasValue
                                && !activeSupports.Contains(value.Name.SupportUuid.Value)
                            )
                        )
                        {
                            throw new NormalizationConflictException(
                                "A committed source claim's authority changed; start a new application for the new decision."
                            );
                        }
                        var carriedPerson = stages.Single().OriginalPersonUuid;
                        carried.Add(new(claimReference, stages.Single().StageUuid, carriedPerson));
                        planned.Add(
                            new(
                                claimReference,
                                SourceActorAliasApplicationDisposition.Complete,
                                "committed_source_claim_receipt",
                                carriedPerson
                            )
                        );
                        if (!targets.ContainsKey(carriedPerson))
                        {
                            targets.Add(
                                carriedPerson,
                                await SourceActorReviewTargets.ReadAsync(c, t, carriedPerson, ct)
                            );
                        }
                        continue;
                    }
                    var exact = reviews.Reviews.SingleOrDefault(value =>
                        value.Coordinates == coordinates
                    );
                    var derived = correspondence
                        .Where(value => value.Current.Coordinates == coordinates)
                        .ToArray();
                    var artist = input.OriginalInput.Binding.Recording.Value!.ArtistUuid;
                    int? slot = null;
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
                    // Human-only passes never observe the machine registry or candidate pool.
                    // A machine pass uses the original policy, freshly active in this same RR.
                    if (
                        evaluateMachine
                        && current
                        && exact == null
                        && derived.Length == 0
                        && !reviews.Reviews.Any(value => SameField(value.Coordinates, coordinates))
                        && pinned != null
                    )
                    {
                        policyState ??= await policies.ReadStateInSnapshotAsync(c, t, ct);
                        if (
                            policyState.Active == null
                            || !Same(policyState.Active.Policy, pinned.Policy)
                        )
                        {
                            throw new NormalizationConflictException(
                                "The pinned source matching policy is no longer active."
                            );
                        }
                        policy = policyState.Active;
                        var query = PeopleActorProjection.Project(assertion, artist).Query;
                        if (query != null)
                        {
                            var key = PeopleActorProjection.QueryFingerprint(query);
                            if (!cache.TryGetValue(key, out var found))
                            {
                                found = pools.Count;
                                pools.Add(
                                    await candidates.FindPeopleFactsInSnapshotAsync(
                                        c,
                                        t,
                                        query,
                                        ct: ct
                                    )
                                );
                                cache.Add(key, found);
                            }
                            slot = found;
                        }
                    }
                    var claim = decisions.Compare(
                        coordinates,
                        assertion,
                        assertionIndex,
                        artist,
                        slot,
                        slot.HasValue ? pools[slot.Value] : null,
                        policyState ?? proposal.Preparation.PolicyState,
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
                    var selected =
                        claim.Decision.Action == SourceActorResolutionAction.MatchExisting
                        || authority && claim.Decision.PersonUuid.HasValue
                            ? claim.Decision.PersonUuid
                            : null;
                    if (selected is Guid person && !targets.ContainsKey(person))
                    {
                        targets.Add(
                            person,
                            await SourceActorReviewTargets.ReadAsync(c, t, person, ct)
                        );
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
        return new(
            extraction,
            preparedInputs.ToArray(),
            reviews,
            pools.ToArray(),
            policy,
            targets.Values.ToArray(),
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
