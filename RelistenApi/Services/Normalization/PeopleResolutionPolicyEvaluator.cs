using System;
using System.Collections.Generic;
using System.Linq;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace Relisten.Normalization;

/// <summary>
/// Ranks an authenticated complete candidate pool under an immutable evaluated policy. This
/// pure result is audit output; the accepted writer separately checks active policy and Work.
/// </summary>
public sealed class PeopleResolutionPolicyEvaluator
{
    public const string ScorerVersion =
        "people-actor-context-linear-v3/"
        + PeopleActorProjection.Version
        + "/"
        + IdentityCandidateMatcher.PeopleActorFeaturesVersion;
    public const string RetrievalVersion =
        "people-actor-retrieval-v2/" + PeopleActorProjection.Version;
    public const string CreationClassifierVersion = "single-alphabetic-full-name-person-v1";

    /// <summary>
    /// claimCardinality counts all mentions in the recording, including unresolved mentions.
    /// This implementation cannot infer that two mentions refer to one new actor. Creation IDs
    /// must come from the application's immutable checkpoint, never from this pure evaluation.
    /// createdPersonUuid is supplied only after the applier verifies this exact original claim's
    /// identity publication receipt and rereads all candidates/context. It is correspondence
    /// evidence, not permission to ignore another plausible identity or infer co-reference.
    /// </summary>
    public PeopleAcceptanceClaimDecision Evaluate(
        NormalizationPeopleResolutionClaim claim,
        RegisteredPeopleAcceptancePolicy policy,
        int claimCardinality,
        string inputForm,
        Guid? creationPersonUuid = null,
        Guid? creationGroupUuid = null,
        Guid? createdPersonUuid = null
    )
    {
        var artifact = policy.Artifact;
        PeopleAcceptanceClaimDecision Result(
            PeopleResolutionAction action,
            string reason,
            IReadOnlyList<PeopleAcceptanceCandidateScore>? scores = null,
            Guid? person = null,
            double? score = null,
            double? margin = null,
            PeopleAcceptanceCreationPlan? creation = null
        ) =>
            new(
                claim.Proposal.ProposalUuid,
                action,
                reason,
                artifact.Uuid,
                artifact.ContentHash,
                policy.Evaluation.Uuid,
                scores ?? [],
                person,
                score,
                margin,
                creation
            );

        if (claim.ScopeIssue != null)
        {
            return Result(PeopleResolutionAction.Unresolved, "unbound_file_scope");
        }
        if (claim.Decision.Reason == "reviewed_credit_claim")
        {
            return Result(PeopleResolutionAction.Review, "reviewed_credit_claim");
        }
        if (!Supports(artifact, inputForm))
        {
            return Result(PeopleResolutionAction.Unresolved, "unsupported_policy_applicability");
        }
        if (claim.Candidates is not { Complete: true } snapshot)
        {
            return Result(PeopleResolutionAction.Unresolved, "incomplete_candidate_facts");
        }
        if (claim.Decision.Reason == "incomplete_actor_projection")
        {
            return Result(PeopleResolutionAction.Unresolved, "incomplete_actor_projection");
        }
        var candidateIds = snapshot.Facts.Select(fact => fact.Hit.Uuid).ToHashSet();
        if (
            snapshot.Version != artifact.CandidateFactsVersion
            || snapshot.Query.Domain != IdentityMatchingDomain.People
            || snapshot.Selection.Domain != IdentityMatchingDomain.People
            || snapshot.Query.ArtistUuid != null
            || candidateIds.Count != snapshot.Facts.Count
            || !candidateIds.SetEquals(snapshot.Selection.Items.Select(hit => hit.Uuid))
            || claim.Comparisons.Count != candidateIds.Count
            || claim.Comparisons.Any(pair =>
                pair.Domain != IdentityMatchingDomain.People
                || pair.CandidateUuid == null
                || !candidateIds.Contains(pair.CandidateUuid.Value)
            )
            || claim.Comparisons.Select(pair => pair.CandidateUuid).Distinct().Count()
                != candidateIds.Count
        )
        {
            return Result(PeopleResolutionAction.Unresolved, "inconsistent_candidate_projection");
        }

        var scores = claim
            .Comparisons.Select(pair => Score(pair, artifact.Scorer))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.PersonUuid)
            .ToArray();
        if (scores.Any(candidate => !double.IsFinite(candidate.Score)))
        {
            return Result(PeopleResolutionAction.Unresolved, "non_finite_candidate_score", scores);
        }
        if (createdPersonUuid is { } createdUuid)
        {
            var created = snapshot.Facts.SingleOrDefault(candidate =>
                candidate.Hit.Uuid == createdUuid
            );
            var targetScore = scores.SingleOrDefault(candidate =>
                candidate.PersonUuid == createdUuid
            );
            if (created == null || targetScore == null)
            {
                return Result(
                    PeopleResolutionAction.Unresolved,
                    "created_identity_missing",
                    scores
                );
            }
            if (
                artifact.Creation == null
                || claimCardinality != 1
                || !IsSupportedFullName(claim.Proposal.Mention.Name)
                || created.Header.Uuid != createdUuid
                || created.Header.Kind != "person"
                || created.Header.DisplayText != claim.Proposal.Mention.Name
                || !targetScore.Eligible
            )
            {
                return Result(
                    PeopleResolutionAction.Review,
                    "creation_correspondence_changed",
                    scores
                );
            }
            var otherCandidates = scores
                .Where(candidate => candidate.PersonUuid != createdUuid)
                .ToArray();
            if (
                otherCandidates.Any(candidate =>
                    candidate.Score > artifact.Creation.MaximumCandidateScore
                    || candidate.RejectionReasons.Contains("contradictory_facts")
                )
            )
            {
                return Result(
                    PeopleResolutionAction.Review,
                    "creation_correspondence_rival",
                    scores
                );
            }
            // The creation receipt binds this exact mention to its new identity. A new person
            // has no accepted artist credit yet, so the ordinary match threshold may not pass.
            // The separate evaluated creation capability and fresh rival checks govern this path.
            var correspondenceMargin =
                otherCandidates.Length == 0
                    ? (double?)null
                    : targetScore.Score - otherCandidates.Max(candidate => candidate.Score);
            return Result(
                PeopleResolutionAction.MatchExisting,
                "evaluated_creation_correspondence",
                scores,
                createdUuid,
                targetScore.Score,
                correspondenceMargin
            );
        }

        var eligible = scores.Where(candidate => candidate.Eligible).ToArray();
        var winner = eligible.FirstOrDefault();
        var rival = eligible.Skip(1).FirstOrDefault();
        var margin = winner == null || rival == null ? (double?)null : winner.Score - rival.Score;
        var conflicting = scores.Where(candidate =>
            candidate.RejectionReasons.Contains("contradictory_facts")
        );
        // A rejected low-scoring rival does not veto a clear winner. A plausible contradiction
        // remains reviewable even when the scorer would otherwise choose an eligible identity.
        if (
            conflicting.Any(candidate =>
                candidate.Score >= artifact.Scorer.MatchThreshold
                && (
                    winner == null
                    || winner.Score == candidate.Score
                    || winner.Score - candidate.Score < artifact.Scorer.MinimumRunnerUpMargin
                )
            )
        )
        {
            return Result(PeopleResolutionAction.Review, "relevant_candidate_conflict", scores);
        }
        if (winner != null && winner.Score >= artifact.Scorer.MatchThreshold)
        {
            if (rival != null && (margin == 0 || margin < artifact.Scorer.MinimumRunnerUpMargin))
            {
                return Result(
                    PeopleResolutionAction.Review,
                    "ambiguous_candidate_margin",
                    scores,
                    score: winner.Score,
                    margin: margin
                );
            }
            return Result(
                PeopleResolutionAction.MatchExisting,
                "evaluated_pool_winner",
                scores,
                winner.PersonUuid,
                winner.Score,
                margin
            );
        }

        var creation = artifact.Creation;
        var actor = claim.Proposal.Mention.ActorFacts;
        if (
            creation != null
            && (
                actor?.Kind?.Kind == "group"
                || actor?.Profiles.Any(profile =>
                    profile.Relationship == ActorProfileRelationship.CreditedProjectProfile
                ) == true
                || actor?.Unresolved.Any(issue =>
                    issue.Reason
                        is "conflicting_actor_kind"
                            or "ambiguous_alias_boundary"
                            or "actor_text_mapping_unavailable"
                ) == true
            )
        )
        {
            return Result(
                PeopleResolutionAction.Review,
                "unsupported_creation_actor_evidence",
                scores
            );
        }
        if (creation == null)
        {
            return Result(PeopleResolutionAction.Review, "creation_capability_unavailable", scores);
        }
        if (
            scores.Any(candidate => candidate.Score > creation.MaximumCandidateScore)
            || conflicting.Any()
        )
        {
            return Result(PeopleResolutionAction.Review, "existing_identity_not_excluded", scores);
        }
        if (claimCardinality != 1)
        {
            return Result(PeopleResolutionAction.Review, "creation_coreference_unresolved", scores);
        }
        if (!IsSupportedFullName(claim.Proposal.Mention.Name))
        {
            return Result(PeopleResolutionAction.Review, "actor_kind_unresolved", scores);
        }
        if (
            !creationPersonUuid.HasValue
            || creationPersonUuid == Guid.Empty
            || !creationGroupUuid.HasValue
            || creationGroupUuid == Guid.Empty
        )
        {
            return Result(
                PeopleResolutionAction.Unresolved,
                "creation_identifiers_required",
                scores
            );
        }
        return Result(
            PeopleResolutionAction.CreateIdentity,
            "evaluated_new_person",
            scores,
            creationPersonUuid,
            creation: new(
                creationPersonUuid.Value,
                "person",
                claim.Proposal.Mention.Name,
                creationGroupUuid.Value
            )
        );
    }

    /// <summary>Also applies to empty-claim stages that may retire complete contributor support.</summary>
    internal static bool Supports(PeopleAcceptancePolicyArtifact artifact, string inputForm) =>
        artifact.ScorerVersion == ScorerVersion
        && artifact.RetrievalVersion == RetrievalVersion
        && artifact.CandidateFactsVersion == NormalizationCandidateService.FactsVersion
        && artifact.ExtractionVersion == NormalizationRecordingExtractor.Version
        && artifact.CreditContextVersion == NormalizedPeopleService.CreditContextVersion
        && artifact.InputForms.Contains(inputForm, StringComparer.Ordinal)
        && (
            artifact.Creation == null
            || artifact.Creation.ClassifierVersion == CreationClassifierVersion
        );

    private static PeopleAcceptanceCandidateScore Score(
        IdentityCandidateComparison pair,
        PeopleAcceptanceLinearScorer scorer
    )
    {
        bool Agrees(string name) =>
            pair.Features.Any(feature =>
                feature.Name == name && feature.Agreement == IdentityFeatureAgreement.Agrees
            );
        var reasons = new List<string>();
        if (!pair.IsCandidate)
        {
            reasons.Add("not_a_candidate");
        }
        if (pair.Features.Any(feature => feature.Agreement == IdentityFeatureAgreement.Conflicts))
        {
            reasons.Add("contradictory_facts");
        }
        var value =
            scorer.Intercept
            + scorer.NameSimilarityWeight * pair.NameTokenSimilarity
            + scorer.ExactNameWeight * (Agrees("name") ? 1 : 0)
            + scorer.ArtistOverlapWeight * (Agrees("artist_context") ? 1 : 0)
            + scorer.ContactAgreementWeight * (Agrees("actor_contact") ? 1 : 0)
            + scorer.ProfileAgreementWeight * (Agrees("actor_profile") ? 1 : 0);
        return new(pair.CandidateUuid!.Value, value, reasons.Count == 0, reasons);
    }

    private static bool IsSupportedFullName(string name)
    {
        var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length is >= 2 and <= 5
            && tokens.All(token =>
                token.Length >= 2
                && char.IsUpper(token[0])
                && token.All(character =>
                    char.IsLetter(character) || character is '\'' or '’' or '-'
                )
            );
    }
}
