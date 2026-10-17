using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

public enum NormalizationRecordingLookupMode
{
    LegacyCatalog,
    RetainedProvider,
}

/// <summary>
/// A legacy request names the canonical legacy_catalog/recording/source-UUID resource and
/// has no input. A retained provider request pins an actual immutable input revision; its
/// recording identity must equal that input resource. Input revisions belong to attempts,
/// while mode, recording identity and expected artist identify the durable registration.
/// </summary>
public sealed record NormalizationRecordingLookupRequest(
    NormalizationRecordingLookupMode Mode,
    NormalizationResourceKey Resource,
    Guid? ExpectedArtistUuid = null,
    NormalizationBindingInputReference? Input = null
);

public sealed record NormalizationRecordingLookupRegistration(
    Guid LookupUuid,
    NormalizationRecordingLookupMode Mode,
    NormalizationResourceKey Resource,
    Guid? ExpectedArtistUuid
);

/// <summary>
/// The first complete preparation wins. Binding and AbsenceProbe are mutually exclusive.
/// Legacy bindings retain the original capture receipt and producer clocks; only a bound
/// legacy lookup carries a prepared extraction handoff. Lookup configuration CAS is separate
/// from the extraction owner's CAS inside LegacyPreparation. Downstream IDs and the legacy processing version
/// are fixed before admission so recovery cannot create another processing attempt.
/// </summary>
public sealed record NormalizationRecordingLookupPreparation(
    NormalizationRecordingBindingSnapshot? Binding,
    NormalizationSourceUuidLookup? AbsenceProbe,
    long? ExpectedConfigurationGeneration,
    Guid? FetchUuid = null,
    IReadOnlyList<NormalizationCatalogDependency>? ProducerCatalogInputs = null,
    NormalizationRecordingPreparation? LegacyPreparation = null,
    Guid? ProcessingAttemptUuid = null,
    string? ProcessingVersion = null
);

public enum NormalizationRecordingLookupDisposition
{
    Published,
    Stale,
}

/// <summary>Published records a lookup outcome, not an accepted normalized identity or extraction.</summary>
public sealed record NormalizationRecordingLookupResult(
    Guid AttemptUuid,
    Guid LookupUuid,
    NormalizationRecordingLookupDisposition Disposition,
    NormalizationCatalogBindingStatus? Status,
    string Reason,
    Guid? HandoffUuid = null,
    bool Replayed = false
);

public sealed record NormalizationRecordingLookupAttempt(
    Guid AttemptUuid,
    NormalizationRecordingLookupRegistration Registration,
    NormalizationRecordingLookupRequest Request,
    string ProcessingVersion,
    NormalizationRecordingLookupPreparation? Preparation,
    NormalizationRecordingLookupResult? Result
);

/// <summary>
/// Durable delivery references the lookup's immutable preparation rather than storing another
/// copy. LegacyPreparation is populated on read from that preparation. For retained providers,
/// ProcessingVersion stays null until a provider consumer has an admitted version. Retained provider
/// handoffs remain pending until a provider consumer exists; no legacy refresh is substituted.
/// </summary>
public sealed record NormalizationRecordingLookupHandoff(
    Guid HandoffUuid,
    Guid LookupAttemptUuid,
    NormalizationRecordingLookupMode Mode,
    Guid SourceUuid,
    Guid ProcessingAttemptUuid,
    string? ProcessingVersion,
    NormalizationRecordingPreparation? LegacyPreparation,
    NormalizationRecordingProcessingResult? CompletedResult
);
