using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

public enum VenueResolutionRequestKind
{
    ImportedVenue,
    ShowObservation,
}

public enum VenueShowClaimSourceKind
{
    Recording,
    Setlist,
}

/// <summary>Exact published source coordinates. The source service derives the performance UUID.</summary>
public sealed record VenueShowClaimReference(
    VenueShowClaimSourceKind SourceKind,
    Guid PublishedAttemptUuid,
    Guid InputRevisionUuid,
    string VenueJsonPointer,
    int? AccountIndex = null
);

/// <summary>Callers identify a source claim, never a winner or a canonical venue.</summary>
public sealed record VenueResolutionRequest(
    VenueResolutionRequestKind Kind,
    Guid? ImportedVenueUuid = null,
    VenueShowClaimReference? Show = null
);

public sealed record VenueResolutionClaim(
    Guid? CatalogUuid,
    bool IsShow,
    ProviderVenueObservation Observation,
    NormalizedEvidenceReference Evidence,
    Guid? ArtistUuid = null,
    Guid? PublishedAttemptUuid = null,
    ObservedValue<DateOnly>? ObservedDate = null
);

/// <summary>A primary catalog existence probe and its retained imported-row generation.</summary>
public sealed record VenueResolutionAbsenceProbe(
    Guid ImportedVenueUuid,
    bool Exists,
    long Generation
);

/// <summary>Original source admission and association facts survive later matching preparation.</summary>
public sealed record VenueResolutionSource(
    VenueResolutionClaim? Claim,
    NormalizationWorkTicket? OriginalTicket,
    NormalizationSubjectObservation? ProposalOwner,
    VenueShowClaimReference? ShowReference = null,
    NormalizationRecordingBindingSnapshot? RecordingBinding = null,
    NormalizationSetlistBindingSnapshot? SetlistBinding = null,
    VenueResolutionAbsenceProbe? Absence = null,
    NormalizationImportedVenueBindingSnapshot? ImportedBinding = null
);

/// <summary>All original Work freshness families, including negative lookups and observational owners.</summary>
public sealed record VenueResolutionReadSet(
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

/// <summary>The previous observation owner and its complete original contributor state before a show handoff.</summary>
public sealed record VenueObservationSubscription(
    NormalizationInputContributionsSnapshot Membership,
    VenueResolutionReadSet Bindings,
    IReadOnlyList<NormalizationBindingInputReference> InputReferences
);

/// <summary>Assignments and all selected or assigned target facts from one primary snapshot.</summary>
public sealed record VenueResolutionContext(
    string Version,
    string ContextHash,
    NormalizationSubjectState? Owner,
    IReadOnlyList<ImportedVenueAssignmentFact> Assignments,
    IReadOnlyList<VenueCandidateFacts> AssignmentTargets,
    NormalizationCandidateFactsSnapshot<VenueCandidateFacts> Candidates,
    IReadOnlyList<ImportedVenueAssignmentFact>? FallbackAssignments = null,
    NormalizationCatalogDependency? CurrentCatalog = null,
    bool SourceCurrent = true,
    NormalizationInputContributionsSnapshot? OwnerMembership = null,
    VenueResolutionReadSet? OwnerBindings = null,
    IReadOnlyList<NormalizationBindingInputReference>? OwnerInputReferences = null,
    VenueObservationSubscription? ObservationSubscription = null
)
{
    public CandidateFactsReadSet ReadSet => Candidates.ReadSet;
    public bool Complete => Candidates.Complete && SourceCurrent;
}

public enum VenueResolutionPreparationStatus
{
    Ready,
    SourceUnbound,
    UnsupportedQuery,
}

/// <summary>Freeze source evidence, whole-pool alternatives and exact owner membership before evaluation.</summary>
public sealed record VenueResolutionPreparation(
    VenueResolutionSource Source,
    VenueResolutionContext Context,
    VenueResolutionReadSet ReadSet,
    ActiveVenueAcceptancePolicy? Policy,
    VenueResolutionPreparationStatus Status = VenueResolutionPreparationStatus.Ready,
    NormalizationSubjectObservation? PolicyOwnerObservation = null
);

/// <summary>A processor-issued decision is authority only when loaded and authenticated by the writer.</summary>
public sealed record VenueResolutionDecision(
    Guid AttemptUuid,
    string ProcessorVersion,
    VenueResolutionClaim Claim,
    VenueCorrespondenceFacts Facts,
    VenueCorrespondenceDecision Decision,
    ActiveVenueAcceptancePolicy? Policy,
    VenueResolutionReadSet ReadSet,
    Guid? TargetVenueUuid,
    Guid AssignmentUuid,
    long ExpectedAssignmentRevision = 0
);

public enum VenueResolutionStageKind
{
    CreateVenue,
    Assignment,
    AcknowledgeMissing,
    AcknowledgeDecision,
    SupersedeObservation,
}

public sealed record VenueResolutionAssignmentOutcome(
    Guid TargetVenueUuid,
    Guid? AcceptedAssignmentUuid,
    Guid? AcceptedVenueUuid,
    IReadOnlyList<Guid> PendingAssignmentUuids
)
{
    public bool Applied => AcceptedVenueUuid == TargetVenueUuid;
}

/// <summary>
/// A frozen owner-scoped publication. DecisionReadSet retains evaluation observations when a
/// post-creation context is needed; ReadSet is the complete admission expectation for this stage.
/// </summary>
public sealed record VenueResolutionStage(
    Guid WorkUuid,
    VenueResolutionStageKind Kind,
    VenuePublication Publication,
    VenueResolutionReadSet ReadSet,
    VenueResolutionReadSet DecisionReadSet,
    NormalizationInputContributionsSnapshot OwnerMembership,
    IReadOnlyList<NormalizationBindingInputReference> OwnerInputReferences,
    NormalizationWorkTicket? Ticket = null,
    NormalizationPublicationResult? Receipt = null,
    VenueResolutionContext? DerivedContext = null,
    VenueResolutionAssignmentOutcome? Outcome = null
);

public enum VenueResolutionDisposition
{
    Pending,
    Applied,
    Review,
    Unresolved,
    Missing,
    Stale,
}

public sealed record VenueResolutionResult(
    Guid AttemptUuid,
    VenueResolutionDisposition Disposition,
    string Reason,
    IReadOnlyList<NormalizationPublicationResult> Publications,
    bool Replayed = false
);

/// <summary>Saved results are historical observations and never freshen the original source clocks.</summary>
public sealed record VenueResolutionAttempt(
    Guid AttemptUuid,
    VenueResolutionRequest Request,
    string ProcessingVersion,
    Guid? PreviousAttemptUuid,
    VenueResolutionPreparation? Preparation,
    VenueResolutionDecision? Decision,
    IReadOnlyList<VenueResolutionStage> Stages,
    VenueResolutionResult? Result
);
