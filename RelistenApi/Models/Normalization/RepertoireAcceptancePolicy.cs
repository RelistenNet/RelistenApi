using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>Immutable, independently evaluated rule applicability; registration does not activate it.</summary>
public sealed record RepertoireAcceptancePolicyArtifact(
    Guid Uuid,
    string Version,
    string ContentHash,
    string ProcessorVersion,
    string SourceVersion,
    string CandidateFactsVersion,
    string MatcherVersion,
    IReadOnlyList<IdentityMatchRule> EnabledRules,
    bool FixtureOnly = false
);

public sealed record RepertoireAcceptanceCapabilityEvaluation(int Cases, int Errors);

/// <summary>
/// The trusted host verifies the named external evaluation artifacts and authenticates ApprovedBy.
/// Hashes cover the typed artifact/receipt with only their own hash field empty.
/// </summary>
public sealed record RepertoireAcceptanceEvaluationReceipt(
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
    RepertoireAcceptanceCapabilityEvaluation Matching,
    bool FixtureOnly = false
);

public sealed record RegisteredRepertoireAcceptancePolicy(
    RepertoireAcceptancePolicyArtifact Artifact,
    RepertoireAcceptanceEvaluationReceipt Evaluation
);

public sealed record ActiveRepertoireAcceptancePolicy(
    RegisteredRepertoireAcceptancePolicy Policy,
    NormalizationSubjectObservation OwnerObservation
);

/// <summary>Absence and activation share the same original owner observation.</summary>
public sealed record RepertoireResolutionPolicyState(
    ActiveRepertoireAcceptancePolicy? Active,
    NormalizationSubjectObservation OwnerObservation
);

public sealed record RepertoireAcceptancePolicyDecision(
    Guid OperationUuid,
    Guid SubjectUuid,
    long ExpectedEditorialRevision,
    Guid? ExpectedArtifactUuid,
    Guid? ArtifactUuid,
    string Reason
);
