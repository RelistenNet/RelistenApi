using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Relisten.Api.Models.Normalization;

/// <summary>Exact immutable 55 history; readers authenticate the batch rather than trusting an ID.</summary>
public sealed record SourceActorAliasProposalReference(
    Guid ResolutionAttemptUuid,
    Guid ExtractionAttemptUuid,
    string ProcessingVersion
);

/// <summary>Indexes address the original 55 input/scope/assertion, never a newly invented proposal.</summary>
public sealed record SourceActorAliasClaimReference(
    SourceActorAliasProposalReference Proposal,
    int InputIndex,
    int ScopeIndex,
    int AssertionIndex
);

/// <summary>
/// Stable selected-root/file origin. Revision, quote and span remain in the immutable cited proof;
/// including them in scope identity would turn an ordinary new revision into a different scope.
/// </summary>
public sealed record SourceActorAliasOriginKey(
    NormalizationResourceKey Item,
    ProviderObservationScope Scope,
    string JsonPointer
);

/// <summary>Stable replacement key retains exact selected/file origins, even after a complete clear.</summary>
public sealed record SourceActorAliasScopeKey(
    Guid SourceUuid,
    NormalizationResourceKey Contributor,
    NormalizationResourceKey Resource,
    string FieldJsonPointer,
    string ProducerVersion,
    SourceActorAliasOriginKey RecordingOrigin,
    SourceActorAliasOriginKey? FileOrigin
);

public enum SourceActorAliasApplicationDisposition
{
    Complete,
    Partial,
    Review,
    Unresolved,
    Stale,
}

public enum SourceActorAliasAuthorityKind
{
    Machine,
    Reviewed,
    Correspondence,
    Withdrawal,
}

/// <summary>
/// Accepted history is addressed by immutable receipts. A current source/target observation belongs
/// to stage Facts, not this original acceptance reference. Reviewed proof names the exact 56
/// preparation, Work operation and original selected UUID; correspondence still names that lineage.
/// </summary>
public sealed record SourceActorAliasAcceptanceReference(
    SourceActorAliasClaimReference Claim,
    SourceActorAliasAuthorityKind Kind,
    Guid? ReviewPreparationUuid = null,
    Guid? ReviewStateUuid = null,
    long ReviewRevision = 0,
    Guid? ReviewOperationUuid = null,
    Guid? OriginalSelectedPersonUuid = null,
    Guid? AcceptedStageUuid = null
);

/// <summary>
/// Exact complete/incomplete field within the retained original scope. A scope can contain
/// several fields; coverage of one field never authorizes replacement or clear of another.
/// A null ScopeIndex records that the authenticated current input omitted this old scope;
/// it is an incomplete observation, never a fabricated containing scope or a clear.
/// </summary>
public sealed record SourceActorAliasScopeObservation(
    SourceActorAliasProposalReference Proposal,
    int InputIndex,
    int? ScopeIndex,
    string FieldJsonPointer,
    bool Complete,
    bool ExplicitClear
);

/// <summary>
/// Complete accepted projection for one exact field. Empty bindings retain clear/replacement proof;
/// an incomplete observation never authorizes retirement. Historical proofs are carried unchanged.
/// </summary>
public sealed record SourceActorAliasScopeMapEntry(
    SourceActorAliasScopeKey Scope,
    IReadOnlyList<SourceActorAliasMapBinding> Bindings,
    SourceActorAliasScopeObservation? LastCompleteObservation,
    IReadOnlyList<SourceActorAliasScopeObservation> IncompleteObservations,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<SourceActorAliasAcceptanceReference>? LastWithdrawalAuthorities = null
);

public sealed record SourceActorAliasMapBinding(
    Guid AssignmentUuid,
    Guid AliasUuid,
    Guid SupportUuid,
    SourceActorAliasAcceptanceReference Acceptance
);

public sealed record SourceActorAliasPlannedClaim(
    SourceActorAliasClaimReference Claim,
    SourceActorAliasApplicationDisposition Disposition,
    string Reason,
    Guid? OriginalPersonUuid
);

public sealed record SourceActorAliasApplicationPlan(
    SourceActorAliasProposalReference Proposal,
    IReadOnlyList<SourceActorAliasPlannedClaim> Claims,
    string? NegativeReason = null
);

/// <summary>
/// A committed identity decision and its actual name outcomes. Blocked names still block
/// finalization; this reference grants no new support and does not refresh the old policy proof.
/// </summary>
public sealed record SourceActorAliasClaimReceiptReference(
    SourceActorAliasClaimReference Claim,
    Guid StageUuid,
    Guid OriginalPersonUuid
);

/// <summary>
/// Original authority references and current factual observations are deliberately separate.
/// Inputs retain each fresh claim's projection, candidate-pool index, complete comparisons and
/// decision. Policy is null and pools are empty when no machine interpretation was consumed.
/// Membership is a complete same-snapshot value, including tombstones and the unmanaged baseline.
/// Repeated candidate snapshots and owner blocks are interned by the source checkpoint codec.
/// </summary>
public sealed record SourceActorAliasStageFacts(
    Guid ExtractionAttemptUuid,
    NormalizationWorkTicket OriginalExtractionTicket,
    IReadOnlyList<SourceActorResolutionInput> Inputs,
    SourceActorResolutionReviewSnapshot SourceReviews,
    IReadOnlyList<NormalizationCandidateFactsSnapshot<PersonCandidateFacts>> CandidatePools,
    ActivePeopleAcceptancePolicy? Policy,
    IReadOnlyList<SourceActorReviewTarget> Targets,
    IReadOnlyList<NormalizationSubjectObservation> CarryObservations,
    NormalizationInputContributionsSnapshot Membership,
    NormalizationReviewReadSet CurrentReadSet,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<SourceActorAliasClaimReceiptReference>? CarriedClaims = null
);

public sealed record SourceActorAliasPreparedName(
    Guid RequestedAliasUuid,
    string Wording,
    NormalizedEvidenceReference Evidence
);

public enum SourceActorAliasDeltaKind
{
    Add,
    Retire,
    FinalizeReplacement,
    Clear,
    ObserveIncomplete,
}

public sealed record SourceActorAliasStageDelta(
    SourceActorAliasDeltaKind Kind,
    SourceActorAliasScopeKey Scope,
    Guid OriginalPersonUuid,
    SourceActorAliasCoordinates? Coordinates,
    string? AssertionSha256,
    SourceActorAliasAcceptanceReference? Acceptance,
    IReadOnlyList<SourceActorAliasPreparedName> Names,
    IReadOnlyList<Guid> RetireAssignmentUuids,
    IReadOnlyList<Guid> RetireSupportUuids,
    SourceActorAliasScopeObservation? Observation,
    SourceActorAliasReplacementReadiness? Readiness
);

/// <summary>Every required claim/name must name an actual committed successful receipt before retirement.</summary>
public sealed record SourceActorAliasRequiredNameReceipt(
    SourceActorAliasClaimReference Claim,
    string Wording,
    Guid StageUuid,
    Guid AliasUuid,
    Guid AssignmentUuid,
    Guid SupportUuid
);

public sealed record SourceActorAliasReplacementReadiness(
    SourceActorAliasScopeObservation CompleteObservation,
    IReadOnlyList<SourceActorAliasRequiredNameReceipt> RequiredNames,
    Guid? PredecessorStageUuid,
    IReadOnlyList<SourceActorAliasAcceptanceReference> OriginalAuthorities
);

/// <summary>
/// Save before admission, attach one exact ticket, then publish by saved IDs. A retry cannot
/// refresh this body. Stale configured stages name both the exact configured predecessor and
/// the last committed map; admitted configuration is never mistaken for accepted projection.
/// </summary>
public sealed record SourceActorAliasPersonStage(
    Guid StageUuid,
    Guid WorkUuid,
    Guid OriginalPersonUuid,
    Guid OwnerSubjectUuid,
    Guid? PredecessorStageUuid,
    Guid? ConfiguredPredecessorStageUuid,
    long? ExpectedConfigurationGeneration,
    IReadOnlyList<NormalizationInputContributionRevision> ExpectedContributions,
    IReadOnlyList<NormalizationPreparedInputContribution> Contributions,
    IReadOnlyList<SourceActorAliasScopeMapEntry> PriorMap,
    IReadOnlyList<SourceActorAliasStageDelta> Deltas,
    SourceActorAliasStageFacts Facts,
    NormalizationWorkTicket? Ticket = null,
    SourceActorAliasStageReceipt? Receipt = null,
    string? TerminalReason = null
);

public enum SourceActorAliasNameDisposition
{
    Accepted,
    ExactDuplicate,
    Pending,
    ProtectedReviewedNaturalClaim,
    ConflictingUuid,
}

public sealed record SourceActorAliasNameOutcome(
    SourceActorAliasClaimReference Claim,
    Guid RequestedAliasUuid,
    string Wording,
    SourceActorAliasNameDisposition Disposition,
    string Reason,
    Guid? ActualAliasUuid,
    Guid? AssignmentUuid,
    Guid? SupportUuid
);

/// <summary>Saved atomically with canonical aliases, managed authority and Work's publication watermark.</summary>
public sealed record SourceActorAliasStageReceipt(
    Guid StageUuid,
    Guid WorkUuid,
    Guid OriginalPersonUuid,
    IReadOnlyList<SourceActorAliasNameOutcome> Names,
    IReadOnlyList<Guid> RetiredAssignmentUuids,
    IReadOnlyList<Guid> RetiredSupportUuids,
    IReadOnlyList<SourceActorAliasScopeMapEntry> ResultingMap,
    SourceActorAliasApplicationDisposition Disposition,
    string Reason
);

/// <summary>References independently bounded stage rows instead of serializing their proofs again.</summary>
public sealed record SourceActorAliasApplicationResult(
    Guid AttemptUuid,
    Guid SourceUuid,
    SourceActorAliasApplicationDisposition Disposition,
    string Reason,
    IReadOnlyList<Guid> CommittedStageUuids,
    IReadOnlyList<SourceActorAliasReplacementReadiness> ReplacementReadiness,
    bool Replayed = false
);

/// <summary>Bounded orchestration metadata. Full original proof and resulting maps are read by StageUuid.</summary>
public sealed record SourceActorAliasPreparedNameReference(
    SourceActorAliasClaimReference Claim,
    SourceActorAliasPreparedName Name
);

public sealed record SourceActorAliasStageReceiptHeader(
    IReadOnlyList<SourceActorAliasNameOutcome> Names,
    long RetiredAssignments,
    long RetiredSupports,
    SourceActorAliasApplicationDisposition Disposition,
    string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? CertificateVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? AuthenticatedRequestHash = null
);

public sealed record SourceActorAliasPersonStageHeader(
    Guid StageUuid,
    Guid WorkUuid,
    Guid OriginalPersonUuid,
    Guid OwnerSubjectUuid,
    Guid? PredecessorStageUuid,
    bool Admitted,
    IReadOnlyList<SourceActorAliasPreparedNameReference> PreparedNames,
    IReadOnlyList<SourceActorAliasAcceptanceReference> Acceptances,
    SourceActorAliasStageReceiptHeader? Receipt,
    string? TerminalReason
);

public sealed record SourceActorAliasApplication(
    Guid AttemptUuid,
    Guid ResolutionAttemptUuid,
    Guid SourceUuid,
    string Version,
    SourceActorAliasApplicationPlan? Plan,
    IReadOnlyList<SourceActorAliasPersonStageHeader> Stages,
    SourceActorAliasApplicationResult? Result
);
