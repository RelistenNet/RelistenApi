using System;
using System.Collections.Generic;
using System.Linq;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace Relisten.Normalization;

/// <summary>
/// Evaluates the entire prepared pool, not an isolated matcher proposal. Its implemented rules
/// are disposable-fixture classifiers demonstrating the publication protocol. Literal locality
/// equality here is explicitly not geographic resolution or a production identity rule.
/// </summary>
public sealed class VenueResolutionPolicyEvaluator
{
    public const string ProcessorVersion = "venue-resolution-processor-v1";
    public const string SourceContextVersion = "venue-resolution-context-v1";
    public const string MatcherVersion = "venue-pair-history-v1";
    public const string FixtureRuleId = "fixture-exact-name-raw-locality-v1";
    public const string FixtureCreationRuleId = "fixture-explicit-site-marker-v1";

    public static bool Supports(VenueAcceptancePolicyArtifact artifact) =>
        artifact.FixtureOnly
        && artifact.MatchingRuleId == FixtureRuleId
        && (artifact.CreationRuleId == null || artifact.CreationRuleId == FixtureCreationRuleId)
        && artifact.ProcessorVersion == ProcessorVersion
        && artifact.SourceContextVersion == SourceContextVersion
        && artifact.CandidateFactsVersion == NormalizationCandidateService.FactsVersion
        && artifact.MatcherVersion == MatcherVersion;

    public VenueCorrespondenceDecision Evaluate(
        VenueCorrespondenceFacts facts,
        RegisteredVenueAcceptancePolicy policy
    )
    {
        var artifact = policy.Artifact;
        if (
            !Supports(artifact)
            || !policy.Evaluation.FixtureOnly
            || facts.ProcessorVersion != artifact.ProcessorVersion
            || facts.SourceContextVersion != artifact.SourceContextVersion
            || facts.CandidateFactsVersion != artifact.CandidateFactsVersion
            || facts.MatcherVersion != artifact.MatcherVersion
        )
        {
            return Review(artifact.MatchingRuleId, "unsupported_policy_or_fact_versions", []);
        }

        if (
            !facts.Complete
            || facts.Candidates.Select(value => value.Header.Uuid).Distinct().Count()
                != facts.Candidates.Count
            || facts.Comparisons.Count != facts.Candidates.Count
            || facts.Comparisons.Any(value => value.Domain != IdentityMatchingDomain.Venues)
            || !facts
                .Candidates.Select(value => (Guid?)value.Header.Uuid)
                .Order()
                .SequenceEqual(facts.Comparisons.Select(value => value.CandidateUuid).Order())
        )
        {
            return Review(artifact.MatchingRuleId, "incomplete_or_inconsistent_candidate_pool", []);
        }

        var decisions = facts.Candidates.Select(candidate => Compare(facts, candidate)).ToArray();
        var eligible = decisions.Where(value => value.Eligible).ToArray();
        if (eligible.Length == 1)
        {
            return new(
                VenueCorrespondenceAction.ProposeMatch,
                eligible[0].VenueUuid,
                artifact.MatchingRuleId,
                "fixture_unique_supported_venue",
                decisions
            );
        }

        if (eligible.Length > 1)
        {
            return Review(artifact.MatchingRuleId, "competing_supported_venues", decisions);
        }

        return EvaluateCreation(facts, policy, decisions);
    }

    private static VenueCorrespondenceCandidateDecision Compare(
        VenueCorrespondenceFacts facts,
        VenueCandidateFacts candidate
    )
    {
        var features = new List<string>();
        var contradictions = new List<string>();
        var evidence = new List<NormalizedEvidenceReference>();
        var comparison = facts.Comparisons.Single(value =>
            value.CandidateUuid == candidate.Header.Uuid
        );
        contradictions.AddRange(
            comparison
                .Features.Where(value => value.Agreement == IdentityFeatureAgreement.Conflicts)
                .Select(value => "matcher_conflict:" + value.Name)
        );

        CompareAuthorities(facts, candidate.Header.Uuid, contradictions);
        if (
            !string.IsNullOrEmpty(facts.Source.Kind)
            && !string.IsNullOrEmpty(candidate.Header.Kind)
            && facts.Source.Kind != candidate.Header.Kind
        )
        {
            contradictions.Add("known_venue_kind_conflict");
        }

        if (facts.Source.ParentVenueUuid is { } parent)
        {
            var parents = candidate
                .Parents.Select(value => value.ResolvedParentVenueUuid)
                .Distinct()
                .ToArray();
            if (parents.Length != 1 || parents[0] != parent)
            {
                contradictions.Add("known_parent_site_conflict");
            }
        }

        var matchingNames = candidate
            .Names.Where(name =>
                Literal(name.Wording).Length > 0
                && facts.OriginalVenueObservation.Names.Any(source =>
                    LiteralSourceName(source.Value) == Literal(name.Wording)
                )
                && AppliesAt(name.ValidFrom, name.ValidTo, facts.Source.ObservedDate)
            )
            .ToArray();
        if (matchingNames.Length > 0)
        {
            features.Add("fixture_exact_literal_name");
            evidence.AddRange(matchingNames.SelectMany(value => value.Provenance.Evidence));
            evidence.AddRange(facts.OriginalVenueObservation.Names.Select(value => value.Evidence));
        }

        var sourceLocality = facts.OriginalVenueObservation.OriginalLocality;
        var matchingLocations = candidate
            .Locations.Where(location =>
                sourceLocality != null
                && Literal(sourceLocality.Value).Length > 0
                && Literal(sourceLocality.Value) == Literal(location.OriginalLocality)
                && AppliesAt(location.ValidFrom, location.ValidTo, facts.Source.ObservedDate)
            )
            .ToArray();
        if (matchingLocations.Length > 0)
        {
            features.Add("fixture_exact_literal_raw_locality");
            evidence.Add(sourceLocality!.Evidence);
            evidence.AddRange(matchingLocations.SelectMany(value => value.Provenance.Evidence));
        }

        // A favorable account cannot hide another applicable location account. Without an
        // event date every retained historical account remains an alternative, not a current pin.
        if (
            sourceLocality != null
            && candidate.Locations.Any(location =>
                AppliesAt(location.ValidFrom, location.ValidTo, facts.Source.ObservedDate)
                && Literal(location.OriginalLocality).Length > 0
                && Literal(location.OriginalLocality) != Literal(sourceLocality.Value)
            )
        )
        {
            contradictions.Add("alternative_raw_locality_account");
        }

        var qualifiedIdentity =
            comparison.SupportedRules.Contains(IdentityMatchRule.QualifiedEntityIdentifier)
            && facts.Source.ExternalIdentities.Any(source =>
                source.IdentifiesEntity
                && source.Provider != "legacy_catalog"
                && candidate.ExternalIdentities.Any(target =>
                    source.Provider == target.Provider
                    && source.ResourceType == target.ResourceType
                    && source.ProviderKey == target.ProviderKey
                )
            );
        if (qualifiedIdentity)
        {
            features.Add("qualified_physical_entity_identifier");
            evidence.AddRange(
                candidate.ExternalIdentities.SelectMany(value => value.Provenance.Evidence)
            );
            evidence.AddRange(
                facts.OriginalVenueObservation.Identities.Select(value => value.Evidence)
            );
        }

        var corroborated =
            qualifiedIdentity || (matchingNames.Length > 0 && matchingLocations.Length > 0);
        if (!corroborated)
        {
            features.Add("insufficient_supported_correspondence");
        }

        return new(
            candidate.Header.Uuid,
            corroborated && contradictions.Count == 0,
            features.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            contradictions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            evidence.Distinct().ToArray()
        );
    }

    private static void CompareAuthorities(
        VenueCorrespondenceFacts facts,
        Guid target,
        List<string> contradictions
    )
    {
        var current = facts.Assignments.Where(value => !value.IsHistorical).ToArray();
        if (current.Any(value => value.State == "accepted" && value.ResolvedVenueUuid != target))
        {
            contradictions.Add("different_current_assignment");
        }
        if (
            current.Any(value =>
                value.Basis == "reviewed"
                && value.State == "rejected"
                && value.ResolvedVenueUuid == target
            )
        )
        {
            contradictions.Add("reviewed_target_exclusion");
        }
    }

    private static VenueCorrespondenceDecision EvaluateCreation(
        VenueCorrespondenceFacts facts,
        RegisteredVenueAcceptancePolicy policy,
        IReadOnlyList<VenueCorrespondenceCandidateDecision> decisions
    )
    {
        if (
            policy.Artifact.CreationRuleId != FixtureCreationRuleId
            || policy.Evaluation.Creation == null
        )
        {
            return Review(policy.Artifact.MatchingRuleId, "no_supported_venue", decisions);
        }

        // Creation is a separately evaluated capability. A fixture marker supplies explicit
        // kind evidence; ordinary imported wording never defaults to a site or guesses a parent.
        var names = facts.OriginalVenueObservation.Names;
        if (
            names.Count != 1
            || !names[0].Value.StartsWith("[site] ", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(names[0].Value[7..])
        )
        {
            return Review(
                policy.Artifact.CreationRuleId,
                "no_explicit_fixture_site_kind",
                decisions
            );
        }
        if (
            facts.Source.Kind is not ("" or "site")
            || facts.Source.ParentVenueUuid != null
            || facts.Candidates.Count > 0
            || facts.Assignments.Any(value => !value.IsHistorical)
        )
        {
            return Review(
                policy.Artifact.CreationRuleId,
                "creation_has_existing_alternatives_or_authority",
                decisions
            );
        }

        return new(
            VenueCorrespondenceAction.ProposeCreation,
            null,
            policy.Artifact.CreationRuleId,
            "fixture_explicit_site_marker",
            decisions,
            new(names[0].Value[7..].Trim(), "site", names[0].Evidence)
        );
    }

    private static bool AppliesAt(DateOnly? from, DateOnly? to, DateOnly? date) =>
        date == null || ((from == null || from <= date) && (to == null || to >= date));

    private static string Literal(string value) => value.Trim();

    // The fixture's explicit site marker supplies kind evidence rather than display wording.
    // Strip exactly one original prefix, matching the creation classifier, while retaining the
    // original observation and its citation. Ordinary provider wording is never interpreted here.
    private static string LiteralSourceName(string value) =>
        Literal(value.StartsWith("[site] ", StringComparison.Ordinal) ? value[7..] : value);

    private static VenueCorrespondenceDecision Review(
        string ruleId,
        string reason,
        IReadOnlyList<VenueCorrespondenceCandidateDecision> candidates
    ) => new(VenueCorrespondenceAction.NeedsReview, null, ruleId, reason, candidates);
}
