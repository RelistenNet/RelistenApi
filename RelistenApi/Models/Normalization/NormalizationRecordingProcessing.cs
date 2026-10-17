using System;
using System.Collections.Generic;

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
/// The original preparation for a catalog-recording attempt. FetchUuid recovers immutable
/// bytes and its original receipt; producer generations must never be replaced on recovery.
/// The configuration revision is captured before admission so retries use the same request.
/// </summary>
public sealed record NormalizationRecordingPreparation(
    Guid FetchUuid,
    IReadOnlyList<NormalizationCatalogDependency> ProducerCatalogInputs,
    NormalizationRecordingBindingSnapshot Binding,
    long? ExpectedConfigurationGeneration,
    long ExpectedContributionRevision
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
    NormalizationRecordingProcessingResult? Result
);

/// <summary>A stable proposal key identifies this evidenced mention, never a canonical person.</summary>
public sealed record NormalizationRecordingCreditProposal(
    Guid ProposalUuid,
    RetainedCreditMention Mention
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

/// <summary>Local extraction only. CandidateSearchStatus distinguishes an unrun search from an empty pool.</summary>
public sealed record NormalizationRecordingExtraction(
    IReadOnlyList<NormalizationRecordingCreditProposal> Credits,
    IReadOnlyList<RetainedCreditUnresolved> UnresolvedCredits,
    IReadOnlyList<NormalizationRecordingTitleProposal> Titles,
    IReadOnlyList<ProviderDecodeIssue> DecodeIssues
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
    NormalizationRecordingExtraction Extraction
);
