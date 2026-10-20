using System;
using System.Collections.Generic;
using System.Linq;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

public sealed partial class RepertoireResolutionPolicyEvaluator
{
    /// <summary>Whether the exact active, evaluated artifact authorizes artist-scoped title reuse.</summary>
    public static bool SupportsTitleReuse(ActiveRepertoireAcceptancePolicy? policy) =>
        policy != null
        && Supports(policy.Policy.Artifact)
        && policy.Policy.Artifact.EnabledRules.Contains(
            IdentityMatchRule.ArtistScopedNormalizedTitle
        );

    public RepertoireResolutionClaimDecision Decide(
        RepertoireResolutionClaimContext context,
        ActiveRepertoireAcceptancePolicy? policy
    ) => DecidePage([context], policy).Single();

    /// <summary>
    /// Plans an entire page before allocating shared titles. Saved allocations are used only
    /// when rechecking a durable page; publication separately verifies that each creation is
    /// new and that distinct creation groups cannot reuse an allocation. Source correspondences
    /// and reviewed exceptions retain priority over this evaluated title rule.
    /// </summary>
    public IReadOnlyList<RepertoireResolutionClaimDecision> DecidePage(
        IReadOnlyList<RepertoireResolutionClaimContext> contexts,
        ActiveRepertoireAcceptancePolicy? policy,
        IReadOnlyDictionary<Guid, Guid>? savedAllocations = null,
        ExpertKnowledgeSnapshot? expertKnowledge = null
    )
    {
        var knowledgeResolver =
            expertKnowledge == null ? null : new ExpertKnowledgeResolver(expertKnowledge);
        var decisions = contexts
            .Select(context =>
            {
                var knowledge = context.Candidates?.Query.ArtistUuid is { } artist
                    ? knowledgeResolver?.Evaluate(KnowledgeObservation(context.Claim, artist))
                    : null;
                return DecideWithoutTitle(context, policy, knowledge) with
                {
                    ExpertKnowledge = knowledge?.AppliedRules.Count > 0 ? knowledge : null,
                };
            })
            .ToArray();
        for (var index = 0; index < decisions.Length; index++)
        {
            var decision = decisions[index];
            if (
                CreatesSong(decision)
                && savedAllocations?.TryGetValue(decision.ClaimUuid, out var saved) == true
            )
            {
                decisions[index] = decision with { RepertoireSongUuid = saved };
            }
        }

        if (!SupportsTitleReuse(policy))
        {
            return decisions;
        }

        var page = new TitlePage(contexts, decisions);
        return contexts
            .Select((context, index) => page.Resolve(context, decisions[index]))
            .ToArray();
    }

    /// <summary>
    /// Keeps one immutable page-wide title graph and its original creation proposals. Resolution
    /// must not depend on which claim was processed first, including aliases across claims.
    /// </summary>
    private sealed class TitlePage
    {
        private readonly Dictionary<string, RepertoireCandidateFacts> existing;
        private readonly Dictionary<string, RepertoireResolutionClaimDecision> byClaimKey;
        private readonly ArtistSongTitleResolver resolver;
        private readonly ArtistSongTitleCandidate[] titleCandidates;

        public TitlePage(
            IReadOnlyList<RepertoireResolutionClaimContext> contexts,
            IReadOnlyList<RepertoireResolutionClaimDecision> decisions
        )
        {
            existing = contexts
                .SelectMany(context => context.Candidates?.Facts ?? [])
                .DistinctBy(candidate => candidate.Header.Uuid)
                .ToDictionary(candidate => CandidateKey(candidate.Header.Uuid));
            byClaimKey = decisions.ToDictionary(decision => ClaimKey(decision.ClaimUuid));
            var sourceCandidates = contexts
                .Select((context, index) => (context, decision: decisions[index]))
                .Where(value => CreatesSong(value.decision))
                .Select(value => new ArtistSongTitleCandidate(
                    ClaimKey(value.context.Claim.ClaimUuid),
                    value.context.Candidates!.Query.ArtistUuid!.Value,
                    value
                        .context.Claim.Aliases.Select(alias => alias.Value)
                        .Prepend(value.context.Claim.Title!.Value)
                        .ToArray(),
                    []
                ));
            titleCandidates = existing
                .Values.Select(TitleCandidate)
                .Concat(sourceCandidates)
                .ToArray();
            resolver = new ArtistSongTitleResolver(titleCandidates);
        }

        public RepertoireResolutionClaimDecision Resolve(
            RepertoireResolutionClaimContext context,
            RepertoireResolutionClaimDecision decision
        )
        {
            if (!CreatesSong(decision))
            {
                return decision;
            }

            var excludedTargets = KnowledgeTargets(
                context,
                decision.ExpertKnowledge?.ExcludedTargets ?? []
            );
            var applicable =
                excludedTargets.Length == 0
                    ? resolver
                    : new ArtistSongTitleResolver(
                        titleCandidates.Where(candidate =>
                            !excludedTargets.Any(uuid => candidate.EntityKey == CandidateKey(uuid))
                        )
                    );
            var group = applicable.Resolve(
                context.Candidates!.Query.ArtistUuid!.Value,
                context.Claim.Title!.Value
            );
            var targets = group
                .Members.Where(member => existing.ContainsKey(member.EntityKey))
                .ToArray();
            if (group.Kind == ArtistSongTitleResolutionKind.ConflictingWorks || targets.Length > 1)
            {
                return decision with
                {
                    Disposition = RepertoireResolutionClaimDisposition.Review,
                    Reason =
                        group.Kind == ArtistSongTitleResolutionKind.ConflictingWorks
                            ? "qualified_work_conflict"
                            : "existing_same_title_identities_require_merge",
                    RepertoireSongUuid = null,
                    Authority = "unresolved",
                };
            }

            if (targets.Length == 1)
            {
                var target = existing[targets[0].EntityKey];
                var excluded = context.ReviewedExclusions.Any(publication =>
                    publication.RepertoireSongs.Any(song => song.Uuid == target.Header.Uuid)
                );
                return decision with
                {
                    Disposition = excluded
                        ? RepertoireResolutionClaimDisposition.Review
                        : RepertoireResolutionClaimDisposition.Bound,
                    Reason = excluded ? "reviewed_music_exclusion" : "same_artist_normalized_title",
                    RepertoireSongUuid = excluded ? null : target.Header.Uuid,
                    Authority = excluded ? "unresolved" : "evaluated_artist_title",
                };
            }

            var representative = byClaimKey[group.Representative!.EntityKey];
            return representative.ClaimUuid == context.Claim.ClaimUuid
                ? decision
                : decision with
                {
                    Reason = "same_page_artist_title",
                    RepertoireSongUuid = representative.RepertoireSongUuid,
                    Authority = "evaluated_same_page_title",
                };
        }

        private static ArtistSongTitleCandidate TitleCandidate(
            RepertoireCandidateFacts candidate
        ) =>
            new(
                CandidateKey(candidate.Header.Uuid),
                candidate.ArtistUuid,
                candidate
                    .Titles.SelectMany(title => title.Aliases.Prepend(title.DisplayTitle))
                    .ToArray(),
                candidate
                    .CompositionLinks.Where(link => link.ResolvedCompositionUuid.HasValue)
                    .Select(link =>
                        "composition:" + link.ResolvedCompositionUuid!.Value.ToString("D")
                    )
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            );
    }

    private static bool CreatesSong(RepertoireResolutionClaimDecision decision) =>
        decision.Authority is "provider_dictionary_assertion" or "provisional_title";

    private static string CandidateKey(Guid uuid) => "song:" + uuid.ToString("D");

    private static string ClaimKey(Guid uuid) => "claim:" + uuid.ToString("D");
}
