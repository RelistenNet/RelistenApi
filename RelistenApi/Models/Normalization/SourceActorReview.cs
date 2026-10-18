using System;

namespace Relisten.Api.Models.Normalization;

/// <summary>Trusted decisions about one source assertion; none publishes canonical metadata or source support.</summary>
public enum SourceActorReviewAction
{
    Accept,
    Reject,
    Retract,
    Reassign,
}

/// <summary>
/// IDs and optimistic revisions only. The extraction is an exact published original; claim
/// coordinates select stored evidence and never authorize caller-supplied assertions. Negative
/// review may address historical input. Positive review requires the current supported field.
/// </summary>
public sealed record SourceActorReviewRequest(
    Guid ExtractionAttemptUuid,
    SourceActorAliasCoordinates Claim,
    SourceActorReviewAction Action,
    long ExpectedEditorialRevision,
    long ExpectedSourceReviewRevision,
    Guid? SelectedPersonUuid = null,
    long? ExpectedSelectedPersonRevision = null
);

/// <summary>
/// Immutable first preparation. OriginalField retains historical evidence; ReadSet.Inputs
/// separately retains the heads actually observed during preparation. Read-only target owners
/// are not additional review subjects. A new preparation is required after a stale apply.
/// </summary>
public sealed record SourceActorReviewPreparation(
    Guid PreparationUuid,
    string ProcessingVersion,
    SourceActorReviewRequest Request,
    Guid ReviewStateUuid,
    NormalizationSubjectObservation SourceOwner,
    SourceActorReviewField OriginalField,
    SourceActorAliasReviewState? PriorReview,
    Guid? PriorAssignedPersonUuid,
    SourceActorReviewTarget? Target,
    NormalizationReviewReadSet ReadSet
);

/// <summary>A complete preparation is refused rather than truncated.</summary>
public sealed class SourceActorReviewCapacityException(string reason)
    : InvalidOperationException(reason)
{
    public string Reason { get; } = reason;
}
