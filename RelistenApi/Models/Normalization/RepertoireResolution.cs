using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Relisten.Api.Models.Normalization;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ImportedRepertoireResolutionSource), "imported_dictionary")]
[JsonDerivedType(typeof(RecordingRepertoireResolutionSource), "recording_titles")]
[JsonDerivedType(typeof(SetlistRepertoireResolutionSource), "setlist_passages")]
[JsonDerivedType(typeof(ProviderRepertoireResolutionSource), "provider_dictionary")]
public abstract record RepertoireResolutionSource;

public sealed record ImportedRepertoireResolutionSource(Guid FetchUuid)
    : RepertoireResolutionSource;

public sealed record RecordingRepertoireResolutionSource(Guid PublishedProcessingAttemptUuid)
    : RepertoireResolutionSource;

public sealed record SetlistRepertoireResolutionSource(Guid PublishedProcessingAttemptUuid)
    : RepertoireResolutionSource;

public sealed record ProviderRepertoireResolutionSource(
    NormalizationBindingInputReference Input,
    NormalizationResourceKey AccountIdentity
) : RepertoireResolutionSource;

/// <summary>Exact retained source coordinates. ExpectedArtistUuid constrains, never supplies, ownership.</summary>
public sealed record RepertoireResolutionRequest(
    RepertoireResolutionSource Source,
    Guid? ExpectedArtistUuid = null
);

public sealed record RepertoireResolutionRegistration(
    Guid RegistrationUuid,
    NormalizationResourceKey Lineage,
    Guid? ExpectedArtistUuid,
    Guid? CurrentAttemptUuid
);

public sealed record RepertoireResolutionReadSet(
    IReadOnlyList<NormalizationInputDependency> Inputs,
    IReadOnlyList<NormalizationSubjectDependency> Dependencies,
    IReadOnlyList<NormalizationCatalogDependency> CatalogInputs,
    IReadOnlyList<NormalizationSourceIdentityDependency> SourceIdentities,
    NormalizationMappingRegistryDependency? MappingRegistry,
    IReadOnlyList<NormalizationImportedVenueDependency> ImportedVenues,
    IReadOnlyList<NormalizationCandidateUniverseDependency> CandidateUniverses,
    IReadOnlyList<NormalizationSubjectObservation> OwnerObservations,
    IReadOnlyList<NormalizationSourceUuidDependency> SourceUuids
);

/// <summary>Identity of this exact claim is independent of a repertoire or performance occurrence UUID.</summary>
public sealed record RepertoireResolutionClaim(
    Guid ClaimUuid,
    string CorrespondenceKey,
    ObservedValue<string>? Title,
    string Kind,
    ProviderObservationOrigin Origin,
    IReadOnlyList<ObservedProviderIdentity> Identities,
    IReadOnlyList<ObservedValue<string>> Aliases,
    ObservedProviderIdentity? AliasOf = null,
    Guid? ImportedSongUuid = null,
    Guid? TrackUuid = null,
    Guid? ProposalUuid = null
);

/// <summary>Complete authenticated source and original clocks; saved once before candidate selection.</summary>
public sealed record RepertoireResolutionSourceSnapshot(
    NormalizationResourceKey Lineage,
    Guid? ArtistUuid,
    IReadOnlyList<RepertoireResolutionClaim> Claims,
    RepertoireResolutionReadSet ReadSet,
    string SourceVersion,
    IReadOnlyList<string> Issues,
    int? TotalClaims = null,
    bool ClaimsOmitted = false
);

public sealed record RepertoireResolutionAssociation(
    Guid DecisionUuid,
    Guid RegistrationUuid,
    Guid AttemptUuid,
    Guid ClaimUuid,
    string CorrespondenceKey,
    Guid? RepertoireSongUuid,
    RepertoireResolutionClaimDisposition Disposition,
    string Reason,
    IReadOnlyList<NormalizedEvidenceReference> Evidence
);

public sealed record RepertoireResolutionOwnerContext(
    NormalizationInputContributionsSnapshot Membership,
    RepertoireResolutionReadSet Bindings,
    IReadOnlyList<NormalizationBindingInputReference> InputReferences
);

public sealed record RepertoireResolutionClaimContext(
    RepertoireResolutionClaim Claim,
    NormalizationCandidateFactsSnapshot<RepertoireCandidateFacts>? Candidates,
    RepertoireResolutionAssociation? ExistingAssociation,
    IReadOnlyList<ImportedSongAssignmentChange> ImportedAssignments,
    IReadOnlyList<RepertoireSongChange> ExistingSongs,
    IReadOnlyList<MusicExternalIdentityChange> ExistingExternalIdentities,
    IReadOnlyList<MusicPublication> ReviewedExclusions
);

/// <summary>Page facts and complete owner membership share one primary repeatable-read transaction.</summary>
public sealed record RepertoireResolutionPageContext(
    Guid? ArtistUuid,
    IReadOnlyList<RepertoireResolutionClaimContext> Claims,
    RepertoireResolutionReadSet ReadSet,
    RepertoireResolutionOwnerContext ObservationOwner,
    RepertoireResolutionOwnerContext? ArtistOwner,
    RepertoireResolutionPolicyState Policy,
    IReadOnlyList<string> Issues,
    bool FreshnessComplete = true
);

public sealed record RepertoireResolutionPreparation(
    RepertoireResolutionSourceSnapshot Source,
    RepertoireResolutionPolicyState Policy,
    int PageSize = 100
);

public enum RepertoireResolutionClaimDisposition
{
    Bound,
    Provisional,
    Review,
    Unresolved,
}

public sealed record RepertoireResolutionClaimDecision(
    Guid DecisionUuid,
    Guid ClaimUuid,
    string CorrespondenceKey,
    RepertoireResolutionClaimDisposition Disposition,
    string Reason,
    Guid? RepertoireSongUuid,
    IReadOnlyList<NormalizedEvidenceReference> Evidence,
    string Authority,
    IReadOnlyList<Guid> CandidateUuids
);

public enum RepertoireResolutionStageKind
{
    AcceptedWrite,
    RetireArtistContribution,
    AcknowledgeSource,
    DecisionPage,
}

/// <summary>Saved before admission. Source read clocks never change during own-write recovery.</summary>
public sealed record RepertoireResolutionStage(
    Guid StageUuid,
    Guid WorkUuid,
    RepertoireResolutionStageKind Kind,
    int PageIndex,
    NormalizationSubjectKey Owner,
    RepertoireResolutionPageContext Context,
    MusicPublication Publication,
    IReadOnlyList<RepertoireResolutionClaimDecision> Decisions,
    RepertoireResolutionReadSet ReadSet,
    NormalizationWorkTicket? Ticket = null,
    NormalizationPublicationResult? Receipt = null,
    bool Superseded = false,
    bool ContextOmitted = false,
    string? FailureReason = null,
    RepertoireResolutionOwnEffects? OwnEffects = null
);

/// <summary>Exact post-write watermarks captured under the accepted stage's existing locks.</summary>
public sealed record RepertoireResolutionOwnEffects(
    IReadOnlyList<NormalizationCandidateUniverseDependency> CandidateUniverses,
    NormalizationSubjectObservation ArtistOwner
);

public enum RepertoireResolutionDisposition
{
    Pending,
    Completed,
    Stale,
}

public sealed record RepertoireResolutionResult(
    Guid AttemptUuid,
    RepertoireResolutionDisposition Disposition,
    string Reason,
    int Bound,
    int Provisional,
    int Review,
    int Unresolved,
    bool Replayed = false
);

public sealed record RepertoireResolutionAttempt(
    Guid AttemptUuid,
    RepertoireResolutionRegistration Registration,
    RepertoireResolutionRequest Request,
    string ProcessingVersion,
    Guid? PreviousAttemptUuid,
    RepertoireResolutionPreparation? Preparation,
    IReadOnlyList<RepertoireResolutionStage> Stages,
    RepertoireResolutionResult? Result,
    long CheckpointRevision = 0
);

public sealed record RepertoireResolutionRecoveryPage(
    IReadOnlyList<RepertoireResolutionAttempt> Attempts,
    Guid? NextCursor
);

public sealed record RepertoireResolutionDecisionPage(
    IReadOnlyList<RepertoireResolutionAssociation> Decisions,
    Guid? NextCursor
);

public sealed record RepertoireResolutionRegistrationPage(
    IReadOnlyList<RepertoireResolutionRegistration> Registrations,
    Guid? NextCursor
);
