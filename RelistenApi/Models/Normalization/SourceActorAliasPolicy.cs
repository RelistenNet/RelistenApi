using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// Separately evaluated input capability. Exact producer and projection identities are part of
/// its hash; a credit matching attestation or an InputForms string supplies none of this authority.
/// This capability never authorizes identity creation, kind classification, merging or attendance.
/// </summary>
public sealed record SourceActorAliasPolicyApplicability(
    string InputForm,
    string RecordingProcessingVersion,
    string ExtractionVersion,
    string AssertionVersion,
    string ProjectionVersion,
    string CandidateFactsVersion,
    string RetrievalVersion,
    string ScorerVersion
);

/// <summary>
/// Independent source-assertion labels and split/adjudication evidence, separate from credit
/// matching evaluation. ApplicabilityHash covers only the exact source capability tuple.
/// The registry host authenticates the approver and referenced evidence; this is no active policy.
/// </summary>
public sealed record SourceActorAliasPolicyEvaluation(
    string ApplicabilityHash,
    string DatasetHash,
    string SplitHash,
    string AdjudicationHash,
    string EvaluatorVersion,
    string IndependentEvaluator,
    PeopleAcceptanceCapabilityEvaluation Matching
);

/// <summary>
/// An original role-free assertion and its complete actual comparison inventory. The resolver
/// must authenticate original source coordinates/readsets before using this pure evaluation;
/// this DTO has no accepted identity authority and carries no fake recording credit or role.
/// </summary>
public sealed record SourceActorAliasPolicyInput(
    SourceActorAliasAssertion<NormalizedEvidenceReference> Assertion,
    Guid ArtistUuid,
    NormalizationCandidateFactsSnapshot<PersonCandidateFacts> Candidates,
    IReadOnlyList<IdentityCandidateComparison> Comparisons
);

public enum SourceActorAliasPolicyAction
{
    MatchExisting,
    Review,
    Unresolved,
}

/// <summary>Proposal audit only. Publication must authenticate the saved source, policy and clocks.</summary>
public sealed record SourceActorAliasPolicyDecision(
    Guid AssertionUuid,
    SourceActorAliasPolicyAction Action,
    string Reason,
    Guid ArtifactUuid,
    string ArtifactHash,
    Guid EvaluationReceiptUuid,
    IReadOnlyList<PeopleAcceptanceCandidateScore> Candidates,
    Guid? PersonUuid = null,
    double? WinnerScore = null,
    double? RunnerUpMargin = null
);
