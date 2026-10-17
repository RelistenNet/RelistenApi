using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>A stored credit revision with its exact citations, including pending and reviewed claims.</summary>
public sealed record RecordingCreditContextClaim(
    RecordingCredit Credit,
    long Revision,
    Guid DecisionUuid
);

/// <summary>The original claim superseded by an operator decision; it remains an exclusion on later extraction.</summary>
public sealed record RecordingCreditReviewedExclusion(
    RecordingCreditContextClaim Claim,
    Guid ReviewOperationUuid,
    string Action
);

/// <summary>
/// One primary repeatable-read snapshot of accepted recording context. The people epoch also
/// fences an absent recording owner and new editorial decisions. Consumers must retain these
/// original observations with their preparation, rather than reread clocks at publication.
/// </summary>
public sealed record RecordingCreditContext(
    Guid SourceUuid,
    NormalizationSubjectObservation? RecordingOwnerObservation,
    NormalizationCandidateUniverseDependency PeopleUniverse,
    IReadOnlyList<RecordingCreditContextClaim> Credits,
    IReadOnlyList<RecordingCreditReviewedExclusion> ReviewedExclusions,
    IReadOnlyList<CandidateRedirectFact> PersonRedirects,
    IReadOnlyList<NormalizationSubjectObservation> IdentityOwnerObservations,
    IReadOnlyList<NormalizationBindingInputReference> EvidenceInputs
);
