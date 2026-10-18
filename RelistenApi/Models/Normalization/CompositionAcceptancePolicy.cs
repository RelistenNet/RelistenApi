using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// Immutable applicability contract for an independently evaluated correspondence rule. A
/// registration is evidence about a rule; only its reviewed active pointer permits publication.
/// ContentHash covers this typed record with ContentHash empty, including collection order.
/// </summary>
public sealed record CompositionAcceptancePolicyArtifact(
    Guid Uuid,
    string Version,
    string ContentHash,
    string ProcessorVersion,
    string FactsVersion,
    string QueryBuilderVersion,
    string DecoderVersion,
    string RuleId,
    bool FixtureOnly = false
);

public sealed record CompositionAcceptanceCapabilityEvaluation(int Cases, int Errors);

/// <summary>
/// Independent correspondence labels, never agreement between two extractors. The trusted host
/// authenticates ApprovedBy and verifies the named evaluation artifacts outside this payload.
/// ReceiptHash covers this record with ReceiptHash empty.
/// </summary>
public sealed record CompositionAcceptanceEvaluationReceipt(
    Guid Uuid,
    Guid ArtifactUuid,
    string ArtifactHash,
    string ReceiptHash,
    string DatasetHash,
    string SplitHash,
    string AdjudicationHash,
    string EvaluatorVersion,
    string IndependentEvaluator,
    string ApprovedBy,
    CompositionAcceptanceCapabilityEvaluation Matching,
    bool FixtureOnly = false
);

public sealed record RegisteredCompositionAcceptancePolicy(
    CompositionAcceptancePolicyArtifact Artifact,
    CompositionAcceptanceEvaluationReceipt Evaluation
);

/// <summary>Keep this original observation through admission and publication; do not freshen it.</summary>
public sealed record ActiveCompositionAcceptancePolicy(
    RegisteredCompositionAcceptancePolicy Policy,
    NormalizationSubjectObservation OwnerObservation
);

public sealed record CompositionAcceptancePolicyDecision(
    Guid OperationUuid,
    Guid SubjectUuid,
    long ExpectedEditorialRevision,
    Guid? ExpectedArtifactUuid,
    Guid? ArtifactUuid,
    string Reason
);

/// <summary>
/// Processor-derived retained observations for a single external work alternative. The
/// publisher authenticates the processor receipt; callers cannot publish by supplying facts.
/// A recording's performer does not assert any work author or complete author list.
/// </summary>
public sealed record CompositionCorrespondenceCandidate(
    Guid ExternalWorkMbid,
    MusicBrainzWorkObservation Work,
    IReadOnlyList<MusicBrainzRecordingObservation> Recordings,
    IReadOnlyList<string> Contradictions
);

/// <summary>
/// The local claim and every retained alternative evaluated together. ExternalUniverseComplete
/// remains false for bounded live search; it is not a positive-match veto. LocalCandidatesComplete
/// instead identifies truncation of the locally accepted candidate readset.
/// </summary>
public sealed record CompositionCorrespondenceFacts(
    Guid RepertoireSongUuid,
    ObservedValue<string> SourceTitle,
    CatalogWorkArtistContext Artist,
    IReadOnlyList<CompositionCorrespondenceCandidate> Candidates,
    bool LocalCandidatesComplete,
    bool ExternalUniverseComplete,
    IReadOnlyList<string> CoverageIssues,
    string ProcessorVersion,
    string FactsVersion,
    string QueryBuilderVersion,
    string DecoderVersion,
    IReadOnlyList<CompositionCandidateFacts>? LocalCandidates = null,
    RepertoireCandidateFacts? LocalRepertoire = null
);

public enum CompositionCorrespondenceAction
{
    NeedsReview,
    ProposeAcceptance,
}

/// <summary>Features and contradictions are evaluator output, never caller acceptance flags.</summary>
public sealed record CompositionCorrespondenceCandidateDecision(
    Guid ExternalWorkMbid,
    bool Eligible,
    IReadOnlyList<string> Features,
    IReadOnlyList<string> Contradictions,
    IReadOnlyList<NormalizedEvidenceReference> Evidence
);

public sealed record CompositionCorrespondenceDecision(
    CompositionCorrespondenceAction Action,
    Guid? ExternalWorkMbid,
    string RuleId,
    string Reason,
    IReadOnlyList<CompositionCorrespondenceCandidateDecision> Candidates
);
