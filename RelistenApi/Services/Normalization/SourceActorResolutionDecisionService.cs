using System;
using System.Collections.Generic;
using System.Linq;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>Role-free identity comparisons and source review protection. No decision writes accepted data.</summary>
public sealed class SourceActorResolutionDecisionService(
    IdentityCandidateMatcher matcher,
    PeopleResolutionPolicyEvaluator evaluator
)
{
    public const string Version =
        "source-actor-resolution-decisions-v1/"
        + PeopleActorProjection.SourceAliasVersion
        + "/"
        + IdentityCandidateMatcher.PeopleActorFeaturesVersion;

    internal SourceActorResolutionClaim Compare(
        SourceActorAliasCoordinates coordinates,
        SourceActorAliasAssertion<NormalizedEvidenceReference> assertion,
        int assertionIndex,
        Guid artistUuid,
        int? candidatePoolIndex,
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts>? candidates,
        PeopleAcceptancePolicyState policy,
        SourceActorResolutionReviewSnapshot sourceReviews
    )
    {
        var reviews = sourceReviews.Reviews;
        var projection = PeopleActorProjection.Project(assertion, artistUuid);
        var comparisons = (candidates?.Facts ?? [])
            .Select(candidate =>
                matcher.ComparePeople(projection.Record, PeopleActorProjection.Candidate(candidate))
            )
            .ToArray();
        var machine =
            policy.Active != null && candidates != null
                ? evaluator.EvaluateSourceActorAlias(
                    new(assertion, artistUuid, candidates, comparisons),
                    policy.Active.Policy
                )
                : null;
        var decision =
            machine == null
                ? new SourceActorResolutionDecision(
                    SourceActorResolutionAction.Unresolved,
                    projection.Query == null
                        ? "incomplete_actor_projection"
                        : "unsupported_policy_applicability",
                    null,
                    null
                )
                : new SourceActorResolutionDecision(
                    Enum.Parse<SourceActorResolutionAction>(machine.Action.ToString()),
                    machine.Reason,
                    machine.PersonUuid,
                    machine
                );
        var hash = Relisten.Data.SourceActorResolutionCheckpoint.AssertionHash(assertion);
        var exact = reviews.SingleOrDefault(review => review.Coordinates == coordinates);
        var corresponding = (sourceReviews.Correspondences ?? [])
            .Where(proof => proof.Current.Coordinates == coordinates)
            .ToArray();
        if (exact != null)
        {
            if (exact.AssertionSha256 != hash)
            {
                throw new ArgumentException(
                    "An exact reviewed assertion must retain its original content hash."
                );
            }
            decision = exact.State switch
            {
                "accepted" => new(
                    SourceActorResolutionAction.Review,
                    "reviewed_source_acceptance",
                    exact.SelectedPersonUuid,
                    machine
                ),
                "rejected" => new(
                    SourceActorResolutionAction.Rejected,
                    "reviewed_source_rejection",
                    null,
                    machine
                ),
                "retracted" => new(
                    SourceActorResolutionAction.Retracted,
                    "reviewed_source_retraction",
                    null,
                    machine
                ),
                _ => throw new ArgumentException("Unsupported source review state."),
            };
        }
        else if (
            corresponding.Length > 0
            && corresponding
                .Select(proof => proof.OriginalReview.SelectedPersonUuid)
                .Distinct()
                .Count() == 1
        )
        {
            // This is derived support from an actual original human operation, not a new
            // approval or a candidate-pool result. The original proof remains in the snapshot.
            decision = new(
                SourceActorResolutionAction.Review,
                "reviewed_source_acceptance",
                corresponding[0].OriginalReview.SelectedPersonUuid,
                machine
            );
        }
        else if (reviews.Any(review => SameField(review.Coordinates, coordinates)))
        {
            // Changed local IDs, parser versions or input revisions do not erase a reviewed
            // field. Strict correspondence is a separate required proof, not name matching.
            decision = new(
                SourceActorResolutionAction.Review,
                "reviewed_source_field_requires_correspondence",
                null,
                machine
            );
        }
        return new(
            coordinates,
            hash,
            assertionIndex,
            projection,
            candidatePoolIndex,
            comparisons,
            exact?.Uuid,
            exact?.ReviewRevision ?? 0,
            decision
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
}
