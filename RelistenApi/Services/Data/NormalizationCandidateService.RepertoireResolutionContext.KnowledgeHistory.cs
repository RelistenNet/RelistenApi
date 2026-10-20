using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    /// <summary>
    /// Follows an automatic correspondence back to its immutable publication. Withdrawing a
    /// review removes that review's authority, not the independent data that preceded it.
    /// History is read in the candidate transaction; current rows remain available for write CAS.
    /// </summary>
    private sealed class RepertoireKnowledgeHistory(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid artist,
        RepertoireKnowledgeIndex knowledge,
        CancellationToken ct
    )
    {
        private readonly Dictionary<Guid, RepertoireResolutionStage?> stages = [];
        private readonly HashSet<Guid> observedRules = [];
        private int depth;
        private bool expertSupported;

        public async Task<IndependentRepertoireCorrespondences> ReadAsync(
            RepertoireResolutionAssociation? association,
            IReadOnlyList<ImportedSongAssignmentChange> imported,
            IReadOnlyList<MusicExternalIdentityChange> external
        )
        {
            expertSupported = false;
            var previous = await IndependentAssociationAsync(association);
            var retainedImported = new List<ImportedSongAssignmentChange>();
            var retiredImported = new List<Guid>();
            foreach (var value in imported.OrderBy(value => value.Uuid))
            {
                var retained = await IndependentImportedAsync(value);
                if (retained != value)
                {
                    retiredImported.Add(value.Uuid);
                }
                if (retained != null)
                {
                    retainedImported.Add(retained);
                }
            }
            var retainedExternal = new List<MusicExternalIdentityChange>();
            var retiredExternal = new List<Guid>();
            foreach (var value in external.OrderBy(value => value.Uuid))
            {
                var retained = await IndependentExternalAsync(value);
                if (retained != value)
                {
                    retiredExternal.Add(value.Uuid);
                }
                if (retained != null)
                {
                    retainedExternal.Add(retained);
                }
            }
            var changed =
                previous != association || retiredImported.Count + retiredExternal.Count > 0;
            return new(
                previous,
                changed
                    ? new(
                        association,
                        retiredImported,
                        retiredExternal,
                        retainedImported,
                        retainedExternal
                    )
                    : null,
                expertSupported
            );
        }

        public async Task ObserveOwnersAsync(CandidateFactsContext facts)
        {
            if (observedRules.Count == 0)
            {
                return;
            }
            var owners = await c.QueryAsync<Guid>(
                new CommandDefinition(
                    "SELECT subject_uuid FROM normalization.expert_knowledge_rules WHERE rule_uuid=ANY(@rules)",
                    new { rules = observedRules.ToArray() },
                    t,
                    cancellationToken: ct
                )
            );
            foreach (var owner in owners)
            {
                facts.AddOwner(owner);
            }
        }

        private async Task<RepertoireResolutionAssociation?> IndependentAssociationAsync(
            RepertoireResolutionAssociation? association
        )
        {
            var requested = association;
            var expiredAuthority = false;
            var visited = new HashSet<Guid>();
            while (association != null)
            {
                if (!visited.Add(association.DecisionUuid))
                {
                    throw new InvalidOperationException(
                        "The repertoire authority history contains a cycle."
                    );
                }
                // Ordinary retries do not create identity authority. Find their last explicit
                // decision in one indexed read; a long-lived source can have years of retries.
                if (association.Authority == "retained_correspondence")
                {
                    var origin = await ReadRepertoireAssociation(
                        c,
                        t,
                        association.RegistrationUuid,
                        association.CorrespondenceKey,
                        ct,
                        association.DecisionUuid,
                        skipRetained: true
                    );
                    if (origin != null)
                    {
                        association = origin;
                        continue;
                    }
                }
                if (
                    association.Authority
                    is not (
                        null
                        or "reviewed_expert_knowledge"
                        or "retained_correspondence"
                        or "evaluated_qualified_identity"
                    )
                )
                {
                    return expiredAuthority ? association : requested;
                }
                var stage = await ReadAssociationStageAsync(association);
                var decision = stage?.Decisions.SingleOrDefault(value =>
                    value.DecisionUuid == association.DecisionUuid
                );
                if (decision == null)
                {
                    return expiredAuthority ? association : requested;
                }
                var source = stage!.Context.Claims.Single(value =>
                    value.Claim.ClaimUuid == decision.ClaimUuid
                );
                if (decision.Authority == "reviewed_expert_knowledge")
                {
                    if (await StillAcceptedAsync(source, decision))
                    {
                        return expiredAuthority ? association : requested;
                    }
                    expiredAuthority = true;
                    association = source.ExistingAssociation;
                    continue;
                }
                if (decision.Authority == "evaluated_qualified_identity")
                {
                    if (await QualifiedSupportStillAcceptedAsync(source, decision))
                    {
                        return expiredAuthority ? association : requested;
                    }
                    expiredAuthority = true;
                    association = source.ExistingAssociation;
                    continue;
                }
                if (decision.Authority != "retained_correspondence")
                {
                    return expiredAuthority ? association : requested;
                }
                if (source.ExistingAssociation != null)
                {
                    association = source.ExistingAssociation;
                    continue;
                }
                foreach (
                    var assignment in source.ImportedAssignments.Where(value => !value.Retracted)
                )
                {
                    if (await IndependentImportedAsync(assignment) != assignment)
                    {
                        return null;
                    }
                }
                return expiredAuthority ? association : requested;
            }
            return null;
        }

        private async Task<ImportedSongAssignmentChange?> IndependentImportedAsync(
            ImportedSongAssignmentChange value
        )
        {
            if (value.Basis == "reviewed" || value.Retracted)
            {
                return value;
            }
            EnterHistory();
            try
            {
                var stage = await ReadAssertionStageAsync(
                    "imported_song_assignments",
                    value.Uuid,
                    value.ExpectedRevision
                );
                var source = stage?.Context.Claims.SingleOrDefault(context =>
                    context.Claim.ImportedSongUuid == value.ImportedSongUuid
                );
                var decision =
                    source == null
                        ? null
                        : stage!.Decisions.Single(item => item.ClaimUuid == source.Claim.ClaimUuid);
                if (
                    source == null
                    || decision == null
                    || await DecisionStillSupportedAsync(source, decision)
                )
                {
                    return value;
                }
                var previous = source.ImportedAssignments.SingleOrDefault(item =>
                    item.ImportedSongUuid == value.ImportedSongUuid && !item.Retracted
                );
                return previous == null ? null : await IndependentImportedAsync(previous);
            }
            finally
            {
                depth--;
            }
        }

        private async Task<MusicExternalIdentityChange?> IndependentExternalAsync(
            MusicExternalIdentityChange value
        )
        {
            if (value.Basis == "reviewed" || value.Retracted)
            {
                return value;
            }
            EnterHistory();
            try
            {
                var stage = await ReadAssertionStageAsync(
                    "music_external_identities",
                    value.Uuid,
                    value.ExpectedRevision
                );
                var purpose =
                    "external:"
                    + value.Provider
                    + ":"
                    + value.ResourceType
                    + ":"
                    + value.ProviderKey;
                var decision = stage?.Decisions.SingleOrDefault(item =>
                    NormalizationRepertoireResolutionProcessor.RelationshipUuid(
                        item.DecisionUuid,
                        purpose
                    ) == value.Uuid
                );
                var source =
                    decision == null
                        ? null
                        : stage!.Context.Claims.Single(item =>
                            item.Claim.ClaimUuid == decision.ClaimUuid
                        );
                if (
                    source == null
                    || decision == null
                    || await DecisionStillSupportedAsync(source, decision)
                )
                {
                    return value;
                }
                var previous = source.ExistingExternalIdentities.SingleOrDefault(item =>
                    !item.Retracted
                    && item.Provider == value.Provider
                    && item.ResourceType == value.ResourceType
                    && item.ProviderKey == value.ProviderKey
                );
                return previous == null ? null : await IndependentExternalAsync(previous);
            }
            finally
            {
                depth--;
            }
        }

        private Task<bool> DecisionStillSupportedAsync(
            RepertoireResolutionClaimContext source,
            RepertoireResolutionClaimDecision decision
        ) =>
            decision.Authority switch
            {
                "reviewed_expert_knowledge" => StillAcceptedAsync(source, decision),
                "evaluated_qualified_identity" => QualifiedSupportStillAcceptedAsync(
                    source,
                    decision
                ),
                _ => Task.FromResult(true),
            };

        private async Task<bool> QualifiedSupportStillAcceptedAsync(
            RepertoireResolutionClaimContext source,
            RepertoireResolutionClaimDecision decision
        )
        {
            var keys = source.Claim.AliasOf is { } alias
                ? new[] { alias.Identity }.ToHashSet()
                : source.Claim.Identities.Select(value => value.Identity).ToHashSet();
            foreach (
                var support in source.ExistingExternalIdentities.Where(value =>
                    !value.Retracted
                    && keys.Contains(new(value.Provider, value.ResourceType, value.ProviderKey))
                )
            )
            {
                var independent = await IndependentExternalAsync(support);
                if (
                    independent?.RepertoireSongUuid is { } stored
                    && decision.RepertoireSongUuid is { } selected
                    && (
                        stored == selected
                        || source.Candidates!.Facts.Any(candidate =>
                            candidate.Header.Uuid == selected
                            && candidate.IdentityClosure.Any(identity => identity.Uuid == stored)
                        )
                    )
                )
                {
                    return true;
                }
            }
            return false;
        }

        private async Task<bool> StillAcceptedAsync(
            RepertoireResolutionClaimContext source,
            RepertoireResolutionClaimDecision decision
        )
        {
            foreach (var rule in decision.ExpertKnowledge?.AppliedRules ?? [])
            {
                observedRules.Add(rule.RuleUuid);
            }
            var current = knowledge.Evaluate(source.Claim, artist);
            foreach (var rule in current.AppliedRules)
            {
                observedRules.Add(rule.RuleUuid);
            }
            if (current.Outcome != "reviewed_correspondence")
            {
                return false;
            }
            var bindings = await knowledge.TargetsAsync(c, t, artist, current, ct);
            var targets = bindings
                .Where(value => value.Target == current.Target)
                .SelectMany(value => value.RepertoireSongUuids)
                .Distinct()
                .ToArray();
            if (targets.Length != 1 || decision.RepertoireSongUuid is not { } oldTarget)
            {
                return false;
            }
            var roots = await ResolveTargets(
                c,
                t,
                DomainTables.For(IdentityMatchingDomain.Repertoire),
                [new RawHit { OriginUuid = oldTarget, Reason = "prior_expert_knowledge" }],
                ct
            );
            var accepted = roots.Any(root => root.Uuid == targets[0]);
            expertSupported |= accepted;
            return accepted;
        }

        private async Task<RepertoireResolutionStage?> ReadAssertionStageAsync(
            string kind,
            Guid uuid,
            long revision
        )
        {
            var work = await c.QuerySingleOrDefaultAsync<Guid?>(
                new CommandDefinition(
                    "SELECT decision_uuid FROM normalization.music_revisions WHERE entity_kind=@kind AND entity_uuid=@uuid AND revision=@revision",
                    new
                    {
                        kind,
                        uuid,
                        revision,
                    },
                    t,
                    cancellationToken: ct
                )
            );
            return work is { } id ? await ReadStageAsync(id) : null;
        }

        private async Task<RepertoireResolutionStage?> ReadAssociationStageAsync(
            RepertoireResolutionAssociation association
        )
        {
            var work = await c.QuerySingleOrDefaultAsync<Guid?>(
                new CommandDefinition(
                    """
                    SELECT work_uuid FROM normalization.repertoire_resolution_stages
                    WHERE attempt_uuid=@AttemptUuid AND (stage->>'Kind')::integer=3
                        AND stage->'Decisions' @> @decision::jsonb
                    """,
                    new
                    {
                        association.AttemptUuid,
                        decision = JsonSerializer.Serialize(
                            new[] { new { association.DecisionUuid } }
                        ),
                    },
                    t,
                    cancellationToken: ct
                )
            );
            return work is { } id ? await ReadStageAsync(id) : null;
        }

        private async Task<RepertoireResolutionStage?> ReadStageAsync(Guid work)
        {
            if (stages.TryGetValue(work, out var stage))
            {
                return stage;
            }
            var json = await c.QuerySingleOrDefaultAsync<string>(
                new CommandDefinition(
                    """
                    SELECT CASE WHEN octet_length(stage::text)<=33554432 THEN stage::text
                        ELSE 'history_projection_exceeds_bound' END
                    FROM normalization.repertoire_resolution_stages WHERE work_uuid=@work AND NOT superseded
                    """,
                    new { work },
                    t,
                    cancellationToken: ct
                )
            );
            if (json == "history_projection_exceeds_bound")
            {
                throw new InvalidOperationException(
                    "The repertoire authority history exceeds its bounded projection."
                );
            }
            stage =
                json == null ? null : JsonSerializer.Deserialize<RepertoireResolutionStage>(json);
            if (stage != null && stage.Context.ArtistUuid != artist)
            {
                throw new InvalidOperationException(
                    "The repertoire authority history belongs to another artist."
                );
            }
            stages.Add(work, stage);
            return stage;
        }

        private void EnterHistory()
        {
            if (++depth > 32)
            {
                throw new InvalidOperationException(
                    "The repertoire authority history exceeds its bounded depth."
                );
            }
        }
    }

    private sealed record IndependentRepertoireCorrespondences(
        RepertoireResolutionAssociation? Association,
        RepertoireExpertReevaluation? Effects,
        bool ExpertSupported
    );

    private static RepertoireCandidateFacts[] RemoveExpiredRepertoireIdentities(
        RepertoireCandidateFacts[] facts,
        RepertoireExpertReevaluation effects
    ) =>
        facts
            .Select(fact =>
                fact with
                {
                    ExternalIdentities = fact
                        .ExternalIdentities.Where(identity =>
                            !effects.ExternalIdentityUuids.Contains(
                                identity.Provenance.AssertionUuid
                            )
                        )
                        .ToArray(),
                }
            )
            .ToArray();
}
