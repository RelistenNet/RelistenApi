using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// Retrieves identities for comparison, never accepted matches. ArtistUuid scopes repertoire
/// only. The similarity cutoff and name limit are retrieval controls to evaluate for recall.
/// </summary>
public sealed record NormalizationCandidateQuery(
    IdentityMatchingDomain Domain,
    IReadOnlyList<string> Names,
    IReadOnlyList<MatchingExternalIdentity> ExternalIdentities,
    Guid? ArtistUuid = null,
    int NameLimit = 100,
    double MinimumNameSimilarity = 0.25
);

/// <summary>
/// A current identity found through a supported name or provider key. Retrieval similarity is
/// not a probability. Matching consumers use the typed facts methods to capture assertions,
/// owner observations and candidate generations in the same database snapshot.
/// </summary>
public sealed record NormalizationCandidateHit(
    Guid Uuid,
    long Revision,
    string DisplayText,
    Guid? OwnerSubjectUuid,
    double NameSimilarity,
    IReadOnlyList<string> Reasons
);

/// <summary>
/// All qualified-identifier hits are retained even when names exceed NameLimit. A truncated
/// name pool is explicit so evaluation and resolution cannot assume exhaustive alternatives.
/// </summary>
public sealed record NormalizationCandidateSet(
    IdentityMatchingDomain Domain,
    IReadOnlyList<NormalizationCandidateHit> Items,
    bool NameCandidatesTruncated
);
