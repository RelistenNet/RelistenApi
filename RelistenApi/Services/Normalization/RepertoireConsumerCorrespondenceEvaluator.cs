using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Pure comparison of authenticated complete source images. The consumer saves the result and
/// its exact facts before admission; the writer reconstructs them before granting authority.
/// Registration and reviewed activation require independent evaluation. Fixture artifacts
/// are restricted separately by the registry; no production artifact is supplied here.
/// </summary>
public sealed class RepertoireConsumerCorrespondenceEvaluator
{
    public const string Version = "music-usage-correspondence-evaluator-v1";
    public const string SourceFactsVersion = "music-usage-source-facts-v1";

    public static bool Supports(MusicMaterializationPolicyArtifact artifact) =>
        artifact.SourceFactsVersion == SourceFactsVersion
        && artifact.EvaluatorVersion == Version
        && !string.IsNullOrWhiteSpace(artifact.SetlistProcessorVersion)
        && !string.IsNullOrWhiteSpace(artifact.RecordingProcessorVersion)
        && artifact.EnabledRules.Count > 0
        && artifact.EnabledRules.Distinct().Count() == artifact.EnabledRules.Count
        && artifact.EnabledRules.All(rule => Enum.IsDefined(rule));

    public IReadOnlyList<MusicUsageCorrespondenceDecision> Evaluate(
        MusicUsageCorrespondenceContext context,
        ActiveMusicMaterializationPolicy? policy
    )
    {
        var contradictions = ContextContradictions(context);
        var decisions = new List<MusicUsageCorrespondenceDecision>();
        foreach (
            var current in context.After.Claims.OrderBy(
                value => ClaimKey(value.Claim),
                StringComparer.Ordinal
            )
        )
        {
            decisions.Add(CompareClaim(context, current, policy, contradictions));
        }
        foreach (
            var previous in context.Before.Claims.OrderBy(
                value => ClaimKey(value.Claim),
                StringComparer.Ordinal
            )
        )
        {
            var targets = Targets(context, previous);
            if (
                targets.Length > 0
                && !StillRepresented(context, previous)
                && !decisions.Any(value =>
                    value.Disposition == MusicUsageDecisionDisposition.Authorized
                    && value.PreviousClaim == previous.Claim
                )
            )
            {
                decisions.Add(
                    CompareWithdrawal(context, previous, targets, policy, contradictions)
                );
            }
        }
        return decisions;
    }

    private static MusicUsageCorrespondenceDecision CompareClaim(
        MusicUsageCorrespondenceContext context,
        MusicUsageClaimFact current,
        ActiveMusicMaterializationPolicy? policy,
        IReadOnlyList<MusicUsageContradiction> contextContradictions
    )
    {
        if (current.UsageIdentity != null)
        {
            return Decision(
                MusicMaterializationOperation.Correspondence,
                null,
                current,
                [],
                null,
                "qualified_usage_correspondence_uses_existing_writer",
                [],
                contextContradictions,
                policy
            );
        }
        var previous = context
            .Before.Claims.Where(value =>
                value.UsageIdentity == null && SameContent(value, current)
            )
            .ToArray();
        var features = new List<MusicUsageFeature>();
        var contradictions = contextContradictions.ToList();
        if (previous.Length != 1)
        {
            if (previous.Length > 1)
            {
                contradictions.Add(MusicUsageContradiction.AmbiguousSourceClaims);
            }
            return Decision(
                MusicMaterializationOperation.Correspondence,
                previous.Length == 1 ? previous[0] : null,
                current,
                [],
                null,
                previous.Length == 0
                    ? "no_prior_usage_correspondence"
                    : "ambiguous_prior_usage_claims",
                features,
                contradictions,
                policy
            );
        }
        var prior = previous[0];
        var targets = Targets(context, prior);
        CheckTargets(
            context,
            prior,
            current,
            targets,
            MusicMaterializationOperation.Correspondence,
            contradictions
        );
        if (
            context.After.Claims.Count(value =>
                value.UsageIdentity == null && SameContent(value, current)
            ) != 1
        )
        {
            contradictions.Add(MusicUsageContradiction.AmbiguousSourceClaims);
        }
        features.Add(MusicUsageFeature.SameContentKind);
        if (current.ContentKind == "song")
        {
            features.Add(MusicUsageFeature.SameAcceptedSongAndKind);
        }
        MusicMaterializationRule? rule = null;
        if (SingleFileContinuity(context))
        {
            rule = MusicMaterializationRule.CheckedSingleFileClaimContinuity;
            features.AddRange([
                MusicUsageFeature.SameCheckedFile,
                MusicUsageFeature.CompleteSingleClaimImages,
            ]);
        }
        else if (UniqueSongWithinQualifiedSet(context, prior, current))
        {
            rule = MusicMaterializationRule.UniqueBoundSongWithinQualifiedSet;
            features.Add(MusicUsageFeature.SameQualifiedSet);
        }
        else if (
            context.Before.Kind is MusicUsageKind.Set or MusicUsageKind.Occurrence
            && NativeAnchorPair(context, prior, current, withdrawal: false)
        )
        {
            rule = MusicMaterializationRule.QualifiedUsageAnchors;
            features.Add(MusicUsageFeature.SameQualifiedUsageAnchors);
        }
        else
        {
            contradictions.Add(MusicUsageContradiction.MissingUsageAnchors);
        }
        if (contradictions.Count == 0)
        {
            features.AddRange([
                MusicUsageFeature.SameQualifiedSourceContainer,
                MusicUsageFeature.UniqueWholePoolCorrespondence,
            ]);
        }
        return Decision(
            MusicMaterializationOperation.Correspondence,
            prior,
            current,
            targets,
            rule,
            rule == null
                ? "unsupported_revised_usage_correspondence"
                : "evaluated_revised_usage_correspondence",
            features,
            contradictions,
            policy,
            context
        );
    }

    private static MusicUsageCorrespondenceDecision CompareWithdrawal(
        MusicUsageCorrespondenceContext context,
        MusicUsageClaimFact previous,
        MusicUsageAcceptedTarget[] targets,
        ActiveMusicMaterializationPolicy? policy,
        IReadOnlyList<MusicUsageContradiction> contextContradictions
    )
    {
        var contradictions = contextContradictions.ToList();
        var features = new List<MusicUsageFeature>();
        CheckTargets(
            context,
            previous,
            null,
            targets,
            MusicMaterializationOperation.Withdrawal,
            contradictions
        );
        if (
            context.After.Claims.Any(value =>
                SameContent(value, previous)
                && (context.Before.Kind != MusicUsageKind.Set || value.UsageIdentity == null)
            )
        )
        {
            contradictions.Add(MusicUsageContradiction.AmbiguousSourceClaims);
        }
        var anchored = context.Before.Kind is MusicUsageKind.Set or MusicUsageKind.Occurrence
            ? NativeAnchorPair(context, previous, null, withdrawal: true)
            : KnownContentWithdrawalAnchors(context, previous);
        if (!anchored)
        {
            contradictions.Add(MusicUsageContradiction.MissingUsageAnchors);
        }
        else
        {
            features.AddRange([
                MusicUsageFeature.SameQualifiedSourceContainer,
                context.Before.Kind == MusicUsageKind.TrackContent
                    ? MusicUsageFeature.SameAcceptedContentAnchors
                    : MusicUsageFeature.SameQualifiedUsageAnchors,
                MusicUsageFeature.AnchorsNowAdjacent,
            ]);
        }
        return Decision(
            MusicMaterializationOperation.Withdrawal,
            previous,
            null,
            targets,
            anchored ? MusicMaterializationRule.BracketedWithdrawal : null,
            anchored ? "evaluated_bracketed_usage_withdrawal" : "unsupported_usage_withdrawal",
            features,
            contradictions,
            policy,
            context
        );
    }

    private static List<MusicUsageContradiction> ContextContradictions(
        MusicUsageCorrespondenceContext context
    )
    {
        var before = context.Before;
        var after = context.After;
        var contradictions = new List<MusicUsageContradiction>();
        if (
            before.Kind != after.Kind
            || before.SourceFactsVersion != SourceFactsVersion
            || after.SourceFactsVersion != SourceFactsVersion
            || AllClaims(before)
                .Any(value => !NativeUsageIdentity(before.Kind, value.UsageIdentity))
            || AllClaims(after).Any(value => !NativeUsageIdentity(after.Kind, value.UsageIdentity))
        )
        {
            contradictions.Add(MusicUsageContradiction.UnsupportedSourceContext);
        }
        if (
            before.Issues.Concat(after.Issues).Any(value => value.PreventsAutomaticAcceptance)
            || AllClaims(before).Any(value => !ClearClaim(before.Kind, value))
            || AllClaims(after).Any(value => !ClearClaim(after.Kind, value))
        )
        {
            contradictions.Add(MusicUsageContradiction.IncompleteSourceContext);
        }
        if (
            before.Container.SourceLineage != after.Container.SourceLineage
            || before.Container.AccountIdentity != after.Container.AccountIdentity
            || before.Container.TrackUuid != after.Container.TrackUuid
            || !SameIdentity(before.Container.FileIdentity, after.Container.FileIdentity)
            || !SameIdentity(before.Container.SetIdentity, after.Container.SetIdentity)
        )
        {
            contradictions.Add(MusicUsageContradiction.SourceScopeChanged);
        }
        if (
            AllClaims(before).Select(value => value.Claim).Distinct().Count()
                != AllClaims(before).Count()
            || AllClaims(after).Select(value => value.Claim).Distinct().Count()
                != AllClaims(after).Count()
            || context.AcceptedTargets.Select(value => value.Uuid).Distinct().Count()
                != context.AcceptedTargets.Count
        )
        {
            contradictions.Add(MusicUsageContradiction.AmbiguousSourceClaims);
        }
        if (
            before.Kind == MusicUsageKind.TrackContent
            && (
                !CheckedFile(context)
                || before
                    .Claims.Concat(after.Claims)
                    .Any(value => value.Claim.TrackUuid != before.Container.TrackUuid)
            )
        )
        {
            contradictions.Add(MusicUsageContradiction.UnsupportedSourceContext);
        }
        return contradictions;
    }

    private static bool ClearClaim(MusicUsageKind kind, MusicUsageClaimFact fact) =>
        fact.Claim.ProcessingAttemptUuid != Guid.Empty
        && fact.Claim.InputRevisionUuid != Guid.Empty
        && fact.Evidence.Count > 0
        && fact.Evidence.All(value => value.InputRevisionUuid == fact.Claim.InputRevisionUuid)
        && (
            kind == MusicUsageKind.Set
                ? fact.ContentKind == "set"
                : fact.ContentKind is "song" or "jam" or "speech" or "nonmusic"
        )
        && (
            fact.ContentKind != "song" || fact.RepertoireSongUuid is { } uuid && uuid != Guid.Empty
        );

    private static bool SameContent(MusicUsageClaimFact left, MusicUsageClaimFact right) =>
        left.ContentKind == right.ContentKind
        && (left.ContentKind != "song" || left.RepertoireSongUuid == right.RepertoireSongUuid);

    // Facts v1 accepts only native usage endpoints. A provider song ID is never an anchor.
    private static bool NativeUsageIdentity(
        MusicUsageKind kind,
        ObservedProviderIdentity? identity
    ) =>
        identity == null
        || (
            kind switch
            {
                MusicUsageKind.Set => identity.Identity.ResourceType is "set" or "show_set",
                MusicUsageKind.Occurrence => identity.Identity.ResourceType == "occurrence",
                _ => false,
            }
        );

    private static bool StillRepresented(
        MusicUsageCorrespondenceContext context,
        MusicUsageClaimFact previous
    ) =>
        previous.UsageIdentity is { } identity
            ? context.After.Claims.Any(value => SameIdentity(value.UsageIdentity, identity))
            : context.After.Claims.Any(value =>
                SameContent(value, previous)
                && (context.Before.Kind != MusicUsageKind.Set || value.UsageIdentity == null)
            );

    private static MusicUsageAcceptedTarget[] Targets(
        MusicUsageCorrespondenceContext context,
        MusicUsageClaimFact previous
    ) =>
        context
            .AcceptedTargets.Where(value => value.Claim == previous.Claim && !value.Retracted)
            .OrderBy(value => value.Uuid)
            .ToArray();

    private static void CheckTargets(
        MusicUsageCorrespondenceContext context,
        MusicUsageClaimFact previous,
        MusicUsageClaimFact? current,
        MusicUsageAcceptedTarget[] targets,
        MusicMaterializationOperation operation,
        List<MusicUsageContradiction> contradictions
    )
    {
        if (
            targets.Length != 1
            || targets.Any(value =>
                value.Uuid == Guid.Empty
                || value.Revision <= 0
                || value.ContentKind != previous.ContentKind
                || value.RepertoireSongUuid != previous.RepertoireSongUuid
            )
        )
        {
            contradictions.Add(MusicUsageContradiction.CompetingAcceptedTargets);
        }
        if (targets.Any(value => value.Basis == "reviewed"))
        {
            contradictions.Add(MusicUsageContradiction.ReviewedTarget);
        }
        if (
            context.AcceptedTargets.Any(value =>
                !value.Retracted
                && !targets.Any(target => target.Uuid == value.Uuid)
                && (
                    current?.Position is { } position && value.Position == position
                    || context.Before.Kind != MusicUsageKind.Set
                        && value.ContentKind == previous.ContentKind
                        && value.RepertoireSongUuid == previous.RepertoireSongUuid
                )
            )
        )
        {
            contradictions.Add(MusicUsageContradiction.CompetingAcceptedTargets);
        }
        if (
            context.AuthorizationHeads.Any(value =>
                value.Operation == operation
                && (
                    value.TargetUuid is { } uuid && targets.Any(target => target.Uuid == uuid)
                    || value.Claim != null
                        && (value.Claim == previous.Claim || value.Claim == current?.Claim)
                )
                && value.Action
                    is SetlistAuthorizationAction.Block
                        or SetlistAuthorizationAction.Revoke
            )
        )
        {
            contradictions.Add(MusicUsageContradiction.BlockedOrRevokedAuthority);
        }
    }

    private static bool SingleFileContinuity(MusicUsageCorrespondenceContext context) =>
        CheckedFile(context) && context.Before.Claims.Count == 1 && context.After.Claims.Count == 1;

    private static bool UniqueSongWithinQualifiedSet(
        MusicUsageCorrespondenceContext context,
        MusicUsageClaimFact previous,
        MusicUsageClaimFact current
    ) =>
        context.Before.Kind == MusicUsageKind.Occurrence
        && context.Before.Container.SetIdentity is { } set
        && set.Identity.ResourceType is "set" or "show_set"
        && set.Identity.Provider == context.Before.Container.AccountIdentity.Provider
        && context.Before.CompetingClaims != null
        && context.After.CompetingClaims != null
        && previous.ContentKind == "song"
        && current.ContentKind == "song"
        && AllClaims(context.Before).Count(value => SameContent(value, current)) == 1
        && AllClaims(context.After).Count(value => SameContent(value, current)) == 1;

    private static IEnumerable<MusicUsageClaimFact> AllClaims(MusicUsageSourceImage image) =>
        image.Claims.Concat(image.CompetingClaims ?? []);

    private static bool CheckedFile(MusicUsageCorrespondenceContext context) =>
        context.Before.Kind == MusicUsageKind.TrackContent
        && context.Before.Container.TrackUuid is { } track
        && track != Guid.Empty
        && context.Before.Container.FileIdentity != null;

    private static bool KnownContentWithdrawalAnchors(
        MusicUsageCorrespondenceContext context,
        MusicUsageClaimFact previous
    )
    {
        if (!CheckedFile(context))
        {
            return false;
        }
        var before = Ordered(context.Before.Claims);
        var after = Ordered(context.After.Claims);
        var index = Array.FindIndex(before, value => value.Claim == previous.Claim);
        if (index <= 0 || index >= before.Length - 1)
        {
            return false;
        }
        var left = KnownTarget(context, before[index - 1].Claim);
        var right = KnownTarget(context, before[index + 1].Claim);
        if (left == null || right == null || left.Uuid == right.Uuid)
        {
            return false;
        }
        var leftIndices = Enumerable
            .Range(0, after.Length)
            .Where(value => KnownTarget(context, after[value].Claim) == left)
            .ToArray();
        var rightIndices = Enumerable
            .Range(0, after.Length)
            .Where(value => KnownTarget(context, after[value].Claim) == right)
            .ToArray();
        return leftIndices.Length == 1
            && rightIndices.Length == 1
            && rightIndices[0] == leftIndices[0] + 1
            && SameContent(before[index - 1], after[leftIndices[0]])
            && SameContent(before[index + 1], after[rightIndices[0]]);
    }

    private static MusicUsageTargetRevision? KnownTarget(
        MusicUsageCorrespondenceContext context,
        MusicUsageClaimReference claim
    )
    {
        var known = (context.KnownCorrespondences ?? [])
            .Where(value => value.Claim == claim)
            .ToArray();
        if (
            known.Length != 1
            || known[0].AcceptedDecision.ConsumerAttemptUuid == Guid.Empty
            || known[0].AcceptedDecision.ConsumerDecisionUuid == Guid.Empty
        )
        {
            return null;
        }
        if (CorrespondenceBlocked(context, [claim], [known[0].TargetUuid]))
        {
            return null;
        }
        var targets = context
            .AcceptedTargets.Where(value =>
                value.Uuid == known[0].TargetUuid
                && value.Revision == known[0].TargetRevision
                && !value.Retracted
            )
            .ToArray();
        var facts = context
            .Before.Claims.Concat(context.After.Claims)
            .Where(value => value.Claim == claim)
            .ToArray();
        return
            targets.Length != 1
            || facts.Length != 1
            || targets[0].ContentKind != facts[0].ContentKind
            || targets[0].RepertoireSongUuid != facts[0].RepertoireSongUuid
            ? null
            : new(targets[0].Uuid, targets[0].Revision);
    }

    private static bool NativeAnchorPair(
        MusicUsageCorrespondenceContext context,
        MusicUsageClaimFact previous,
        MusicUsageClaimFact? current,
        bool withdrawal
    )
    {
        if (previous.UsageIdentity != null || current?.UsageIdentity != null)
        {
            return false;
        }
        var before = Ordered(context.Before.Claims);
        var after = Ordered(context.After.Claims);
        var index = Array.FindIndex(before, value => value.Claim == previous.Claim);
        if (index <= 0 || index >= before.Length - 1 || after.Length == 0)
        {
            return false;
        }
        var left = before[index - 1];
        var right = before[index + 1];
        if (
            left.UsageIdentity == null
            || right.UsageIdentity == null
            || SameIdentity(left.UsageIdentity, right.UsageIdentity)
        )
        {
            return false;
        }
        var leftAfter = Array.FindIndex(
            after,
            value => SameIdentity(value.UsageIdentity, left.UsageIdentity)
        );
        var rightAfter = Array.FindIndex(
            after,
            value => SameIdentity(value.UsageIdentity, right.UsageIdentity)
        );
        if (
            leftAfter < 0
            || rightAfter < 0
            || !UniqueIdentity(before, left.UsageIdentity)
            || !UniqueIdentity(before, right.UsageIdentity)
            || !UniqueIdentity(after, left.UsageIdentity)
            || !UniqueIdentity(after, right.UsageIdentity)
            || !SameContent(left, after[leftAfter])
            || !SameContent(right, after[rightAfter])
        )
        {
            return false;
        }
        var anchorClaims = new[]
        {
            left.Claim,
            right.Claim,
            after[leftAfter].Claim,
            after[rightAfter].Claim,
        };
        var anchorTargets = context
            .AcceptedTargets.Where(value =>
                value.Claim != null && anchorClaims.Contains(value.Claim)
            )
            .Select(value => value.Uuid)
            .ToArray();
        if (CorrespondenceBlocked(context, anchorClaims, anchorTargets))
        {
            return false;
        }
        if (withdrawal)
        {
            return rightAfter == leftAfter + 1;
        }
        var currentIndex = Array.FindIndex(after, value => value.Claim == current!.Claim);
        return currentIndex == leftAfter + 1 && rightAfter == currentIndex + 1;
    }

    private static MusicUsageClaimFact[] Ordered(IReadOnlyList<MusicUsageClaimFact> claims) =>
        claims.Any(value => value.Position == null)
        || claims.Select(value => value.Position).Distinct().Count() != claims.Count
            ? []
            : claims.OrderBy(value => value.Position).ToArray();

    private static bool UniqueIdentity(
        IEnumerable<MusicUsageClaimFact> claims,
        ObservedProviderIdentity identity
    ) => claims.Count(value => SameIdentity(value.UsageIdentity, identity)) == 1;

    private static bool CorrespondenceBlocked(
        MusicUsageCorrespondenceContext context,
        IReadOnlyList<MusicUsageClaimReference> claims,
        IReadOnlyList<Guid> targets
    ) =>
        context.AuthorizationHeads.Any(value =>
            value.Operation == MusicMaterializationOperation.Correspondence
            && value.Action is SetlistAuthorizationAction.Block or SetlistAuthorizationAction.Revoke
            && (
                value.Claim != null && claims.Contains(value.Claim)
                || value.TargetUuid is { } target && targets.Contains(target)
            )
        );

    private static bool SameIdentity(
        ObservedProviderIdentity? left,
        ObservedProviderIdentity? right
    ) => left?.Identity == right?.Identity;

    private static MusicUsageCorrespondenceDecision Decision(
        MusicMaterializationOperation operation,
        MusicUsageClaimFact? previous,
        MusicUsageClaimFact? current,
        IReadOnlyList<MusicUsageAcceptedTarget> targets,
        MusicMaterializationRule? rule,
        string reason,
        IEnumerable<MusicUsageFeature> features,
        IEnumerable<MusicUsageContradiction> contradictions,
        ActiveMusicMaterializationPolicy? policy,
        MusicUsageCorrespondenceContext? context = null
    )
    {
        var conflicts = contradictions.Distinct().Order().ToArray();
        var applicable =
            context != null
            && policy != null
            && Supports(policy.Policy.Artifact)
            && policy.Policy.Artifact.EnabledRules.Contains(rule ?? (MusicMaterializationRule)(-1))
            && context.Before.ProcessorVersion
                == ProcessorVersion(policy.Policy.Artifact, context.Before.Kind)
            && context.After.ProcessorVersion
                == ProcessorVersion(policy.Policy.Artifact, context.After.Kind);
        var authorized = applicable && rule != null && conflicts.Length == 0;
        var reference =
            policy == null
                ? null
                : new MusicMaterializationPolicyReference(
                    policy.Policy.Artifact.Uuid,
                    policy.Policy.Artifact.ContentHash,
                    policy.Policy.Evaluation.Uuid,
                    policy.Policy.Evaluation.ReceiptHash
                );
        return new(
            operation,
            authorized ? MusicUsageDecisionDisposition.Authorized
                : conflicts.Length > 0 ? MusicUsageDecisionDisposition.Review
                : MusicUsageDecisionDisposition.Unresolved,
            previous?.Claim,
            current?.Claim,
            targets
                .Select(value => new MusicUsageTargetRevision(value.Uuid, value.Revision))
                .ToArray(),
            rule,
            !applicable && conflicts.Length == 0 && rule != null
                ? "inactive_or_inapplicable_music_usage_policy"
                : reason,
            features.Distinct().Order().ToArray(),
            conflicts,
            reference
        );
    }

    private static string ProcessorVersion(
        MusicMaterializationPolicyArtifact artifact,
        MusicUsageKind kind
    ) =>
        kind == MusicUsageKind.TrackContent
            ? artifact.RecordingProcessorVersion
            : artifact.SetlistProcessorVersion;

    private static string ClaimKey(MusicUsageClaimReference claim) =>
        JsonSerializer.Serialize(claim);
}
