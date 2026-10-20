using System;
using System.Collections.Generic;
using System.Linq;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

public sealed partial class RepertoireResolutionPolicyEvaluator
{
    /// <summary>
    /// Runtime review selectors use the source's stable correspondence key, exact observed title,
    /// admitted artist and provider. Frozen-reader account keys keep their own capture scope.
    /// </summary>
    public static ExpertKnowledgeObservation KnowledgeObservation(
        RepertoireResolutionClaim claim,
        Guid artist
    ) =>
        new(
            "songs",
            "repertoire",
            claim.CorrespondenceKey,
            claim.Title?.Value,
            artist,
            claim.Origin.Item.Provider
        );

    private static Guid[] KnowledgeTargets(
        RepertoireResolutionClaimContext context,
        IEnumerable<ExpertKnowledgeTarget> targets
    )
    {
        var selected = targets.ToHashSet();
        return (context.ExpertTargets ?? [])
            .Where(binding => selected.Contains(binding.Target))
            .SelectMany(binding => binding.RepertoireSongUuids)
            .Distinct()
            .Order()
            .ToArray();
    }

    private static RepertoireResolutionClaimDecision? ReviewedKnowledgeDecision(
        RepertoireResolutionClaimContext context,
        ExpertKnowledgeEvaluation? knowledge
    )
    {
        if (knowledge?.Outcome == "conflict")
        {
            return Decision(
                context,
                RepertoireResolutionClaimDisposition.Review,
                "expert_knowledge_conflict"
            );
        }
        if (
            knowledge?.ExcludedTargets.Any(target =>
                KnowledgeTargets(context, [target]).Length != 1
            ) == true
        )
        {
            return Decision(
                context,
                RepertoireResolutionClaimDisposition.Review,
                "expert_exclusion_unbound_or_ambiguous"
            );
        }
        if (knowledge?.Outcome != "reviewed_correspondence")
        {
            return null;
        }
        var targets = KnowledgeTargets(context, [knowledge.Target!]);
        if (targets.Length != 1)
        {
            return Decision(
                context,
                RepertoireResolutionClaimDisposition.Review,
                "expert_target_unbound_or_ambiguous"
            );
        }
        var target = targets[0];
        if (
            context.ImportedAssignments.Any(value =>
                !value.Retracted
                && value.Basis == "reviewed"
                && !CorrespondsTo(context, value.RepertoireSongUuid, target)
            )
            || context.ExistingExternalIdentities.Any(value =>
                !value.Retracted
                && value.Basis == "reviewed"
                && context.Claim.Identities.Any(identity =>
                    identity.Identity.Provider == value.Provider
                    && identity.Identity.ResourceType == value.ResourceType
                    && identity.Identity.ProviderKey == value.ProviderKey
                )
                && (
                    value.RepertoireSongUuid is not { } stored
                    || !CorrespondsTo(context, stored, target)
                )
            )
        )
        {
            return Decision(
                context,
                RepertoireResolutionClaimDisposition.Review,
                "expert_and_reviewed_correspondence_conflict"
            );
        }
        return Decision(
            context,
            RepertoireResolutionClaimDisposition.Bound,
            "reviewed_expert_song_correspondence",
            target,
            "reviewed_expert_knowledge"
        );
    }

    private static bool HasIndependentIdentity(
        RepertoireResolutionClaimContext context,
        RepertoireCandidateFacts candidate,
        NormalizationResourceKey key
    ) =>
        candidate.ExternalIdentities.Any(identity => SameIdentity(key, identity))
        || (context.ExpertReevaluation?.IndependentExternalIdentities ?? []).Any(identity =>
            !identity.Retracted
            && identity.Provider == key.Provider
            && identity.ResourceType == key.ResourceType
            && identity.ProviderKey == key.ProviderKey
            && identity.RepertoireSongUuid is { } stored
            && CorrespondsTo(context, stored, candidate.Header.Uuid)
        );

    private static bool CorrespondsTo(
        RepertoireResolutionClaimContext context,
        Guid stored,
        Guid target
    ) =>
        stored == target
        || context.Candidates!.Facts.Any(candidate =>
            candidate.Header.Uuid == target
            && candidate.IdentityClosure.Any(identity => identity.Uuid == stored)
        );
}
