using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// Immutable applicability contract. Registration records independently evaluated evidence;
/// only the reviewed active pointer authorizes a processor publication. No production rule is
/// implemented by this first fixture contract. Collection order participates in both hashes.
/// </summary>
public sealed record VenueAcceptancePolicyArtifact(
    Guid Uuid,
    string Version,
    string ContentHash,
    string ProcessorVersion,
    string SourceContextVersion,
    string CandidateFactsVersion,
    string MatcherVersion,
    string MatchingRuleId,
    string? CreationRuleId = null,
    bool FixtureOnly = false
);

public sealed record VenueAcceptanceCapabilityEvaluation(int Cases, int Errors);

/// <summary>
/// The command host verifies the named dataset, split and adjudication artifacts and
/// authenticates ApprovedBy independently. Draft labels and extractor agreement do not qualify.
/// Matching and creation require separate evaluations; neither authorizes merging identities.
/// </summary>
public sealed record VenueAcceptanceEvaluationReceipt(
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
    VenueAcceptanceCapabilityEvaluation Matching,
    VenueAcceptanceCapabilityEvaluation? Creation = null,
    bool FixtureOnly = false
);

public sealed record RegisteredVenueAcceptancePolicy(
    VenueAcceptancePolicyArtifact Artifact,
    VenueAcceptanceEvaluationReceipt Evaluation
);

/// <summary>Retain the original observation through admission and publication.</summary>
public sealed record ActiveVenueAcceptancePolicy(
    RegisteredVenueAcceptancePolicy Policy,
    NormalizationSubjectObservation OwnerObservation
);

/// <summary>
/// Absence is a registry decision input too. The owner observation fences activation after a
/// processor prepared without an active policy, including an inactive-to-active-to-inactive race.
/// </summary>
public sealed record VenueAcceptancePolicyState(
    ActiveVenueAcceptancePolicy? Active,
    NormalizationSubjectObservation OwnerObservation
);

public sealed record VenueAcceptancePolicyDecision(
    Guid OperationUuid,
    Guid SubjectUuid,
    long ExpectedEditorialRevision,
    Guid? ExpectedArtifactUuid,
    Guid? ArtifactUuid,
    string Reason
);

public enum VenueResolutionSourceScope
{
    Imported,
    Show,
}

/// <summary>
/// Processor-authenticated source, whole candidate pool and current/historical authorities.
/// Source keeps unknown kind, parent, date and geography unknown. A matcher proposal is only
/// an input feature. Publication loads the saved processor receipt rather than caller facts.
/// </summary>
public sealed record VenueCorrespondenceFacts(
    ProviderVenueObservation OriginalVenueObservation,
    VenueResolutionSourceScope SourceScope,
    VenueMatchRecord Source,
    IReadOnlyList<ImportedVenueAssignmentFact> Assignments,
    IReadOnlyList<VenueCandidateFacts> Candidates,
    IReadOnlyList<IdentityCandidateComparison> Comparisons,
    bool Complete,
    string ProcessorVersion,
    string SourceContextVersion,
    string CandidateFactsVersion,
    string MatcherVersion
);

public enum VenueCorrespondenceAction
{
    NeedsReview,
    ProposeMatch,
    ProposeCreation,
}

public sealed record VenueCorrespondenceCandidateDecision(
    Guid VenueUuid,
    bool Eligible,
    IReadOnlyList<string> Features,
    IReadOnlyList<string> Contradictions,
    IReadOnlyList<NormalizedEvidenceReference> Evidence
);

/// <summary>
/// A creation proposal contains evidenced kind and an explicitly resolved parent when needed.
/// UUID allocation and parent freshness belong to the processor's durable creation stage.
/// The fixture rule emits sites only; it cannot infer a room's parent from its name.
/// </summary>
public sealed record VenueResolutionCreationProposal(
    string DisplayName,
    string Kind,
    NormalizedEvidenceReference IdentityEvidence,
    VenueCreationParent? ParentTarget = null,
    NormalizationSubjectObservation? ParentOwner = null
);

public sealed record VenueCorrespondenceDecision(
    VenueCorrespondenceAction Action,
    Guid? VenueUuid,
    string RuleId,
    string Reason,
    IReadOnlyList<VenueCorrespondenceCandidateDecision> Candidates,
    VenueResolutionCreationProposal? Creation = null
);
