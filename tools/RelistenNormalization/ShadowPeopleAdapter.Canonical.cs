using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

public sealed partial class ShadowPeopleAdapter
{
    private readonly ShadowPeopleCandidateSnapshots? acceptedCandidates;

    private ShadowCanonicalPeopleDecision CanonicalDecision(ActorAccount actor)
    {
        if (acceptedCandidates == null)
        {
            return UnavailableCanonicalDecision(actor, "accepted_candidate_snapshot_unavailable");
        }
        if (actor.Query == null)
        {
            return UnavailableCanonicalDecision(actor, "unsupported_actor_query");
        }

        var lookup = acceptedCandidates.Find(actor.Query);
        if (lookup.Snapshot is not { } snapshot)
        {
            return UnavailableCanonicalDecision(actor, "accepted_candidate_query_not_exported");
        }

        // These candidates are actual resolved identities. Their complete matching values
        // remain in the pinned snapshot; bounded evidence examples never define membership.
        var candidates = snapshot
            .Facts.Select(fact => CanonicalCandidate(actor, fact, snapshot.Complete))
            .OrderByDescending(candidate => candidate.Eligible)
            .ThenByDescending(candidate => candidate.RawScore)
            .ThenBy(candidate => candidate.PersonUuid)
            .ToArray();
        var supported = candidates
            .Where(candidate => candidate.SupportedIdentityEvidence)
            .ToArray();
        var reason =
            !snapshot.Complete ? "incomplete_accepted_candidate_snapshot"
            : !actor.ProjectionComplete ? "incomplete_actor_projection"
            : candidates.Length == 0 ? "no_accepted_candidates"
            : supported.Length > 0 ? "canonical_identity_proposals_require_qualified_policy"
            : "canonical_candidates_require_identity_evidence";

        return new(
            actor.Person.EntityKey,
            supported.Length > 0 ? "proposed" : "unresolved",
            reason,
            lookup.Locator,
            snapshot.Complete,
            actor.ProjectionComplete,
            candidates,
            CanonicalRanking(candidates)
        );
    }

    private ShadowCanonicalPeopleCandidate CanonicalCandidate(
        ActorAccount actor,
        PersonCandidateFacts fact,
        bool snapshotComplete
    )
    {
        var candidate = PeopleActorProjection.Candidate(fact);
        var comparison = matcher.ComparePeople(actor.Match, candidate);
        var support = IdentitySupport(
            actor.Match,
            candidate,
            comparison,
            actor.ProjectionComplete && snapshotComplete
        );
        var score =
            identityModel == null
                ? null
                : PeopleResolutionPolicyEvaluator.ScorePair(comparison, identityModel.Scorer);
        return new(
            fact.Hit.Uuid,
            "canonical_person:" + fact.Hit.Uuid,
            comparison,
            score?.Score,
            score?.Eligible ?? !support.HasConflict,
            score?.RejectionReasons ?? (support.HasConflict ? ["contradictory_facts"] : []),
            support.Supported
        );
    }

    private ShadowPeopleRanking? CanonicalRanking(ShadowCanonicalPeopleCandidate[] candidates)
    {
        if (identityModel == null)
        {
            return null;
        }

        var ranked = candidates.Where(candidate => candidate.Eligible).ToArray();
        var bestScore = ranked.FirstOrDefault()?.RawScore;
        string[] top =
            bestScore == null
                ? []
                : ranked
                    .Where(candidate => candidate.RawScore == bestScore)
                    .Select(candidate => candidate.EntityKey)
                    .ToArray();
        return new(
            identityModel.Identity,
            PeopleResolutionPolicyEvaluator.ScorerVersion,
            "raw_logit",
            top,
            bestScore,
            ranked.Length >= 2 ? bestScore - ranked[1].RawScore : null,
            ranked.Length == 0 ? "no_eligible_canonical_candidate"
                : top.Length > 1 ? "tied_canonical_candidates"
                : "unique_ranked_canonical_candidate"
        );
    }

    private static ShadowCanonicalPeopleDecision UnavailableCanonicalDecision(
        ActorAccount actor,
        string reason
    ) =>
        new(
            actor.Person.EntityKey,
            "unresolved",
            reason,
            null,
            false,
            actor.ProjectionComplete,
            [],
            null
        );
}

/// <summary>
/// Compares one source actor with the distinct accepted people from its exact retained query.
/// The snapshot locator preserves original facts and freshness stamps without copying them
/// into each result. A proposed link or ranking grants no automatic acceptance authority.
/// An empty complete snapshot permits separate creation evaluation; it does not create a person.
/// </summary>
public sealed record ShadowCanonicalPeopleDecision(
    string PersonEntityKey,
    string Outcome,
    string Reason,
    ShadowPeopleCandidateSnapshotLocator? Snapshot,
    bool SnapshotComplete,
    bool ProjectionComplete,
    IReadOnlyList<ShadowCanonicalPeopleCandidate> Candidates,
    ShadowPeopleRanking? Ranking,
    bool QualifiedForAutomaticAcceptance = false
);

/// <summary>One resolved person UUID, compared using full accepted matching aggregates.</summary>
public sealed record ShadowCanonicalPeopleCandidate(
    Guid PersonUuid,
    string EntityKey,
    IdentityCandidateComparison Comparison,
    double? RawScore,
    bool Eligible,
    IReadOnlyList<string> RejectionReasons,
    bool SupportedIdentityEvidence
);
