using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace Relisten.Normalization.Batch;

public sealed partial class ShadowPeopleAdapter
{
    private const int MaximumDetailedRelationships = 100;
    private readonly Dictionary<string, Dictionary<string, ActorWitnesses>> accounts = new();
    private readonly Dictionary<string, Dictionary<string, ActorWitnesses>> names = new();
    private readonly IdentityCandidateMatcher matcher = new();

    private (
        IReadOnlyList<ShadowPersonRelationship> Links,
        ShadowPeopleRelationshipCoverage Coverage,
        ShadowPeopleIdentityDecision Decision
    ) Relationships(ActorAccount actor)
    {
        var accountCandidates = LookupKeys(actor.Match)
            .Where(accounts.ContainsKey)
            .SelectMany(key => accounts[key].Values)
            .Select(witnesses => witnesses.ForSource(actor.Person.SourceUuid))
            .OfType<ActorAccount>()
            .ToArray();
        var nameCandidates = actor
            .Match.Names.Select(name => NormalizedPeopleService.NameKey(name.Wording))
            .Where(names.ContainsKey)
            .SelectMany(key => names[key].Values)
            .Select(witnesses => witnesses.ForSource(actor.Person.SourceUuid))
            .OfType<ActorAccount>()
            .ToArray();
        var accountKeys = accountCandidates
            .Select(candidate => candidate.Person.EntityKey)
            .ToHashSet(StringComparer.Ordinal);
        var nameKeys = nameCandidates
            .Select(candidate => candidate.Person.EntityKey)
            .ToHashSet(StringComparer.Ordinal);
        var candidates = accountCandidates
            .Concat(nameCandidates)
            .DistinctBy(candidate => candidate.Person.EntityKey)
            .Where(candidate => candidate.Person.SourceUuid != actor.Person.SourceUuid)
            .ToArray();

        // Compare the entire retrieved pool. The limit applies only to repeated
        // supporting prose, never to scores, rivals or set-level decisions.
        var compared = candidates
            .Select(candidate => Compare(actor, candidate))
            .OrderByDescending(pair => pair.Score?.Eligible ?? !pair.HasConflict)
            .ThenByDescending(pair => pair.Score?.Score)
            .ThenByDescending(pair => pair.Supported)
            .ThenByDescending(pair => accountKeys.Contains(pair.Candidate.Person.EntityKey))
            .ThenBy(pair => pair.Candidate.Person.EntityKey, StringComparer.Ordinal)
            .ToArray();
        var links = compared
            .Take(MaximumDetailedRelationships)
            .Select(pair => Relationship(actor, pair))
            .ToArray();
        var coverage = new ShadowPeopleRelationshipCoverage(
            actor.Person.EntityKey,
            candidates.Length,
            compared.Length,
            candidates.Count(candidate => nameKeys.Contains(candidate.Person.EntityKey)),
            candidates.Count(candidate => accountKeys.Contains(candidate.Person.EntityKey)),
            candidates.Count(candidate => candidate.Person.ArtistUuid != actor.Person.ArtistUuid),
            links.Length
        );
        return (links, coverage, Decide(actor, compared, coverage));
    }

    private ComparedActor Compare(ActorAccount actor, ActorAccount candidate)
    {
        var comparison = matcher.ComparePeople(actor.Match, candidate.Match);
        bool Agrees(string feature) =>
            comparison.Features.Any(value =>
                value.Name == feature && value.Agreement == IdentityFeatureAgreement.Agrees
            );
        var uncertain =
            !actor.ProjectionComplete
            || !candidate.ProjectionComplete
            || actor.Match.ActorContext?.HasAttachmentUncertainty == true
            || candidate.Match.ActorContext?.HasAttachmentUncertainty == true;
        var conflict = comparison.Features.Any(feature =>
            feature.Agreement == IdentityFeatureAgreement.Conflicts
        );
        var accountAgrees = Agrees("actor_contact") || Agrees("actor_profile");
        var attributedAccount =
            Agrees("actor_contact")
            || ProfileKeys(actor.Match).Intersect(ProfileKeys(candidate.Match)).Any();
        var supported = Agrees("name") && !uncertain && !conflict && attributedAccount;
        var kind =
            supported ? "proposed_same_person"
            : conflict ? "unresolved_conflicting_actor_facts"
            : uncertain ? "unresolved_actor_attachment"
            : accountAgrees ? "unresolved_shared_account"
            : "unresolved_name_match";
        var score =
            identityModel == null
                ? null
                : PeopleResolutionPolicyEvaluator.ScorePair(comparison, identityModel.Scorer);
        return new(candidate, comparison, score, supported, conflict, kind);
    }

    private static ShadowPersonRelationship Relationship(ActorAccount actor, ComparedActor pair) =>
        new(
            pair.Kind,
            actor.Person.EntityKey,
            pair.Candidate.Person.EntityKey,
            pair.Candidate.Person.SourceUuid,
            pair.Candidate.Person.ArtistUuid,
            pair.Supported ? "name_or_explicit_alias_and_attributed_account"
                : pair.HasConflict ? "contradictory_actor_facts_require_review"
                : pair.Kind == "unresolved_actor_attachment"
                    ? "actor_projection_or_attachment_requires_review"
                : pair.Kind == "unresolved_shared_account" ? "nonexclusive_account_requires_review"
                : "name_requires_identity_evidence",
            pair.Comparison.Features,
            pair.Candidate.Evidence,
            pair.Comparison,
            actor.ModelDerived || pair.Candidate.ModelDerived
        );

    private ShadowPeopleIdentityDecision Decide(
        ActorAccount actor,
        ComparedActor[] candidates,
        ShadowPeopleRelationshipCoverage coverage
    )
    {
        var supported = candidates
            .Where(pair => pair.Supported)
            .Select(pair => pair.Candidate.Person.EntityKey)
            .ToArray();
        var ranked = candidates.Where(pair => pair.Score?.Eligible == true).ToArray();
        var bestScore = ranked.FirstOrDefault()?.Score?.Score;
        var runnerUpMargin = ranked.Length >= 2 ? bestScore - ranked[1].Score!.Score : null;
        string[] topKeys =
            bestScore == null
                ? []
                : ranked
                    .Where(pair => pair.Score!.Score == bestScore)
                    .Select(pair => pair.Candidate.Person.EntityKey)
                    .ToArray();
        var indexed = observedSources.Contains(actor.Person.SourceUuid);
        var reason =
            !indexed ? "global_actor_index_unavailable"
            : !actor.ProjectionComplete ? "incomplete_actor_projection"
            : actor.Match.ActorContext?.HasAttachmentUncertainty == true
                ? "actor_attachment_uncertainty"
            : candidates.Any(pair => pair.HasConflict) ? "conflicting_candidate_facts"
            : supported.Length > 0 ? "account_supported_identity_proposals"
            : candidates.Any(pair => pair.Kind == "unresolved_shared_account")
                ? "nonexclusive_account_requires_review"
            : candidates.Length > 0 ? "name_requires_identity_evidence"
            : "no_retrieved_candidate_witnesses";
        var scores = candidates
            .Select(pair => new ShadowPeopleCandidateOutcome(
                pair.Candidate.Person.EntityKey,
                pair.Candidate.Person.SourceUuid,
                pair.Candidate.Person.ArtistUuid,
                pair.Kind,
                pair.Score?.Score,
                pair.Score?.Eligible ?? !pair.HasConflict,
                pair.Score?.RejectionReasons ?? (pair.HasConflict ? ["contradictory_facts"] : []),
                pair.Candidate.ProjectionComplete,
                actor.ModelDerived || pair.Candidate.ModelDerived
            ))
            .ToArray();
        return new(
            actor.Person.EntityKey,
            reason == "account_supported_identity_proposals" ? "proposed" : "unresolved",
            reason,
            supported,
            indexed && coverage.Complete,
            actor.ProjectionComplete,
            scores,
            identityModel == null
                ? null
                : new(
                    identityModel.Identity,
                    PeopleResolutionPolicyEvaluator.ScorerVersion,
                    "raw_logit",
                    topKeys,
                    bestScore,
                    runnerUpMargin,
                    ranked.Length == 0 ? "no_eligible_candidate"
                        : topKeys.Length > 1 ? "tied_candidate_witnesses"
                        : "unique_ranked_witness"
                )
        );
    }

    /// <summary>
    /// Two source slots let the deterministic representative retrieve equivalent
    /// evidence from another recording. Multiple assertions in one source can
    /// occupy only one slot; candidate cardinality remains one per fact bucket.
    /// </summary>
    private sealed class ActorWitnesses
    {
        private ActorAccount first;
        private ActorAccount? second;

        internal ActorWitnesses(ActorAccount actor)
        {
            first = actor;
        }

        internal ActorAccount? ForSource(Guid sourceUuid) =>
            first.Person.SourceUuid != sourceUuid ? first : second;

        internal void Remember(ActorAccount actor)
        {
            if (actor.Person.SourceUuid == first.Person.SourceUuid)
            {
                if (Before(actor, first))
                {
                    first = actor;
                }
                return;
            }
            if (second != null && actor.Person.SourceUuid == second.Person.SourceUuid)
            {
                if (Before(actor, second))
                {
                    second = actor;
                }
            }
            else if (second == null || Before(actor, second))
            {
                second = actor;
            }
            if (second != null && Before(second, first))
            {
                (first, second) = (second, first);
            }
        }

        private static bool Before(ActorAccount left, ActorAccount right) =>
            StringComparer.Ordinal.Compare(left.Person.EntityKey, right.Person.EntityKey) < 0;
    }

    private sealed record ComparedActor(
        ActorAccount Candidate,
        IdentityCandidateComparison Comparison,
        PeopleIdentityPairScore? Score,
        bool Supported,
        bool HasConflict,
        string Kind
    );
}

/// <summary>
/// Complete set-level diagnostic over compact source-actor witnesses, not a canonical-person
/// pool. A ranking score is not a probability. Missing evaluated policy qualification remains
/// explicit even when one witness ranks first or several sources support a proposed link.
/// </summary>
public sealed record ShadowPeopleIdentityDecision(
    string PersonEntityKey,
    string Outcome,
    string Reason,
    IReadOnlyList<string> ProposedCandidateKeys,
    bool CandidateComparisonsComplete,
    bool ProjectionComplete,
    IReadOnlyList<ShadowPeopleCandidateOutcome> CandidateOutcomes,
    ShadowPeopleRanking? Ranking,
    bool QualifiedForAutomaticAcceptance = false
);

/// <summary>Compact outcome for every retrieved witness, including omitted detailed prose.</summary>
public sealed record ShadowPeopleCandidateOutcome(
    string CandidateEntityKey,
    Guid SourceUuid,
    Guid ArtistUuid,
    string Outcome,
    double? RawScore,
    bool Eligible,
    IReadOnlyList<string> RejectionReasons,
    bool ProjectionComplete,
    bool ModelDerivedEvidence
);

public sealed record ShadowPeopleRanking(
    string ModelSha256,
    string ScorerVersion,
    string ScoreKind,
    IReadOnlyList<string> HighestRankedCandidateKeys,
    double? HighestScore,
    double? RunnerUpMargin,
    string Outcome,
    bool AcceptanceThresholdAvailable = false
);
