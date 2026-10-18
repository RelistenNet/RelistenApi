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

public sealed partial class NormalizedPeopleService
{
    private static async Task<SourceActorAliasApplicationService.PreparedSource> AuthenticateSourceAliasFacts(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasApplication application,
        SourceActorAliasPersonStage stage,
        CancellationToken ct
    )
    {
        var proposal = await SourceActorAliasApplicationService.ReadOriginalProposal(
            c,
            t,
            application.Plan!.Proposal,
            application.SourceUuid,
            ct
        );
        var extraction = await SourceActorResolutionStore.ReadPublishedExtractionAsync(
            c,
            t,
            stage.Facts.ExtractionAttemptUuid,
            application.SourceUuid,
            ct
        );
        var original = await SourceActorResolutionStore.ReadOriginalInputsAsync(
            c,
            t,
            extraction,
            ct
        );

        AuthenticateOriginalSourceAliasInventory(application, stage, extraction, original);
        var reviews = await AuthenticateSourceAliasReviews(
            c,
            t,
            application,
            stage,
            extraction,
            original,
            ct
        );

        if (stage.Facts.Policy is { } policy)
        {
            if (!SourceAliasSame(policy.Policy, proposal.Preparation.PolicyState.Active?.Policy))
            {
                throw new ArgumentException(
                    "Machine source publication must retain its original proposal's pinned qualified policy."
                );
            }
            await PeopleResolutionPolicyService.RequireStateAsync(
                c,
                t,
                new(policy, policy.OwnerObservation),
                ct
            );
        }

        var current = await SourceActorAliasApplicationService.SourceIsCurrent(
            c,
            t,
            extraction,
            ct
        );
        if (!current && stage.Deltas.Any(delta => delta.Kind != SourceActorAliasDeltaKind.Retire))
        {
            throw new NormalizationConflictException(
                "Positive source interpretation requires its exact original source proof to remain current."
            );
        }

        var carried = await AuthenticateCarriedSourceAliasClaims(
            c,
            t,
            application,
            stage,
            reviews,
            current,
            ct
        );
        var claims = stage
            .Facts.Inputs.SelectMany(
                (input, inputIndex) =>
                    input.Scopes.SelectMany(
                        (scope, scopeIndex) =>
                            scope.Claims.Select(claim => new SourceActorAliasPlannedClaim(
                                new(
                                    application.Plan.Proposal,
                                    inputIndex,
                                    scopeIndex,
                                    claim.AssertionIndex
                                ),
                                claim.Decision.PersonUuid.HasValue
                                    ? SourceActorAliasApplicationDisposition.Complete
                                    : SourceActorAliasApplicationDisposition.Unresolved,
                                claim.Decision.Reason,
                                claim.Decision.Action == SourceActorResolutionAction.MatchExisting
                                || claim.Decision.Action == SourceActorResolutionAction.Review
                                    && claim.Decision.Reason == "reviewed_source_acceptance"
                                    ? claim.Decision.PersonUuid
                                    : null
                            ))
                    )
            )
            .Concat(carried)
            .ToArray();
        return new(
            extraction,
            stage.Facts.Inputs,
            stage.Facts.SourceReviews,
            stage.Facts.CandidatePools,
            stage.Facts.Policy,
            stage.Facts.Targets,
            claims,
            current,
            proposal.Preparation.PolicyState,
            stage.Facts.CarriedClaims ?? []
        );
    }

    private static void AuthenticateOriginalSourceAliasInventory(
        SourceActorAliasApplication application,
        SourceActorAliasPersonStage stage,
        SourceActorExtractionConsumption extraction,
        IReadOnlyList<SourceActorResolutionInput> original
    )
    {
        if (
            application.Plan!.Proposal.ExtractionAttemptUuid != extraction.AttemptUuid
            || !SourceAliasSame(extraction.Ticket, stage.Facts.OriginalExtractionTicket)
            || original.Count != stage.Facts.Inputs.Count
        )
        {
            throw new ArgumentException(
                "Source stages retain the complete original published source proof."
            );
        }

        for (var index = 0; index < original.Count; index++)
        {
            var saved = stage.Facts.Inputs[index];
            var actual = original[index];
            if (
                !SourceAliasSame(saved.OriginalInput, actual.OriginalInput)
                || saved.AliasPayloadPresent != actual.AliasPayloadPresent
                || !SourceAliasSame(saved.DecodeIssues, actual.DecodeIssues)
                || saved.Scopes.Count != actual.Scopes.Count
            )
            {
                throw new ArgumentException(
                    "A source input differs from its retained original extraction."
                );
            }
            for (var scopeIndex = 0; scopeIndex < actual.Scopes.Count; scopeIndex++)
            {
                var scope = saved.Scopes[scopeIndex];
                var raw = actual.Scopes[scopeIndex];
                if (
                    !SourceAliasSame(scope.OriginalScope, raw.OriginalScope)
                    || !SourceAliasSame(scope.Fields, raw.Fields)
                )
                {
                    throw new ArgumentException(
                        "A source scope must preserve all fields, assertions and unresolved coverage."
                    );
                }
                var indexes = scope
                    .Claims.Select(claim => claim.AssertionIndex)
                    .Concat(
                        (stage.Facts.CarriedClaims ?? [])
                            .Where(value =>
                                value.Claim.InputIndex == index
                                && value.Claim.ScopeIndex == scopeIndex
                            )
                            .Select(value => value.Claim.AssertionIndex)
                    )
                    .ToArray();
                if (
                    indexes.Length != raw.OriginalScope.Extraction.Assertions.Count
                    || !indexes
                        .ToHashSet()
                        .SetEquals(
                            Enumerable.Range(0, raw.OriginalScope.Extraction.Assertions.Count)
                        )
                )
                {
                    throw new ArgumentException(
                        "A source scope must preserve all fields, assertions and unresolved coverage."
                    );
                }
            }
        }
    }

    private static async Task<SourceActorResolutionReviewSnapshot> AuthenticateSourceAliasReviews(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasApplication application,
        SourceActorAliasPersonStage stage,
        SourceActorExtractionConsumption extraction,
        IReadOnlyList<SourceActorResolutionInput> original,
        CancellationToken ct
    )
    {
        var reviews = await SourceActorResolutionStore.ReadSourceReviewsAsync(
            c,
            t,
            extraction,
            original,
            ct
        );
        var correspondences = await SourceActorReviewedEvidence.ReadCorrespondencesAsync(
            c,
            t,
            extraction,
            original,
            reviews,
            ct
        );
        var rows = reviews.Reviews.ToList();
        var history = stage
            .PriorMap.Where(entry => entry.Scope.SourceUuid == application.SourceUuid)
            .SelectMany(entry =>
                entry
                    .Bindings.Select(binding =>
                        binding.Acceptance.Claim.Proposal.ExtractionAttemptUuid
                    )
                    .Concat(
                        (entry.LastWithdrawalAuthorities ?? []).Select(value =>
                            value.Claim.Proposal.ExtractionAttemptUuid
                        )
                    )
            )
            .Distinct()
            .Where(uuid => uuid != extraction.AttemptUuid);
        foreach (var extractionUuid in history)
        {
            var historical = await SourceActorResolutionStore.ReadPublishedExtractionAsync(
                c,
                t,
                extractionUuid,
                application.SourceUuid,
                ct
            );
            var inputs = await SourceActorResolutionStore.ReadOriginalInputsForAcceptedProofAsync(
                c,
                t,
                historical,
                ct
            );
            rows.AddRange(
                (
                    await SourceActorResolutionStore.ReadSourceReviewsAsync(
                        c,
                        t,
                        historical,
                        inputs,
                        ct
                    )
                ).Reviews
            );
        }
        var currentRows = rows.GroupBy(value => value.Uuid)
            .Select(group =>
            {
                var first = group.First();
                if (group.Any(value => !SourceAliasSame(first, value)))
                {
                    throw new NormalizationConflictException(
                        "A source review UUID has conflicting current snapshots."
                    );
                }
                return first;
            })
            .OrderBy(value => value.Uuid)
            .ToArray();
        if (
            !SourceAliasSame(
                currentRows,
                stage.Facts.SourceReviews.Reviews.OrderBy(value => value.Uuid).ToArray()
            )
            || reviews.OwnerObservation != stage.Facts.SourceReviews.OwnerObservation
            || !SourceAliasSame(
                correspondences
                    .Select(proof =>
                        proof with
                        {
                            Target = RestoreSourceAliasPrimary(proof.Target, stage),
                        }
                    )
                    .ToArray(),
                stage.Facts.SourceReviews.Correspondences ?? []
            )
        )
        {
            throw new NormalizationConflictException(
                "The original source review inventory or whole-field correspondence changed."
            );
        }

        return reviews with
        {
            Correspondences = correspondences.Count == 0 ? null : correspondences,
        };
    }

    private static async Task ValidateSourceAliasClaims(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasApplication application,
        SourceActorAliasPersonStage stage,
        SourceActorAliasApplicationService.PreparedSource source,
        CancellationToken ct
    )
    {
        var decisions = new SourceActorResolutionDecisionService(new(), new());
        var usedPools = new HashSet<int>();
        // A null policy is deliberately sufficient for human/negative branches. Matching
        // only consumes policy when an actual machine pool was read for this new stage.
        var policy = stage.Facts.Policy is { } active
            ? new PeopleAcceptancePolicyState(active, active.OwnerObservation)
            : source.OriginalPolicyState;
        for (var inputIndex = 0; inputIndex < source.Inputs.Count; inputIndex++)
        {
            var input = source.Inputs[inputIndex];
            foreach (var scope in input.Scopes)
            {
                var resource = source
                    .Extraction.Ticket.Inputs.Single(value =>
                        value.RevisionUuid == scope.OriginalScope.InputRevisionUuid
                    )
                    .ResourceUuid;
                foreach (var claim in scope.Claims)
                {
                    var index = claim.AssertionIndex;
                    var assertion = scope.OriginalScope.Extraction.Assertions[index];
                    var coordinates = new SourceActorAliasCoordinates(
                        application.SourceUuid,
                        input.OriginalInput.Contributor,
                        resource,
                        scope.OriginalScope.InputRevisionUuid,
                        assertion.NameEvidence.JsonPointer,
                        scope.OriginalScope.ProducerVersion,
                        assertion.AssertionUuid,
                        assertion.ActorReferenceUuid,
                        assertion.FieldSha256
                    );
                    NormalizationCandidateFactsSnapshot<PersonCandidateFacts>? pool = null;
                    if (claim.CandidatePoolIndex is int slot)
                    {
                        if (
                            slot < 0
                            || slot >= stage.Facts.CandidatePools.Count
                            || stage.Facts.Policy == null
                        )
                        {
                            throw new ArgumentException(
                                "Machine source claims require their exact consumed policy and complete candidate pool."
                            );
                        }
                        pool = stage.Facts.CandidatePools[slot];
                        usedPools.Add(slot);
                        var query = PeopleActorProjection
                            .Project(
                                assertion,
                                input.OriginalInput.Binding.Recording.Value!.ArtistUuid
                            )
                            .Query;
                        if (
                            query == null
                            || pool.Version != NormalizationCandidateService.PeopleFactsVersion
                            || string.IsNullOrWhiteSpace(pool.QueryHash)
                            || !SourceAliasSame(pool.Query, query)
                            || pool.Selection.Domain != IdentityMatchingDomain.People
                            || !pool
                                .Facts.Select(fact => fact.Hit.Uuid)
                                .ToHashSet()
                                .SetEquals(pool.Selection.Items.Select(hit => hit.Uuid))
                            || pool.Facts.Select(fact => fact.Hit.Uuid).Distinct().Count()
                                != pool.Facts.Count
                            || pool.Facts.Any(fact =>
                                fact.Header.Uuid != fact.Hit.Uuid
                                || fact.ActorMatchingFacts == null
                                || fact.AcceptedAliasMatchingNames == null
                                || fact.AliasMatchingNamesComplete == null
                            )
                            || !(pool.ReadSet.OwnerObservations)
                                .ToHashSet()
                                .IsSubsetOf(
                                    (stage.Facts.CurrentReadSet.OwnerObservations ?? []).ToHashSet()
                                )
                            || !pool
                                .ReadSet.CatalogInputs.ToHashSet()
                                .IsSubsetOf(
                                    (stage.Facts.CurrentReadSet.CatalogInputs ?? []).ToHashSet()
                                )
                            || !pool
                                .ReadSet.CandidateUniverses.ToHashSet()
                                .IsSubsetOf(
                                    (
                                        stage.Facts.CurrentReadSet.CandidateUniverses ?? []
                                    ).ToHashSet()
                                )
                        )
                        {
                            throw new ArgumentException(
                                "Machine source claims require the actual complete original query, facts and read set."
                            );
                        }
                    }
                    var expected = decisions.Compare(
                        coordinates,
                        assertion,
                        index,
                        input.OriginalInput.Binding.Recording.Value!.ArtistUuid,
                        claim.CandidatePoolIndex,
                        pool,
                        policy,
                        source.Reviews
                    );
                    var exact = source.Reviews.Reviews.SingleOrDefault(value =>
                        value.Coordinates == coordinates
                    );
                    if (
                        exact != null
                        && await SourceActorReviewedEvidence.ReadReviewPreparationAsync(
                            c,
                            t,
                            exact,
                            ct
                        ) == null
                    )
                    {
                        expected = expected with
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
                        !source.Current
                        && expected.Decision.Action
                            is not (
                                SourceActorResolutionAction.Rejected
                                or SourceActorResolutionAction.Retracted
                            )
                    )
                    {
                        expected = expected with
                        {
                            Decision = new(
                                SourceActorResolutionAction.Unresolved,
                                "source_proof_is_historical",
                                null,
                                null
                            ),
                        };
                    }
                    if (!SourceAliasSame(claim, expected))
                    {
                        throw new ArgumentException(
                            "Source claims must reproduce their exact coordinates, projections, comparisons and authority."
                        );
                    }
                }
            }
        }
        if (!usedPools.SetEquals(Enumerable.Range(0, stage.Facts.CandidatePools.Count)))
        {
            throw new ArgumentException(
                "A source stage cannot consume unrelated machine candidate pools."
            );
        }
    }
}
