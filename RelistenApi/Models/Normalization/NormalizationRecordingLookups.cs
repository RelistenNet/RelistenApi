using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

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
/// Local directory requests retain their actual root reference in SupportingInputs; the
/// root is contextual evidence for that directory account, not another recording to process.
/// </summary>
public sealed record NormalizationRecordingLookupRequest(
    NormalizationRecordingLookupMode Mode,
    NormalizationResourceKey Resource,
    Guid? ExpectedArtistUuid = null,
    NormalizationBindingInputReference? Input = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<NormalizationBindingInputReference>? SupportingInputs = null
);

public sealed record NormalizationRecordingLookupRegistration(
    Guid LookupUuid,
    NormalizationRecordingLookupMode Mode,
    NormalizationResourceKey Resource,
    Guid? ExpectedArtistUuid
);

/// <summary>
/// The first complete preparation wins. Binding and AbsenceProbe are mutually exclusive.
/// Bound lookups retain a stable downstream handoff ID. Legacy bindings also retain their
/// original capture receipt, producer clocks and extraction-owner CAS in LegacyPreparation.
/// Lookup configuration CAS is separate from that downstream CAS. The downstream ID and
/// legacy processing version are fixed before admission so recovery cannot create another
/// processing attempt; retained providers freeze their consumer version when delivery starts.
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
/// ProcessingVersion is null: the consumer freezes its version in the processing attempt header.
/// Retained input and supporting context are consumed unchanged; no legacy refresh is substituted.
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
