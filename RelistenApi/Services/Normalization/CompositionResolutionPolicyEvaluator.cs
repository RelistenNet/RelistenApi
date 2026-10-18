using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Pure correspondence evaluation, not publication authority. The affirmative production rule
/// requires a cited title, an accepted catalog performer identity and an explicit recording-to-work
/// path. Fixture behavior remains separately named; independent qualification and registry
/// activation are required before either rule can authorize publication.
/// </summary>
public sealed partial class CompositionResolutionPolicyEvaluator
{
    public const string ProcessorVersion = "composition-resolution-processor-v2";
    public const string FactsVersion = "composition-correspondence-facts-v1";
    public const string FixtureRuleId = "fixture-title-performer-recording-work-v1";

    public const string AffirmativeRuleId = "title-accepted-performer-recording-work-v1";

    public static bool Supports(CompositionAcceptancePolicyArtifact artifact) =>
        (
            (artifact.FixtureOnly && artifact.RuleId == FixtureRuleId)
            || (!artifact.FixtureOnly && artifact.RuleId == AffirmativeRuleId)
        )
        && artifact.ProcessorVersion == ProcessorVersion
        && artifact.FactsVersion == FactsVersion
        && artifact.QueryBuilderVersion == MusicBrainzCandidateQueryBuilder.Version
        && artifact.DecoderVersion == NormalizationProviderDecoder.Version;

    public CompositionCorrespondenceDecision Evaluate(
        CompositionCorrespondenceFacts facts,
        RegisteredCompositionAcceptancePolicy policy
    )
    {
        var artifact = policy.Artifact;
        if (
            !Supports(artifact)
            || policy.Evaluation.FixtureOnly != artifact.FixtureOnly
            || facts.ProcessorVersion != artifact.ProcessorVersion
            || facts.FactsVersion != artifact.FactsVersion
            || facts.QueryBuilderVersion != artifact.QueryBuilderVersion
            || facts.DecoderVersion != artifact.DecoderVersion
        )
        {
            return Review(artifact.RuleId, "unsupported_policy_or_fact_versions", []);
        }

        if (
            !facts.LocalCandidatesComplete
            || facts.Candidates.Select(candidate => candidate.ExternalWorkMbid).Distinct().Count()
                != facts.Candidates.Count
        )
        {
            return Review(artifact.RuleId, "incomplete_or_inconsistent_local_candidate_pool", []);
        }

        if (artifact.RuleId == AffirmativeRuleId)
        {
            return EvaluateAffirmative(facts, artifact);
        }

        var candidates = facts.Candidates.Select(candidate => Compare(facts, candidate)).ToArray();
        var eligible = candidates.Where(candidate => candidate.Eligible).ToArray();
        if (eligible.Length != 1)
        {
            return Review(
                artifact.RuleId,
                eligible.Length == 0
                    ? "no_corroborated_candidate"
                    : "competing_corroborated_candidates",
                candidates
            );
        }

        // Live search cannot establish a complete external universe. This fixture rule needs
        // affirmative corroboration; search rank, missing rivals and page count never enter it.
        return new(
            CompositionCorrespondenceAction.ProposeAcceptance,
            eligible[0].ExternalWorkMbid,
            artifact.RuleId,
            "fixture_corroborated_recording_work_correspondence",
            candidates
        );
    }

    private static CompositionCorrespondenceCandidateDecision Compare(
        CompositionCorrespondenceFacts facts,
        CompositionCorrespondenceCandidate candidate
    )
    {
        var features = new List<string>();
        var contradictions = candidate.Contradictions.ToList();
        var evidence = new List<NormalizedEvidenceReference> { facts.SourceTitle.Evidence };
        if (
            candidate.ExternalWorkMbid == Guid.Empty
            || !HasIdentity(candidate.Work.Work.Identity, "work", candidate.ExternalWorkMbid)
        )
        {
            contradictions.Add("hydrated_work_identity_mismatch");
        }

        var title = ComparisonTitle(facts.SourceTitle.Value);
        if (title.Length == 0 || ComparisonTitle(candidate.Work.Work.Name?.Value) != title)
        {
            contradictions.Add("local_and_work_titles_differ");
        }
        else
        {
            features.Add("exact_local_and_work_title");
            evidence.Add(candidate.Work.Work.Name!.Evidence);
            evidence.Add(candidate.Work.Work.Identity.Evidence);
        }

        if (facts.Artist.ExternalArtistMbid is not { } artistMbid || artistMbid == Guid.Empty)
        {
            contradictions.Add("no_accepted_catalog_artist_musicbrainz_identity");
        }
        else
        {
            CompareRecordings(candidate, title, artistMbid, features, contradictions, evidence);
        }

        return new(
            candidate.ExternalWorkMbid,
            contradictions.Count == 0,
            features.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            contradictions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            evidence
                .Distinct()
                .OrderBy(value => value.InputRevisionUuid)
                .ThenBy(value => value.JsonPointer, StringComparer.Ordinal)
                .ThenBy(value => value.StartCodePoint)
                .ThenBy(value => value.EndCodePoint)
                .ThenBy(value => value.Quote, StringComparer.Ordinal)
                .ToArray()
        );
    }

    private static void CompareRecordings(
        CompositionCorrespondenceCandidate candidate,
        string title,
        Guid artistMbid,
        List<string> features,
        List<string> contradictions,
        List<NormalizedEvidenceReference> evidence
    )
    {
        var corroborated = false;
        foreach (var recording in candidate.Recordings)
        {
            if (
                !HasIdentity(recording.Recording.Identity, "recording")
                || ComparisonTitle(recording.Recording.Name?.Value) != title
            )
            {
                continue;
            }

            var credits =
                recording
                    .ArtistCredits?.Where(value =>
                        value.Artist is { } artist
                        && HasIdentity(artist.Identity, "artist", artistMbid)
                    )
                    .ToArray()
                ?? [];
            if (credits.Length == 0)
            {
                continue;
            }

            var relationships =
                recording.Relationships?.Where(IsForwardPerformance).ToArray() ?? [];
            var matching = relationships
                .Where(value =>
                    value.Target is { } target
                    && HasIdentity(target.Identity, "work", candidate.ExternalWorkMbid)
                )
                .ToArray();
            if (matching.Length == 0)
            {
                continue;
            }

            // A medley or other recording linked to several works needs another rule. A matching
            // relationship must not conceal an explicitly retained alternative work relationship.
            if (
                relationships.Any(value =>
                    value.Target is { } target
                    && !HasIdentity(target.Identity, "work", candidate.ExternalWorkMbid)
                )
            )
            {
                contradictions.Add("corroborating_recording_has_alternative_work_relationship");
                evidence.Add(recording.Origin.Evidence);
                evidence.AddRange(relationships.Select(value => value.Evidence));
                continue;
            }

            features.Add("exact_local_and_recording_title");
            features.Add("accepted_catalog_artist_matches_recording_performer_mbid");
            features.Add("explicit_forward_recording_performance_of_work");
            evidence.Add(recording.Recording.Name!.Evidence);
            evidence.Add(recording.Recording.Identity.Evidence);
            evidence.AddRange(credits.Select(value => value.Artist!.Identity.Evidence));
            evidence.AddRange(matching.Select(value => value.Evidence));
            corroborated = true;
        }

        // Inspect every relevant recording before deciding. A clean path cannot erase an
        // ambiguous medley encountered earlier or later in retained discovery order.
        if (!corroborated)
        {
            contradictions.Add("no_corroborated_recording_to_work_path");
        }
    }

    private static bool IsForwardPerformance(MusicBrainzRelationshipObservation relationship) =>
        relationship.Type?.Value == "performance"
        && relationship.Direction?.Value == "forward"
        && relationship.TargetType?.Value == "work";

    private static bool HasIdentity(
        ObservedProviderIdentity identity,
        string resourceType,
        Guid? expected = null
    ) =>
        identity.Identity.Provider == "musicbrainz.org"
        && identity.Identity.ResourceType == resourceType
        && Guid.TryParse(identity.Identity.ProviderKey, out var mbid)
        && mbid != Guid.Empty
        && (expected == null || mbid == expected);

    private static string ComparisonTitle(string? title) =>
        (title ?? "").Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant();

    private static CompositionCorrespondenceDecision Review(
        string ruleId,
        string reason,
        IReadOnlyList<CompositionCorrespondenceCandidateDecision> candidates
    ) => new(CompositionCorrespondenceAction.NeedsReview, null, ruleId, reason, candidates);
}
