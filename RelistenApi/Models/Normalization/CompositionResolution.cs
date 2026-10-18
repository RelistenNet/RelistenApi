using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>The exact accepted local claim; callers cannot supply matching facts or a winner.</summary>
public sealed record CatalogWorkClaimReference(Guid RepertoireSongUuid, Guid MusicRevisionUuid);

public sealed record CatalogWorkArtistContext(
    Guid ArtistUuid,
    string? Name,
    Guid? ExternalArtistMbid
);

/// <summary>Original primary-snapshot clocks. No family is silently discarded when a Work stage is admitted.</summary>
public sealed record CompositionResolutionReadSet(
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

public sealed record CompositionResolutionExclusion(
    Guid DecisionUuid,
    string ClaimHash,
    MusicPublication Publication
);

public sealed record CompositionResolutionContext(
    CatalogWorkClaimReference Claim,
    Guid ArtistUuid,
    CatalogWorkArtistContext Artist,
    ObservedValue<string>? LocalTitle,
    RepertoireCandidateFacts Repertoire,
    NormalizationCandidateFactsSnapshot<CompositionCandidateFacts> Candidates,
    CompositionResolutionReadSet ReadSet,
    IReadOnlyList<string> Issues,
    IReadOnlyList<CompositionResolutionExclusion>? ReviewedExclusions = null,
    NormalizationInputContributionsSnapshot? OwnerMembership = null,
    CompositionResolutionReadSet? OwnerBindings = null,
    IReadOnlyList<NormalizationBindingInputReference>? OwnerInputReferences = null
);

/// <summary>The source context and active evaluated policy are frozen before acquisition begins.</summary>
public sealed record CompositionResolutionPreparation(
    CompositionResolutionContext Context,
    MusicBrainzSearchVariants Variants,
    ActiveCompositionAcceptancePolicy? Policy
);

public sealed record CompositionSearchPage(
    Guid CommandUuid,
    MusicBrainzSearchPageRequest Request,
    MusicBrainzSearchAcquisition? Acquisition
);

public sealed record CompositionResolutionSearchCheckpoint(
    IReadOnlyList<CompositionSearchPage> Pages,
    IReadOnlyList<string> Issues,
    bool Complete,
    bool ObservedEnd = false
)
{
    public bool ExternalUniverseComplete => false;
}

public sealed record CompositionHydrationLookup(
    Guid CommandUuid,
    MusicBrainzLookupRequest Request,
    MusicBrainzAcquisition? Acquisition
);

public sealed record CompositionResolutionHydrationCheckpoint(
    IReadOnlyList<CompositionHydrationLookup> Lookups,
    IReadOnlyList<string> Issues,
    bool Complete
);

public enum CompositionResolutionPhase
{
    Search,
    Hydration,
    Evaluate,
    CreateComposition,
    Link,
}

/// <summary>Append-only progress; each row has exactly the typed payload belonging to its phase.</summary>
public sealed record CompositionResolutionCheckpoint(
    long Ordinal,
    CompositionResolutionPhase Phase,
    CompositionResolutionSearchCheckpoint? Search = null,
    CompositionResolutionHydrationCheckpoint? Hydration = null
);

public enum CompositionResolutionStageKind
{
    CreateComposition,
    Link,
}

/// <summary>
/// A frozen domain write and original admission expectations. DerivedContext records a reread
/// after this attempt's own accepted creation; it never replaces the preparation's source clocks.
/// </summary>
public sealed record CompositionResolutionStage(
    Guid WorkUuid,
    CompositionResolutionStageKind Kind,
    MusicPublication Publication,
    CompositionResolutionReadSet ReadSet,
    NormalizationWorkTicket? Ticket = null,
    NormalizationPublicationResult? Receipt = null,
    CompositionResolutionContext? DerivedContext = null
);

public enum CompositionResolutionDisposition
{
    Pending,
    Proposed,
    Applied,
    Review,
    Unresolved,
    Stale,
}

public sealed record CompositionResolutionResult(
    Guid AttemptUuid,
    CompositionResolutionDisposition Disposition,
    string Reason,
    IReadOnlyList<NormalizationPublicationResult> Publications,
    bool Replayed = false
);

/// <summary>Saved receipts are historical observations, not claims that the original read set is still fresh.</summary>
public sealed record CompositionResolutionAttempt(
    Guid AttemptUuid,
    CatalogWorkClaimReference Claim,
    string ProcessingVersion,
    Guid? PreviousAttemptUuid,
    CompositionResolutionPreparation? Preparation,
    IReadOnlyList<CompositionResolutionCheckpoint> Checkpoints,
    EvaluatedCatalogWorkCorrespondenceReceipt? Proposal,
    IReadOnlyList<CompositionResolutionStage> Stages,
    CompositionResolutionResult? Result
);

/// <summary>Issued only by the processor and loaded by the publication route; caller-held JSON is not authority.</summary>
public sealed record EvaluatedCatalogWorkCorrespondenceReceipt(
    Guid ReceiptUuid,
    Guid AttemptUuid,
    string ProcessorVersion,
    CatalogWorkClaimReference Claim,
    Guid? ExternalWorkMbid,
    Guid? CompositionUuid,
    Guid LinkUuid,
    CompositionCorrespondenceFacts Facts,
    CompositionCorrespondenceDecision Decision,
    ActiveCompositionAcceptancePolicy? Policy,
    CompositionResolutionReadSet ReadSet,
    long ExpectedCompositionRevision = 0,
    long ExpectedLinkRevision = 0,
    CompositionResolutionContext? Context = null
);
