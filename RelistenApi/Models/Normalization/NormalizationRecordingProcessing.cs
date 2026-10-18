using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Relisten.Api.Models.Normalization;

/// <summary>Extraction outcomes, not evidence that identities have been matched or accepted.</summary>
public enum NormalizationRecordingProcessingDisposition
{
    Published,
    Missing,
    Unresolved,
    Stale,
}

/// <summary>
/// The original complete preparation for a recording attempt. Binding and Extraction in the
/// batch identify the initiating input, not a preferred account. Additional inputs preserve
/// the other contributors' claims. FetchUuid is real for legacy snapshots and absent for raw
/// provider bytes; original producer generations must never be replaced on recovery.
/// </summary>
public sealed record NormalizationRecordingPreparation(
    Guid? FetchUuid,
    IReadOnlyList<NormalizationCatalogDependency> ProducerCatalogInputs,
    NormalizationRecordingBindingSnapshot Binding,
    long? ExpectedConfigurationGeneration,
    long ExpectedContributionRevision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        NormalizationResourceKey? Contributor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<NormalizationRecordingPreparedInput>? AdditionalInputs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<NormalizationInputContributionRevision>? ExpectedContributions = null
);

/// <summary>
/// One contributor's complete recording input, with its original catalog receipt when the
/// facts were copied from the legacy catalog. Provider bytes have no invented fetch receipt.
/// </summary>
public sealed record NormalizationRecordingPreparedInput(
    NormalizationResourceKey Contributor,
    long ExpectedContributionRevision,
    NormalizationRecordingBindingSnapshot Binding,
    Guid? FetchUuid = null,
    IReadOnlyList<NormalizationCatalogDependency>? ProducerCatalogInputs = null
);

/// <summary>An exact attempt replay returns its saved outcome; changed facts require a new attempt UUID.</summary>
public sealed record NormalizationRecordingProcessingResult(
    Guid AttemptUuid,
    Guid SourceUuid,
    NormalizationRecordingProcessingDisposition Disposition,
    string Reason,
    bool Replayed = false
);

/// <summary>
/// A durable attempt has its request, optional immutable preparation and optional terminal result.
/// Cancellation or a transient failure leaves an unfinished attempt resumable.
/// </summary>
public sealed record NormalizationRecordingAttempt(
    Guid AttemptUuid,
    Guid SourceUuid,
    string ProcessingVersion,
    NormalizationRecordingPreparation? Preparation,
    NormalizationRecordingProcessingResult? Result,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        Guid? InitiatingHandoffUuid = null
);

/// <summary>A stable proposal key identifies this evidenced mention, never a canonical person.</summary>
public sealed record NormalizationRecordingCreditProposal(
    Guid ProposalUuid,
    RetainedCreditMention Mention,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ProviderObservationOrigin? FileOrigin = null
);

/// <summary>
/// A bound file's original title and extracted mentions. Missing titles remain explicit.
/// Segment offsets refer to OriginalTitle; they are not audio times or performance positions.
/// </summary>
public sealed record NormalizationRecordingTitleProposal(
    Guid ProposalUuid,
    Guid TrackUuid,
    ObservedValue<string>? Title,
    TrackTitleExtraction? Extraction,
    string? Reason
);

/// <summary>
/// Role-free source assertions from one original recording or file account. The assertion
/// UUID is scoped by this producer, retained input and field; the enclosing attempt supplies
/// publication identity. FileOrigin is absent for recording-wide/root-account prose, including
/// when that prose accompanies files with unresolved catalog bindings.
/// </summary>
public sealed record NormalizationRecordingSourceActorAliasScope(
    string ProducerVersion,
    Guid InputRevisionUuid,
    NormalizationResourceKey Resource,
    ProviderObservationOrigin RecordingOrigin,
    SourceActorAliasExtraction<NormalizedEvidenceReference> Extraction,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ProviderObservationOrigin? FileOrigin = null
);

/// <summary>Local extraction only. CandidateSearchStatus distinguishes an unrun search from an empty pool.</summary>
public sealed record NormalizationRecordingExtraction(
    IReadOnlyList<NormalizationRecordingCreditProposal> Credits,
    IReadOnlyList<RetainedCreditUnresolved> UnresolvedCredits,
    IReadOnlyList<NormalizationRecordingTitleProposal> Titles,
    IReadOnlyList<ProviderDecodeIssue> DecodeIssues,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<NormalizationRecordingSourceActorAliasScope>? SourceActorAliases = null
)
{
    public string CandidateSearchStatus => "not_run";
}

/// <summary>
/// A completed local extraction with the exact evidence and catalog read sets consumed.
/// An attempt can retain this batch before publication or after becoming stale. Reading it
/// does not establish current identity matches. It contains no accepted people, repertoire,
/// composition or occurrence assignments.
/// </summary>
public sealed record NormalizationRecordingProposalBatch(
    Guid AttemptUuid,
    Guid SourceUuid,
    Guid ArtistUuid,
    string ProcessingVersion,
    NormalizationRecordingPreparation Preparation,
    NormalizationWorkTicket Ticket,
    NormalizationRecordingExtraction Extraction,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<NormalizationRecordingInputExtraction>? AdditionalExtractions = null
);

/// <summary>
/// Preserves one input's proposals separately. Several accounts can cite the same catalog
/// track with different titles; their input-specific claims must remain available together.
/// </summary>
public sealed record NormalizationRecordingInputExtraction(
    Guid InputRevisionUuid,
    NormalizationRecordingExtraction Extraction
);
