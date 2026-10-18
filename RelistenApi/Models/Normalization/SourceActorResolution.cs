using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Relisten.Normalization;

namespace Relisten.Api.Models.Normalization;

/// <summary>Source identity proposals only; none authorizes an assignment, alias, credit or identity write.</summary>
public enum SourceActorResolutionAction
{
    MatchExisting,
    Review,
    Unresolved,
    Rejected,
    Retracted,
}

/// <summary>
/// Final proposal after source review protection. PolicyProposal retains the independent machine
/// outcome even when reviewed source authority requires a hold. Selected identities remain original UUIDs.
/// </summary>
public sealed record SourceActorResolutionDecision(
    SourceActorResolutionAction Action,
    string Reason,
    Guid? PersonUuid,
    SourceActorAliasPolicyDecision? PolicyProposal
);

/// <summary>
/// One original assertion within its containing scope. AssertionIndex and CandidatePoolIndex
/// address this preparation's complete stored values; neither is an identity or new proposal UUID.
/// The original assertion is OriginalScope.Extraction.Assertions[AssertionIndex].
/// Review revision zero retains exact-selector absence, never a fabricated review-state row.
/// </summary>
public sealed record SourceActorResolutionClaim(
    SourceActorAliasCoordinates Coordinates,
    string AssertionSha256,
    int AssertionIndex,
    PeopleActorProjectionResult Projection,
    int? CandidatePoolIndex,
    IReadOnlyList<IdentityCandidateComparison> Comparisons,
    Guid? SourceReviewStateUuid,
    long SourceReviewRevision,
    SourceActorResolutionDecision Decision
);

/// <summary>
/// Exact decoded original field bytes expressed as UTF-8 text, with the same SHA-256 convention
/// as the source extractor. Evidence retains its whole original field span; no whitespace,
/// HTML, names, qualifiers or local-reference anchors are normalized for later correspondence.
/// </summary>
public sealed record SourceActorResolutionField(
    string Field,
    string OriginalText,
    string FieldSha256,
    NormalizedEvidenceReference Evidence
);

/// <summary>One exact original source scope, including complete coverage and unresolved observations even with no assertions.</summary>
public sealed record SourceActorResolutionScope(
    NormalizationRecordingSourceActorAliasScope OriginalScope,
    IReadOnlyList<SourceActorResolutionField> Fields,
    IReadOnlyList<SourceActorResolutionClaim> Claims
);

/// <summary>
/// Complete extraction input membership and alias inventory. AliasPayloadPresent=false records
/// historical absence; empty Scopes cannot then be interpreted as a complete field scan or clear.
/// OriginalInput retains its original source/catalog/input binding observations, never restamped heads.
/// </summary>
public sealed record SourceActorResolutionInput(
    NormalizationRecordingPreparedInput OriginalInput,
    long ExpectedContributionRevision,
    bool AliasPayloadPresent,
    IReadOnlyList<SourceActorResolutionScope> Scopes,
    IReadOnlyList<ProviderDecodeIssue> DecodeIssues
);

/// <summary>
/// Current reviews for every original field consumed by this preparation, including earlier input
/// revisions. OwnerObservation is the actual nullable owner read in the same primary snapshot;
/// null is an observed absence, not a synthetic revision-zero owner. Publication rechecks rows
/// under the source Work lock, protecting concurrent first rejection and other source review changes.
/// </summary>
public sealed record SourceActorResolutionReviewSnapshot(
    NormalizationSubjectObservation? OwnerObservation,
    IReadOnlyList<SourceActorAliasReviewState> Reviews,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ReviewedSourceCorrespondence>? Correspondences = null
);

/// <summary>
/// First complete source proposal proof. Repeated identical candidate queries share an exact
/// snapshot slot within this preparation; each claim still retains every actual comparison.
/// PolicyState and PeopleUniverse retain negative observations when no qualified policy/query exists.
/// Review rows and original scopes retain the evidence needed for later strict correspondence review.
/// </summary>
public sealed record SourceActorResolutionPreparation(
    NormalizationSubjectObservation ExtractionObservation,
    NormalizationWorkTicket ExtractionTicket,
    PeopleAcceptancePolicyState PolicyState,
    NormalizationCandidateUniverseDependency PeopleUniverse,
    SourceActorResolutionReviewSnapshot SourceReviews,
    long? ExpectedConfigurationGeneration,
    IReadOnlyList<NormalizationInputContributionRevision> ExpectedContributions,
    IReadOnlyList<SourceActorResolutionInput> Inputs,
    IReadOnlyList<NormalizationCandidateFactsSnapshot<PersonCandidateFacts>> CandidatePools
);

public enum SourceActorResolutionDisposition
{
    Published,
    Unresolved,
    Stale,
}

public sealed record SourceActorResolutionResult(
    Guid AttemptUuid,
    Guid SourceUuid,
    SourceActorResolutionDisposition Disposition,
    string Reason,
    bool Replayed = false
);

/// <summary>Exact immutable request and first preparation survive cancellation, process restart and stale publication.</summary>
public sealed record SourceActorResolutionAttempt(
    Guid AttemptUuid,
    Guid SourceUuid,
    Guid ExtractionAttemptUuid,
    string ProcessingVersion,
    SourceActorResolutionPreparation? Preparation,
    SourceActorResolutionResult? Result
);

/// <summary>Published proposal history only. Neither this receipt nor a selected UUID is accepted identity authority.</summary>
public sealed record SourceActorResolutionBatch(
    Guid AttemptUuid,
    Guid SourceUuid,
    Guid ExtractionAttemptUuid,
    string ProcessingVersion,
    SourceActorResolutionPreparation Preparation,
    NormalizationWorkTicket Ticket
);
