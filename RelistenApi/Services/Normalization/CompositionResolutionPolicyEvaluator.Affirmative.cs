using System;
using System.Linq;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

public sealed partial class CompositionResolutionPolicyEvaluator
{
    private static CompositionCorrespondenceDecision EvaluateAffirmative(
        CompositionCorrespondenceFacts facts,
        CompositionAcceptancePolicyArtifact artifact
    )
    {
        var inventory = facts.RetainedRecordingInventory;
        if (inventory == null || !ConsistentRecordingInventory(facts, inventory))
        {
            return Review(
                artifact.RuleId,
                "incomplete_or_inconsistent_retained_recording_inventory",
                []
            );
        }
        var candidates = facts
            .Candidates.Select(candidate => CompareAffirmative(facts, candidate, inventory))
            .ToArray();
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
        // This is an affirmative correspondence rule. A bounded retrieval window neither
        // proves global uniqueness nor needs to claim an exhaustive author or work universe.
        return new(
            CompositionCorrespondenceAction.ProposeAcceptance,
            eligible[0].ExternalWorkMbid,
            artifact.RuleId,
            "affirmative_accepted_performer_recording_work_correspondence",
            candidates
        );
    }

    private static bool ConsistentRecordingInventory(
        CompositionCorrespondenceFacts facts,
        CompositionRetainedRecordingInventory inventory
    )
    {
        if (
            !inventory.Complete
            || inventory.Issues.Count > 0
            || inventory.ExpectedInputs.Select(input => input.InputRevisionUuid).Distinct().Count()
                != inventory.ExpectedInputs.Count
            || inventory.Recordings.Count != inventory.ExpectedInputs.Count
        )
        {
            return false;
        }
        foreach (var input in inventory.ExpectedInputs)
        {
            var rows = inventory
                .Recordings.Where(value =>
                    value.Observation.Origin.Evidence.InputRevisionUuid == input.InputRevisionUuid
                )
                .ToArray();
            if (input.InputRevisionUuid == Guid.Empty || rows.Length != 1)
            {
                return false;
            }
            var row = rows[0];
            var recording = row.Observation;
            if (
                row.Issues.Count > 0
                || !HasIdentity(recording.Recording.Identity, "recording")
                || recording.Origin.Scope != ProviderObservationScope.ResourceItem
                || recording.Origin.Item != input.Resource
                || recording.Recording.Identity.Identity != input.Resource
                || recording.Recording.Identity.Evidence.InputRevisionUuid
                    != input.InputRevisionUuid
                || recording.Recording.Evidence.InputRevisionUuid != input.InputRevisionUuid
                || recording.Recording.Name?.Evidence.InputRevisionUuid != input.InputRevisionUuid
                || !RecordingProvenanceIsConsistent(recording)
            )
            {
                return false;
            }
        }
        // Candidate projection cannot inject a clean recording that was not present in the
        // complete inventory. Compare the full typed observation, including its exact citations.
        return facts
            .Candidates.SelectMany(candidate => candidate.Recordings)
            .All(recording =>
                inventory.Recordings.Any(row =>
                    JsonSerializer.Serialize(row.Observation) == JsonSerializer.Serialize(recording)
                )
            );
    }

    private static CompositionCorrespondenceCandidateDecision CompareAffirmative(
        CompositionCorrespondenceFacts facts,
        CompositionCorrespondenceCandidate candidate,
        CompositionRetainedRecordingInventory inventory
    )
    {
        var decision = Compare(
            facts,
            candidate with
            {
                Recordings = inventory.Recordings.Select(row => row.Observation).ToArray(),
            }
        );
        var contradictions = decision.Contradictions.ToList();
        var evidence = decision.Evidence.ToList();
        var work = candidate.Work;
        if (facts.Artist.ArtistUuid == Guid.Empty || facts.RepertoireSongUuid == Guid.Empty)
        {
            contradictions.Add("missing_accepted_catalog_context");
        }
        if (
            work.Origin.Scope != ProviderObservationScope.ResourceItem
            || work.Origin.Item != work.Work.Identity.Identity
            || work.Origin.Evidence.InputRevisionUuid == Guid.Empty
            || work.Work.Identity.Evidence.InputRevisionUuid
                != work.Origin.Evidence.InputRevisionUuid
            || work.Work.Name?.Evidence.InputRevisionUuid != work.Origin.Evidence.InputRevisionUuid
            || !EntityProvenanceIsConsistent(work.Work, work.Origin.Evidence)
        )
        {
            contradictions.Add("hydrated_work_provenance_mismatch");
        }
        var title = ComparisonTitle(facts.SourceTitle.Value);
        foreach (var retained in inventory.Recordings)
        {
            var recording = retained.Observation;
            if (ComparisonTitle(recording.Recording.Name?.Value) != title)
            {
                continue;
            }
            if (recording.ArtistCredits == null)
            {
                contradictions.Add("matching_title_recording_performer_credits_unavailable");
                evidence.Add(recording.Origin.Evidence);
                continue;
            }
            var artist = facts.Artist.ExternalArtistMbid;
            if (
                artist == null
                || !recording.ArtistCredits.Any(credit =>
                    credit.Artist is { } performer
                    && HasIdentity(performer.Identity, "artist", artist)
                )
            )
            {
                continue;
            }
            if (recording.Relationships == null)
            {
                contradictions.Add("matching_recording_work_relationships_unavailable");
                evidence.Add(recording.Origin.Evidence);
                continue;
            }
            foreach (var relation in recording.Relationships)
            {
                // Classification fields are optional in the external representation. A
                // partially classified work relationship can conceal a rival even when the
                // decoder read every supplied field without issuing a diagnostic.
                if (
                    relation.Type == null
                    || relation.Direction == null
                    || relation.TargetType == null
                )
                {
                    contradictions.Add("matching_recording_has_incomplete_work_relationship");
                    evidence.Add(relation.Evidence);
                }
            }
            foreach (var relation in recording.Relationships.Where(IsForwardPerformance))
            {
                evidence.Add(relation.Evidence);
                evidence.Add(recording.Recording.Identity.Evidence);
                if (relation.Target is not { } target || !HasIdentity(target.Identity, "work"))
                {
                    contradictions.Add("matching_recording_has_malformed_work_relationship");
                }
                else if (!HasIdentity(target.Identity, "work", candidate.ExternalWorkMbid))
                {
                    // This target remains a known rival even when there is no hydrated
                    // candidate for it. Work hydration membership cannot erase this fact.
                    contradictions.Add("retained_matching_recording_targets_another_work");
                }
            }
        }
        return decision with
        {
            Eligible = contradictions.Count == 0,
            Contradictions = contradictions
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            Evidence = evidence
                .Distinct()
                .OrderBy(value => value.InputRevisionUuid)
                .ThenBy(value => value.JsonPointer, StringComparer.Ordinal)
                .ThenBy(value => value.StartCodePoint)
                .ThenBy(value => value.EndCodePoint)
                .ThenBy(value => value.Quote, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private static bool RecordingProvenanceIsConsistent(MusicBrainzRecordingObservation recording)
    {
        if (!EntityProvenanceIsConsistent(recording.Recording, recording.Origin.Evidence))
        {
            return false;
        }
        foreach (var credit in recording.ArtistCredits ?? [])
        {
            if (
                !EvidenceIsWithin(credit.Evidence, recording.Origin.Evidence)
                || (credit.Name != null && !EvidenceIsWithin(credit.Name.Evidence, credit.Evidence))
                || (
                    credit.Artist != null
                    && !EntityProvenanceIsConsistent(credit.Artist, credit.Evidence)
                )
            )
            {
                return false;
            }
        }
        foreach (var relation in recording.Relationships ?? [])
        {
            if (
                !EvidenceIsWithin(relation.Evidence, recording.Origin.Evidence)
                || (
                    relation.Type != null
                    && !EvidenceIsWithin(relation.Type.Evidence, relation.Evidence)
                )
                || (
                    relation.Direction != null
                    && !EvidenceIsWithin(relation.Direction.Evidence, relation.Evidence)
                )
                || (
                    relation.TargetType != null
                    && !EvidenceIsWithin(relation.TargetType.Evidence, relation.Evidence)
                )
                || (
                    relation.Target != null
                    && !EntityProvenanceIsConsistent(relation.Target, relation.Evidence)
                )
            )
            {
                return false;
            }
        }
        return true;
    }

    private static bool EntityProvenanceIsConsistent(
        MusicBrainzEntityReference entity,
        NormalizedEvidenceReference parent
    ) =>
        EvidenceIsWithin(entity.Evidence, parent)
        && EvidenceIsWithin(entity.Identity.Evidence, entity.Evidence)
        && (entity.Name == null || EvidenceIsWithin(entity.Name.Evidence, entity.Evidence));

    private static bool EvidenceIsWithin(
        NormalizedEvidenceReference evidence,
        NormalizedEvidenceReference parent
    ) =>
        evidence.InputRevisionUuid == parent.InputRevisionUuid
        && (
            evidence.JsonPointer == parent.JsonPointer
            || evidence.JsonPointer.StartsWith(parent.JsonPointer + "/", StringComparison.Ordinal)
        );
}
