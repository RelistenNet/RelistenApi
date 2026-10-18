using System;
using System.Collections.Generic;
using System.Linq;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Diagnostic score for a pair of actor assertions. It carries no canonical person
/// identifier, probability, evaluated threshold or permission to publish a match.
/// </summary>
public sealed record PeopleIdentityPairScore(
    double Score,
    bool Eligible,
    IReadOnlyList<string> RejectionReasons
);

public sealed partial class PeopleResolutionPolicyEvaluator
{
    /// <summary>
    /// Score public people-comparison features with the same arithmetic used by
    /// evaluated policies. Shadow witnesses need no invented canonical UUID.
    /// Eligibility excludes noncandidates and contradictory facts; callers still
    /// need qualified whole-pool evidence and policy authority for acceptance.
    /// </summary>
    public static PeopleIdentityPairScore ScorePair(
        IdentityCandidateComparison pair,
        PeopleAcceptanceLinearScorer scorer
    )
    {
        if (pair.Domain != IdentityMatchingDomain.People)
        {
            throw new ArgumentException(
                "People scoring requires a people comparison.",
                nameof(pair)
            );
        }
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
        return new(value, reasons.Count == 0, reasons);
    }
}
