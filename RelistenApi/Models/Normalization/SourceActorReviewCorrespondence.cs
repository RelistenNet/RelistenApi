using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// An authenticated published field, with its complete containing scope. The reader must
/// verify extraction membership, original retained bytes, resource identity and origins before
/// calling the pure checker. FieldEvidence cites the complete decoded field, including markup.
/// </summary>
public sealed record SourceActorReviewField(
    Guid ExtractionAttemptUuid,
    SourceActorAliasCoordinates Coordinates,
    NormalizationRecordingSourceActorAliasScope Scope,
    string FieldText,
    NormalizedEvidenceReference FieldEvidence
);

/// <summary>One current original identity and its authenticated terminal resolution.</summary>
public sealed record SourceActorReviewIdentityMember(
    Guid PersonUuid,
    Guid ResolvedPersonUuid,
    long Revision
);

/// <summary>
/// The reader authenticates the complete current component, redirect edges and every original
/// and resolved owner in one primary snapshot. This value retains those observations; the pure
/// checker cannot establish database membership or owner authenticity.
/// </summary>
public sealed record SourceActorReviewTarget(
    Guid OriginalPersonUuid,
    Guid ResolvedPersonUuid,
    IReadOnlyList<SourceActorReviewIdentityMember> Members,
    IReadOnlyList<CandidateRedirectFact> Redirects,
    IReadOnlyList<NormalizationSubjectObservation> OwnerObservations
);

public enum SourceActorReviewLocalIdKind
{
    Assertion,
    ActorReference,
    Alias,
    Relationship,
}

/// <summary>
/// A pre-namespace extractor anchor and its exact old/current revision-local UUIDs. Matching
/// anchors come from deterministic whole-field parsing, never name similarity or list position.
/// </summary>
public sealed record SourceActorReviewLocalIdMap(
    SourceActorReviewLocalIdKind Kind,
    Guid AnchorUuid,
    Guid OriginalUuid,
    Guid CurrentUuid
);

/// <summary>Original and current citations of the same complete field or nested support.</summary>
public sealed record SourceActorReviewEvidenceMap(
    NormalizedEvidenceReference Original,
    NormalizedEvidenceReference Current
);

/// <summary>
/// Derived authority from an unchanged source field. OriginalReview is the immutable accepted
/// review/history snapshot, including its original operation and selected UUID. This proof
/// creates no human receipt or review row. Consumers must authenticate the proof and fence its
/// saved current observations before publication; unknown versions require new work.
/// </summary>
public sealed record ReviewedSourceCorrespondence(
    string Version,
    SourceActorAliasReviewState OriginalReview,
    SourceActorReviewField Original,
    SourceActorReviewField Current,
    IReadOnlyList<SourceActorReviewLocalIdMap> LocalIds,
    IReadOnlyList<SourceActorReviewEvidenceMap> Evidence,
    SourceActorReviewTarget Target
);
