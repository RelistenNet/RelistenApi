using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Relisten.Api.Models.Normalization;

/// <summary>Fixed local scorer parameters. Scores rank candidates; they are not probabilities.</summary>
public sealed record PeopleAcceptanceLinearScorer(
    double Intercept,
    double NameSimilarityWeight,
    double ExactNameWeight,
    double ArtistOverlapWeight,
    double MatchThreshold,
    double MinimumRunnerUpMargin,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        double ContactAgreementWeight = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        double ProfileAgreementWeight = 0
);

/// <summary>
/// Separately evaluated creation classifier. This version accepts a single alphabetic full-name
/// mention as a person only when no candidate remains plausible. It cannot classify groups or
/// establish co-reference between mentions. An absent capability leaves creation for review.
/// </summary>
public sealed record PeopleAcceptanceCreationCapability(
    string ClassifierVersion,
    double MaximumCandidateScore
);

/// <summary>
/// Immutable exported policy, never an executable plugin or acceptance authority by itself.
/// ContentHash is SHA-256 of the JSON record with ContentHash empty. Supported input forms
/// are exact provenance categories supplied by the authenticated application preparation.
/// </summary>
public sealed record PeopleAcceptancePolicyArtifact(
    Guid Uuid,
    string Version,
    string ContentHash,
    string ScorerVersion,
    string RetrievalVersion,
    string CandidateFactsVersion,
    string ExtractionVersion,
    string CreditContextVersion,
    IReadOnlyList<string> InputForms,
    PeopleAcceptanceLinearScorer Scorer,
    PeopleAcceptanceCreationCapability? Creation = null,
    bool FixtureOnly = false
);

/// <summary>Independent whole-pool labels, including errors, not extraction agreement.</summary>
public sealed record PeopleAcceptanceCapabilityEvaluation(int Cases, int Errors);

/// <summary>
/// Trusted evaluation attestation. Dataset, split and adjudication hashes identify immutable
/// external evidence. The command host authenticates ApprovedBy independently of this payload.
/// ReceiptHash covers this record with ReceiptHash empty. Creation needs separate identity and
/// actor-kind labels; matching labels alone cannot authorize allocation.
/// </summary>
public sealed record PeopleAcceptanceEvaluationReceipt(
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
    PeopleAcceptanceCapabilityEvaluation Matching,
    PeopleAcceptanceCapabilityEvaluation? Creation = null,
    PeopleAcceptanceCapabilityEvaluation? ActorKind = null,
    bool FixtureOnly = false
);

/// <summary>Registration preserves the artifact and its verified evaluation as one immutable value.</summary>
public sealed record RegisteredPeopleAcceptancePolicy(
    PeopleAcceptancePolicyArtifact Artifact,
    PeopleAcceptanceEvaluationReceipt Evaluation
);

/// <summary>
/// Active artifact and original policy-owner observation read in one primary snapshot. Retain
/// this observation through Work admission and publication; never replace it with a newer stamp.
/// </summary>
public sealed record ActivePeopleAcceptancePolicy(
    RegisteredPeopleAcceptancePolicy Policy,
    NormalizationSubjectObservation OwnerObservation
);

/// <summary>Trusted CAS review command. Null ArtifactUuid revokes automatic publication.</summary>
public sealed record PeopleAcceptancePolicyDecision(
    Guid OperationUuid,
    Guid SubjectUuid,
    long ExpectedEditorialRevision,
    Guid? ExpectedArtifactUuid,
    Guid? ArtifactUuid,
    string Reason
);

public sealed record PeopleAcceptanceCandidateScore(
    Guid PersonUuid,
    double Score,
    bool Eligible,
    IReadOnlyList<string> RejectionReasons
);

/// <summary>Allocate these IDs once and checkpoint before any publication or retry.</summary>
public sealed record PeopleAcceptanceCreationPlan(
    Guid PersonUuid,
    string Kind,
    string DisplayName,
    Guid CoReferenceGroupUuid
);

/// <summary>Evaluated proposal audit. Publication must authenticate facts and active policy again.</summary>
public sealed record PeopleAcceptanceClaimDecision(
    Guid ProposalUuid,
    PeopleResolutionAction Action,
    string Reason,
    Guid ArtifactUuid,
    string ArtifactHash,
    Guid EvaluationReceiptUuid,
    IReadOnlyList<PeopleAcceptanceCandidateScore> Candidates,
    Guid? PersonUuid = null,
    double? WinnerScore = null,
    double? RunnerUpMargin = null,
    PeopleAcceptanceCreationPlan? Creation = null
);
