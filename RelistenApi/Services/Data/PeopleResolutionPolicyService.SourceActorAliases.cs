using System;
using System.Linq;
using System.Text.Json;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class PeopleResolutionPolicyService
{
    /// <summary>Hash of the exact role-free applicability tuple, including every producer version.</summary>
    public static string ComputeSourceActorApplicabilityHash(
        SourceActorAliasPolicyApplicability applicability
    ) => Hash(JsonSerializer.Serialize(applicability));

    private static void ValidateSourceActorQualification(
        RegisteredPeopleAcceptancePolicy policy,
        bool requireCurrentVersions
    )
    {
        var declaresSource = policy.Artifact.InputForms.Contains(
            PeopleResolutionPolicyEvaluator.SourceInputForm,
            StringComparer.Ordinal
        );
        if (
            !declaresSource
            && policy.Artifact.SourceActorAliasApplicability == null
            && policy.Evaluation.SourceActorAliasEvaluation == null
        )
        {
            // Historical credit-only payloads retain their original validation and bytes.
            return;
        }
        if (!SourceActorQualificationIsValid(policy, requireCurrentVersions))
        {
            throw new ArgumentException(
                "Source actor matching requires its exact applicability tuple and independently evaluated source capability."
            );
        }
    }

    /// <summary>
    /// Checks source qualification and the enclosing artifact/receipt hashes. The registry
    /// retains common scorer/creation validation and authenticates the approver/evidence;
    /// this helper does not replace that complete registration contract. A caller-assembled
    /// optional capability is no substitute for its enclosing artifact/receipt linkage.
    /// Source matching grants no identity creation, classification or merge capability.
    /// </summary>
    internal static bool SourceActorQualificationIsValid(
        RegisteredPeopleAcceptancePolicy policy,
        bool requireCurrentVersions = true
    )
    {
        var artifact = policy.Artifact;
        var evaluation = policy.Evaluation;
        var applicability = artifact.SourceActorAliasApplicability;
        var source = evaluation.SourceActorAliasEvaluation;
        if (
            applicability == null
            || source == null
            || !artifact.InputForms.Contains(
                PeopleResolutionPolicyEvaluator.SourceInputForm,
                StringComparer.Ordinal
            )
            || applicability.InputForm != PeopleResolutionPolicyEvaluator.SourceInputForm
            || new[]
            {
                applicability.RecordingProcessingVersion,
                applicability.ExtractionVersion,
                applicability.AssertionVersion,
                applicability.ProjectionVersion,
                applicability.CandidateFactsVersion,
                applicability.RetrievalVersion,
                applicability.ScorerVersion,
            }.Any(string.IsNullOrWhiteSpace)
            || applicability.ExtractionVersion != artifact.ExtractionVersion
            || applicability.CandidateFactsVersion != artifact.CandidateFactsVersion
            || applicability.RetrievalVersion != artifact.RetrievalVersion
            || applicability.ScorerVersion != artifact.ScorerVersion
            || !SourceEvaluationIsValid(source)
            || source.ApplicabilityHash != ComputeSourceActorApplicabilityHash(applicability)
        )
        {
            return false;
        }
        if (
            artifact.Uuid == Guid.Empty
            || evaluation.Uuid == Guid.Empty
            || evaluation.ArtifactUuid != artifact.Uuid
            || string.IsNullOrWhiteSpace(artifact.Version)
            || string.IsNullOrWhiteSpace(evaluation.EvaluatorVersion)
            || string.IsNullOrWhiteSpace(evaluation.IndependentEvaluator)
            || string.IsNullOrWhiteSpace(evaluation.ApprovedBy)
            || artifact.ContentHash != ComputeArtifactHash(artifact)
            || evaluation.ArtifactHash != artifact.ContentHash
            || evaluation.ReceiptHash != ComputeEvaluationHash(evaluation)
            || !IsHash(evaluation.DatasetHash)
            || !IsHash(evaluation.SplitHash)
            || !IsHash(evaluation.AdjudicationHash)
            || evaluation.FixtureOnly != artifact.FixtureOnly
            || !ValidEvidence(evaluation.Matching)
        )
        {
            return false;
        }

        // Retained registrations stay structurally readable after a producer advances;
        // positive evaluation/activation must target every current contract exactly.
        return !requireCurrentVersions
            || (
                applicability.RecordingProcessingVersion == NormalizationRecordingProcessor.Version
                && applicability.ExtractionVersion == NormalizationRecordingExtractor.Version
                && applicability.AssertionVersion == SourceActorAliasExtractor.Version
                && applicability.ProjectionVersion == PeopleActorProjection.SourceAliasVersion
                && applicability.CandidateFactsVersion
                    == NormalizationCandidateService.PeopleFactsVersion
                && applicability.RetrievalVersion
                    == PeopleResolutionPolicyEvaluator.RetrievalVersion
                && applicability.ScorerVersion == PeopleResolutionPolicyEvaluator.ScorerVersion
            );
    }

    private static bool SourceEvaluationIsValid(SourceActorAliasPolicyEvaluation source) =>
        IsHash(source.ApplicabilityHash)
        && IsHash(source.DatasetHash)
        && IsHash(source.SplitHash)
        && IsHash(source.AdjudicationHash)
        && !string.IsNullOrWhiteSpace(source.EvaluatorVersion)
        && !string.IsNullOrWhiteSpace(source.IndependentEvaluator)
        && ValidEvidence(source.Matching);
}
