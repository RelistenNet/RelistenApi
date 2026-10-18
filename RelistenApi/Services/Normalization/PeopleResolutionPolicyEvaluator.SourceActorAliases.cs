using System;
using System.Linq;
using System.Text.Json;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace Relisten.Normalization;

public sealed partial class PeopleResolutionPolicyEvaluator
{
    public const string SourceInputForm = "source_actor_alias_assertion";

    /// <summary>
    /// Evaluates one authenticated role-free assertion against a complete primary snapshot.
    /// This pure audit is not source authentication, active-policy authority or publication.
    /// No branch can create an identity or infer its kind, a recording role or attendance.
    /// </summary>
    public SourceActorAliasPolicyDecision EvaluateSourceActorAlias(
        SourceActorAliasPolicyInput input,
        RegisteredPeopleAcceptancePolicy policy
    )
    {
        var artifact = policy.Artifact;
        SourceActorAliasPolicyDecision Result(
            SourceActorAliasPolicyAction action,
            string reason,
            PeopleAcceptanceCandidateScore[]? scores = null,
            Guid? person = null,
            double? score = null,
            double? margin = null
        ) =>
            new(
                input.Assertion.AssertionUuid,
                action,
                reason,
                artifact.Uuid,
                artifact.ContentHash,
                policy.Evaluation.Uuid,
                scores ?? [],
                person,
                score,
                margin
            );

        if (!PeopleResolutionPolicyService.SourceActorQualificationIsValid(policy))
        {
            return Result(
                SourceActorAliasPolicyAction.Unresolved,
                "unsupported_policy_applicability"
            );
        }
        var projection = PeopleActorProjection.Project(input.Assertion, input.ArtistUuid);
        if (
            input.Assertion.Version != SourceActorAliasExtractor.Version
            || !projection.Complete
            || projection.Query == null
        )
        {
            return Result(SourceActorAliasPolicyAction.Unresolved, "incomplete_actor_projection");
        }
        if (!input.Candidates.Complete)
        {
            return Result(SourceActorAliasPolicyAction.Unresolved, "incomplete_candidate_facts");
        }
        if (!HasCompleteSourceCandidateProjection(input, projection))
        {
            return Result(
                SourceActorAliasPolicyAction.Unresolved,
                "inconsistent_candidate_projection"
            );
        }
        if (!ReproducesSourceComparisons(input, projection))
        {
            return Result(
                SourceActorAliasPolicyAction.Unresolved,
                "inconsistent_candidate_comparisons"
            );
        }

        var scores = ScoreCandidates(input.Comparisons, artifact.Scorer);
        if (scores.Any(candidate => !double.IsFinite(candidate.Score)))
        {
            return Result(
                SourceActorAliasPolicyAction.Unresolved,
                "non_finite_candidate_score",
                scores
            );
        }
        var selected = SelectExistingCandidate(scores, artifact.Scorer);
        if (selected == null)
        {
            // An empty pool or low scores cannot classify a name as a new person or group.
            // A combined artifact's separate credit-creation capability is irrelevant here.
            return Result(
                SourceActorAliasPolicyAction.Review,
                "source_identity_unresolved",
                scores
            );
        }
        return Result(
            selected.Action == PeopleResolutionAction.MatchExisting
                ? SourceActorAliasPolicyAction.MatchExisting
                : SourceActorAliasPolicyAction.Review,
            selected.Reason,
            scores,
            selected.PersonUuid,
            selected.WinnerScore,
            selected.RunnerUpMargin
        );
    }

    private static bool HasCompleteSourceCandidateProjection(
        SourceActorAliasPolicyInput input,
        PeopleActorProjectionResult projection
    )
    {
        var snapshot = input.Candidates;
        var candidateIds = snapshot.Facts.Select(fact => fact.Hit.Uuid).ToHashSet();
        if (
            candidateIds.Count != snapshot.Facts.Count
            || candidateIds.Count != snapshot.Selection.Items.Count
            || !candidateIds.SetEquals(snapshot.Selection.Items.Select(hit => hit.Uuid))
        )
        {
            return false;
        }
        var selectedHits = snapshot.Selection.Items.ToDictionary(hit => hit.Uuid);
        return snapshot.Version == NormalizationCandidateService.PeopleFactsVersion
            && snapshot.Selection.Domain == IdentityMatchingDomain.People
            && SameSourceValue(snapshot.Query, projection.Query)
            && snapshot.Facts.All(fact =>
                SameSourceValue(fact.Hit, selectedHits[fact.Hit.Uuid])
                && fact.Header.Uuid == fact.Hit.Uuid
                && fact.Header.Kind is "person" or "group"
                && fact.ActorMatchingFacts != null
                && fact.AcceptedAliasMatchingNames != null
                && fact.AliasMatchingNamesComplete == true
            )
            && input.Comparisons.Count == candidateIds.Count
            && input.Comparisons.All(pair =>
                pair.Domain == IdentityMatchingDomain.People
                && pair.CandidateUuid.HasValue
                && candidateIds.Contains(pair.CandidateUuid.Value)
            )
            && input.Comparisons.Select(pair => pair.CandidateUuid).Distinct().Count()
                == candidateIds.Count;
    }

    private static bool ReproducesSourceComparisons(
        SourceActorAliasPolicyInput input,
        PeopleActorProjectionResult projection
    )
    {
        var matcher = new IdentityCandidateMatcher();
        var comparisons = input.Comparisons.ToDictionary(pair => pair.CandidateUuid!.Value);
        // Do not accept caller-supplied agreements or ranking numbers. Reproduce every actual
        // comparison from the exact role-free projection and the v5 semantic matching aggregate;
        // bounded supporting witnesses cannot substitute for that complete aggregate.
        return input.Candidates.Facts.All(fact =>
            SameSourceValue(
                matcher.ComparePeople(projection.Record, PeopleActorProjection.Candidate(fact)),
                comparisons[fact.Hit.Uuid]
            )
        );
    }

    private static bool SameSourceValue<T>(T left, T right) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(left),
            JsonSerializer.SerializeToElement(right)
        );
}
