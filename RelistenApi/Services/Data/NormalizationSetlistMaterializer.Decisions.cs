using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationSetlistMaterializer
{
    /// <summary>
    /// Allocation belongs to the saved decision nonce. Re-evaluation may reuse those nonces,
    /// but it derives every disposition, target selection and authority again from the facts.
    /// </summary>
    internal static SetlistMaterializationPreparation Decide(
        SetlistMaterializationContext context,
        IReadOnlyList<SetlistMaterializationDecision>? allocations = null
    )
    {
        var decisions = new List<SetlistMaterializationDecision>();
        Guid Allocate(string key) =>
            allocations?.SingleOrDefault(value => value.Key == key)?.DecisionUuid ?? Guid.NewGuid();
        var holdReason = AccountHoldReason(context);
        if (holdReason != null)
        {
            var reason = holdReason;
            foreach (var source in context.Sources)
            {
                var key = AccountKey(source);
                decisions.Add(
                    new SetlistAccountMaterializationDecision(
                        Allocate(key),
                        key,
                        reason == "source_artist_unbound"
                            ? MusicMaterializationDecisionDisposition.Unresolved
                            : MusicMaterializationDecisionDisposition.Review,
                        reason,
                        AccountEvidence(source),
                        source
                    )
                );
            }
            return Preparation(context, null, decisions, []);
        }

        var sourceAccount = context.Sources.Single();
        var comparisons = CorrespondenceContexts(context);
        var evaluations = comparisons
            .SelectMany(comparison =>
                new RepertoireConsumerCorrespondenceEvaluator().Evaluate(
                    comparison,
                    context.Policy.Active
                )
            )
            .ToArray();
        for (var index = 0; index < sourceAccount.Account.Account.Sets.Count; index++)
        {
            var claim = new SetlistClaimReference(SetIndex: index);
            var original = sourceAccount.Account.Account.Sets[index];
            decisions.Add(
                DecideClaim(
                    context,
                    sourceAccount,
                    claim,
                    original.Identity?.Identity,
                    [original.Evidence],
                    evaluations,
                    Allocate(ClaimKey(sourceAccount, claim))
                )
            );
        }
        foreach (var passage in sourceAccount.Account.Passages)
        {
            var claim = new SetlistClaimReference(ProposalUuid: passage.ProposalUuid);
            decisions.Add(
                DecideClaim(
                    context,
                    sourceAccount,
                    claim,
                    passage.Passage.OccurrenceIdentity?.Identity,
                    [passage.Passage.Title.Evidence],
                    evaluations,
                    Allocate(ClaimKey(sourceAccount, claim))
                )
            );
        }
        AddRemovals(context, decisions, evaluations, Allocate);
        if (
            decisions.Any(decision =>
                decision.Disposition == MusicMaterializationDecisionDisposition.Review
            )
        )
        {
            return Preparation(context, null, WithheldDecisions(decisions), comparisons);
        }

        var image = BuildImage(context, decisions);
        if (context.ReviewedClaimExclusions.Count > 0)
        {
            decisions = decisions
                .Select(decision =>
                    decision is SetlistClaimMaterializationDecision claim
                        ? claim with
                        {
                            Disposition = MusicMaterializationDecisionDisposition.Review,
                            Reason = "reviewed_music_exclusion",
                        }
                        : decision
                )
                .ToList();
            decisions = WithheldDecisions(decisions);
            if (decisions.Count == 0)
            {
                var key = AccountKey(sourceAccount);
                decisions.Add(
                    new SetlistAccountMaterializationDecision(
                        Allocate(key),
                        key,
                        MusicMaterializationDecisionDisposition.Review,
                        "reviewed_music_exclusion",
                        AccountEvidence(sourceAccount),
                        sourceAccount
                    )
                );
            }
            image = null;
        }
        else if (
            context.Structure is { } previous
            && SameImageFacts(
                previous.Value,
                image,
                context.SourceVersions.All(value =>
                    value.ProcessingVersion == context.Consumption.Setlist!.ProcessingVersion
                )
            )
        )
        {
            decisions = decisions
                .Select(decision =>
                    decision is SetlistClaimMaterializationDecision claim
                        ? claim with
                        {
                            Disposition = MusicMaterializationDecisionDisposition.Retained,
                            Reason = "unchanged_accepted_image",
                        }
                        : decision
                )
                .ToList();
            image = null;
        }
        else if (
            context.Structure?.Value.Basis == "reviewed"
            || context.Edition?.Value.Basis == "reviewed"
            || context.Occurrences.Any(record =>
                record.Value.Basis == "reviewed" && !record.Value.Retracted
            )
        )
        {
            // Full image publication must not turn correspondence authority into permission
            // to replace metadata whose meaning was separately reviewed.
            decisions = decisions
                .Select(decision =>
                    decision is SetlistClaimMaterializationDecision claim
                        ? claim with
                        {
                            Disposition = MusicMaterializationDecisionDisposition.Review,
                            Reason = "reviewed_metadata_requires_review",
                        }
                        : decision
                )
                .ToList();
            decisions = WithheldDecisions(decisions);
            image = null;
        }
        return Preparation(context, image, decisions, comparisons);
    }

    private static string? AccountHoldReason(SetlistMaterializationContext context)
    {
        if (context.Consumption.Source.ArtistUuid == null)
        {
            return "source_artist_unbound";
        }
        if (context.Sources.Count != 1)
        {
            return "multiple_account_observations";
        }
        if (context.Edition?.Value.Retracted == true)
        {
            return "canonical_edition_retracted";
        }
        if (context.Edition != null && context.Structure == null)
        {
            return "unmanaged_accepted_edition";
        }
        var source = context.Sources.Single();
        var account = source.Account.Account;
        if (
            account.Ordered
            && (
                account.Passages.Any(value => value.Ordinal is not > 0)
                || account.Passages.Select(value => value.Ordinal).Distinct().Count()
                    != account.Passages.Count
                || account.Sets.Any(value => value.Ordinal <= 0)
                || account.Sets.Select(value => value.Ordinal).Distinct().Count()
                    != account.Sets.Count
            )
        )
        {
            // The accepted writer has one edition-order contract. Keep a partially ordered
            // source intact rather than inventing positions or discarding its known order.
            return "source_order_not_representable";
        }
        var binding = context
            .Consumption.Setlist!.Preparation.Inputs.Single(value =>
                value.Input.InputRevisionUuid == source.InputRevisionUuid
            )
            .Accounts[source.AccountIndex]
            .Binding;
        var show =
            binding.Performance.Status == NormalizationCatalogBindingStatus.Bound
                ? binding.Performance.Value?.ShowUuid
                : null;
        if (
            context.Edition?.Value.ShowUuid is { } previous
            && previous != show
            && (context.Edition.Value.Selected || show == null)
        )
        {
            return "performance_binding_requires_review";
        }
        return null;
    }

    private static List<SetlistMaterializationDecision> WithheldDecisions(
        IReadOnlyList<SetlistMaterializationDecision> decisions
    ) =>
        decisions
            .Select(value =>
                value switch
                {
                    SetlistClaimMaterializationDecision claim
                        when claim.Disposition
                            == MusicMaterializationDecisionDisposition.Accepted => claim with
                    {
                        Disposition = claim.Targets.All(target => target.ExpectedRevision > 0)
                            ? MusicMaterializationDecisionDisposition.Retained
                            : MusicMaterializationDecisionDisposition.Review,
                        Reason = "complete_image_withheld",
                    },
                    SetlistRemovalMaterializationDecision removal
                        when removal.Disposition
                            == MusicMaterializationDecisionDisposition.Withdrawn => removal with
                    {
                        Disposition = MusicMaterializationDecisionDisposition.Review,
                        Reason = "complete_image_withheld",
                    },
                    _ => value,
                }
            )
            .ToList();

    private static SetlistMaterializationPreparation Preparation(
        SetlistMaterializationContext context,
        SetlistStructureChange? image,
        IReadOnlyList<SetlistMaterializationDecision> decisions,
        IReadOnlyList<MusicUsageCorrespondenceContext> comparisons
    ) =>
        new(
            context,
            image,
            decisions,
            RequiresObservationRetirement: context.ContributionsToRetire.Count > 0
        )
        {
            CorrespondenceContexts = comparisons,
            ContributionsToRetire = context.ContributionsToRetire,
        };

    private static SetlistClaimMaterializationDecision DecideClaim(
        SetlistMaterializationContext context,
        SetlistStructureSource source,
        SetlistClaimReference claim,
        NormalizationResourceKey? identity,
        IReadOnlyList<NormalizedEvidenceReference> evidence,
        MusicUsageCorrespondenceDecision[] evaluations,
        Guid nonce
    )
    {
        var key = ClaimKey(source, claim);
        var head = context.Authorizations.SingleOrDefault(item =>
            item.Active
            && item.Correspondence is { } approval
            && SameClaim(approval.Source, approval.Claim, source, claim)
        );
        if (head != null)
        {
            if (
                head.Action != SetlistAuthorizationAction.Authorize
                || head.Correspondence!.Targets.Count != 1
                || !CurrentTargets(
                    context,
                    claim,
                    head.Correspondence.Targets.Select(value => value.Uuid).ToArray(),
                    out var authorized,
                    allowRetracted: head.OperationUuid.HasValue
                )
                || !ApprovedTargetRevisions(context, claim, head, authorized)
            )
            {
                return new(
                    nonce,
                    key,
                    MusicMaterializationDecisionDisposition.Review,
                    "blocked_or_invalid_correspondence_authority",
                    evidence,
                    source,
                    claim,
                    []
                );
            }
            return new(
                nonce,
                key,
                MusicMaterializationDecisionDisposition.Accepted,
                "reviewed_correspondence",
                evidence,
                source,
                claim,
                authorized,
                head.AuthorizationUuid
            );
        }
        var matches = context
            .Correspondences.Where(link =>
                link.Claim.SetIndex.HasValue == claim.SetIndex.HasValue
                && (
                    link.ProcessingVersion == context.Consumption.Setlist!.ProcessingVersion
                        && link.InputRevisionUuid == source.InputRevisionUuid
                        && link.AccountIndex == source.AccountIndex
                        && link.Claim == claim
                    || identity != null && link.ProviderIdentity == identity
                )
            )
            .ToArray();
        if (matches.Length > 0)
        {
            var latest = matches
                .Where(link =>
                    link.StructureRevision == matches.Max(value => value.StructureRevision)
                )
                .ToArray();
            var targets = latest.Select(link => link.TargetUuid).Distinct().ToArray();
            var authorities = latest.Select(link => link.AuthorizationUuid).Distinct().ToArray();
            var blocked = latest.Any(link =>
                context.Authorizations.Any(item =>
                    item.Correspondence is { } approval
                    && SameClaim(
                        approval.Source,
                        approval.Claim,
                        new(
                            link.ProcessingAttemptUuid,
                            link.InputRevisionUuid,
                            link.AccountIndex,
                            source.Account
                        ),
                        link.Claim
                    )
                    && (
                        item.Action != SetlistAuthorizationAction.Authorize
                        || item.AuthorizationUuid != link.AuthorizationUuid
                    )
                )
            );
            if (
                blocked
                || authorities.Length != 1
                || targets.Length != 1
                || !CurrentTargets(context, claim, targets, out var retained)
            )
            {
                return new(
                    nonce,
                    key,
                    MusicMaterializationDecisionDisposition.Review,
                    "historical_correspondence_conflict",
                    evidence,
                    source,
                    claim,
                    []
                );
            }
            return new(
                nonce,
                key,
                MusicMaterializationDecisionDisposition.Accepted,
                identity == null ? "retained_exact_claim" : "qualified_usage_identity",
                evidence,
                source,
                claim,
                retained,
                authorities[0]
            );
        }
        var evaluation = evaluations.SingleOrDefault(value =>
            value.CurrentClaim == UsageClaim(source, claim)
            && value.Disposition == MusicUsageDecisionDisposition.Authorized
        );
        if (evaluation != null)
        {
            return new(
                nonce,
                key,
                MusicMaterializationDecisionDisposition.Accepted,
                evaluation.Reason,
                evidence,
                source,
                claim,
                evaluation
                    .Targets.Select(value => new SetlistTargetRevision(
                        value.Uuid,
                        value.ExpectedRevision
                    ))
                    .ToArray(),
                Evaluation: evaluation
            );
        }
        if (context.Structure == null || identity != null)
        {
            return new(
                nonce,
                key,
                MusicMaterializationDecisionDisposition.Accepted,
                context.Structure == null
                    ? "initial_source_assertion"
                    : "new_qualified_usage_identity",
                evidence,
                source,
                claim,
                [new(PurposeUuid(nonce, "target"), 0)]
            );
        }
        return new(
            nonce,
            key,
            MusicMaterializationDecisionDisposition.Review,
            "unkeyed_revised_claim_requires_authority",
            evidence,
            source,
            claim,
            []
        );
    }

    private static bool CurrentTargets(
        SetlistMaterializationContext context,
        SetlistClaimReference claim,
        Guid[] ids,
        out SetlistTargetRevision[] targets,
        bool allowRetracted = false
    )
    {
        targets = claim.SetIndex.HasValue
            ? context
                .Sets.Where(item =>
                    ids.Contains(item.Value.Uuid) && (allowRetracted || !item.Value.Retracted)
                )
                .Select(item => new SetlistTargetRevision(item.Value.Uuid, item.Revision))
                .ToArray()
            : context
                .Occurrences.Where(item =>
                    ids.Contains(item.Value.Uuid) && (allowRetracted || !item.Value.Retracted)
                )
                .Select(item => new SetlistTargetRevision(item.Value.Uuid, item.Revision))
                .ToArray();
        return targets.Length == ids.Length;
    }

    private static bool ApprovedTargetRevisions(
        SetlistMaterializationContext context,
        SetlistClaimReference claim,
        SetlistAuthorizationRecord authorization,
        IReadOnlyList<SetlistTargetRevision> current
    )
    {
        return authorization.Correspondence!.Targets.All(approved =>
            current.Any(target => target == approved)
            || current.Any(target => target.Uuid == approved.Uuid)
                && (
                    claim.SetIndex.HasValue
                        ? context.Sets.Any(value =>
                            value.Value.Uuid == approved.Uuid && !value.Value.Retracted
                        )
                        : context.Occurrences.Any(value =>
                            value.Value.Uuid == approved.Uuid && !value.Value.Retracted
                        )
                )
                && context.Correspondences.Any(value =>
                    value.TargetUuid == approved.Uuid
                    && value.AuthorizationUuid == authorization.AuthorizationUuid
                )
        );
    }

    private static void AddRemovals(
        SetlistMaterializationContext context,
        List<SetlistMaterializationDecision> decisions,
        MusicUsageCorrespondenceDecision[] evaluations,
        Func<string, Guid> allocate
    )
    {
        var used = decisions
            .OfType<SetlistClaimMaterializationDecision>()
            .SelectMany(value => value.Targets)
            .Select(value => value.Uuid)
            .ToHashSet();
        var remaining = context
            .Sets.Where(value => !value.Value.Retracted && !used.Contains(value.Value.Uuid))
            .Select(value =>
                (
                    Kind: SetlistRemovalKind.Set,
                    Target: new SetlistTargetRevision(value.Value.Uuid, value.Revision),
                    Evidence: value.Value.Evidence
                )
            )
            .Concat(
                context
                    .Occurrences.Where(value =>
                        !value.Value.Retracted && !used.Contains(value.Value.Uuid)
                    )
                    .Select(value =>
                        (
                            Kind: SetlistRemovalKind.Occurrence,
                            Target: new SetlistTargetRevision(value.Value.Uuid, value.Revision),
                            Evidence: value.Value.Evidence
                        )
                    )
            );
        foreach (var item in remaining)
        {
            var key = $"removal/{item.Kind}/{item.Target.Uuid:D}";
            var manual = context.Authorizations.SingleOrDefault(value =>
                value.Active
                && value.Removal is { } removal
                && removal.Kind == item.Kind
                && removal.Target == item.Target
            );
            var evaluation = evaluations.SingleOrDefault(value =>
                value.Operation == MusicMaterializationOperation.Withdrawal
                && value.Disposition == MusicUsageDecisionDisposition.Authorized
                && value.Targets.Any(target => target.Uuid == item.Target.Uuid)
            );
            var accepted =
                manual?.Action == SetlistAuthorizationAction.Authorize
                || manual == null && evaluation != null;
            decisions.Add(
                new SetlistRemovalMaterializationDecision(
                    allocate(key),
                    key,
                    accepted
                        ? MusicMaterializationDecisionDisposition.Withdrawn
                        : MusicMaterializationDecisionDisposition.Review,
                    accepted
                        ? manual != null
                            ? "reviewed_usage_withdrawal"
                            : evaluation!.Reason
                        : "usage_withdrawal_requires_authority",
                    item.Evidence,
                    item.Kind,
                    item.Target,
                    context.Sources,
                    manual?.AuthorizationUuid,
                    manual == null ? evaluation : null
                )
            );
        }
    }

    internal static SetlistStructureChange BuildImage(
        SetlistMaterializationContext context,
        IReadOnlyList<SetlistMaterializationDecision> decisions
    )
    {
        var source = context.Sources.Single();
        var account = source.Account.Account;
        var identity = account.Origin.Item!;
        var claims = decisions.OfType<SetlistClaimMaterializationDecision>().ToArray();
        var ordered =
            account.Ordered
            && account.Passages.All(value => value.Ordinal is > 0)
            && account.Passages.Select(value => value.Ordinal).Distinct().Count()
                == account.Passages.Count;
        var sets = new List<SetlistStructureSet>();
        var setLinks = new List<SetlistSetCorrespondence>();
        for (var index = 0; index < account.Sets.Count; index++)
        {
            var original = account.Sets[index];
            var decision = claims.Single(value => value.Claim.SetIndex == index);
            sets.Add(
                new()
                {
                    Uuid = decision.Targets.Single().Uuid,
                    Position = ordered ? original.Ordinal : null,
                    Label = original.Label?.Value,
                    EncoreNumber = original.EncoreNumber?.Value,
                    ProviderIdentity = original.Identity?.Identity,
                    Evidence = [original.Evidence],
                }
            );
            setLinks.Add(new(0, index, decision.Targets.Single().Uuid, Authorization(decision)));
        }
        var occurrences = new List<SongOccurrenceChange>();
        var passageLinks = new List<SetlistPassageCorrespondence>();
        foreach (var original in source.Account.Passages)
        {
            var decision = claims.Single(value =>
                value.Claim.ProposalUuid == original.ProposalUuid
            );
            var target = decision.Targets.Single();
            var passage = original.Passage;
            var sourceClaim = context.Consumption.Source.Claims.Single(value =>
                value.ProposalUuid == original.ProposalUuid
            );
            var resolution = context.Consumption.Decisions.Single(value =>
                value.ClaimUuid == sourceClaim.ClaimUuid
            );
            var song =
                resolution.Disposition
                    is RepertoireResolutionClaimDisposition.Bound
                        or RepertoireResolutionClaimDisposition.Provisional
                && resolution.RepertoireSongUuid is { } requested
                    ? context
                        .Consumption.Targets.Single(value => value.RequestedUuid == requested)
                        .CanonicalUuid
                    : (Guid?)null;
            var sourceSet = account
                .Sets.Select((set, index) => (set, index))
                .Where(value => value.set.Ordinal == passage.SetOrdinal)
                .ToArray();
            occurrences.Add(
                new()
                {
                    Uuid = target.Uuid,
                    ExpectedRevision = target.ExpectedRevision,
                    EditionUuid = context.EditionUuid,
                    Position = ordered ? passage.Ordinal : null,
                    SetLabel = passage.SetLabel?.Value,
                    SetUuid =
                        sourceSet.Length == 1
                            ? setLinks.Single(value => value.SetIndex == sourceSet[0].index).SetUuid
                            : null,
                    Kind = sourceClaim.Kind,
                    RepertoireSongUuid = song,
                    OriginalText = passage.Title.Value,
                    TransitionAfter = Transition(passage),
                    ProviderItemKey = passage.OccurrenceIdentity?.Identity.ProviderKey,
                    Evidence = [passage.Title.Evidence],
                }
            );
            passageLinks.Add(new(0, original.ProposalUuid, [target.Uuid], Authorization(decision)));
        }
        var removals = decisions
            .OfType<SetlistRemovalMaterializationDecision>()
            .Where(value => value.Disposition == MusicMaterializationDecisionDisposition.Withdrawn)
            .ToArray();
        sets.AddRange(
            context
                .Sets.Where(value => !sets.Any(set => set.Uuid == value.Value.Uuid))
                .Select(value => value.Value with { Retracted = true })
        );
        occurrences.AddRange(
            context
                .Occurrences.Where(value =>
                    !occurrences.Any(occurrence => occurrence.Uuid == value.Value.Uuid)
                )
                .Select(value =>
                    value.Value with
                    {
                        ExpectedRevision = value.Revision,
                        Retracted = true,
                    }
                )
        );
        var binding = context
            .Consumption.Setlist!.Preparation.Inputs.Single(value =>
                value.Input.InputRevisionUuid == source.InputRevisionUuid
            )
            .Accounts[source.AccountIndex]
            .Binding;
        var edition = new SetlistEditionChange
        {
            Uuid = context.EditionUuid,
            ExpectedRevision = context.Edition?.Revision ?? 0,
            ArtistUuid = context.Consumption.Source.ArtistUuid!.Value,
            ShowUuid =
                binding.Performance.Status == NormalizationCatalogBindingStatus.Bound
                    ? binding.Performance.Value?.ShowUuid
                    : null,
            LiteralDate = account.LiteralDate?.Value ?? "",
            Ordered = ordered,
            Provider = identity.Provider,
            ResourceType = identity.ResourceType,
            ProviderKey = identity.ProviderKey,
            Selected = context.Edition?.Value.Selected ?? false,
            Evidence = AccountEvidence(source),
        };
        return new()
        {
            Uuid = context.StructureUuid,
            ExpectedRevision = context.Structure?.Revision ?? 0,
            Edition = edition,
            Sets = sets,
            Occurrences = occurrences,
            Sources = context.Sources,
            SetCorrespondence = setLinks,
            PassageCorrespondence = passageLinks,
            RemovalAuthorizations = removals
                .Select(value => new SetlistRemovalAuthorization(
                    value.RemovalKind,
                    value.Target.Uuid,
                    Authorization(value)!.Value
                ))
                .ToArray(),
            Evidence = AccountEvidence(source),
        };
    }

    private static Guid? Authorization(SetlistMaterializationDecision decision) =>
        decision switch
        {
            SetlistClaimMaterializationDecision claim => claim.ExistingAuthorizationUuid
                ?? (
                    claim.Evaluation != null
                        ? PurposeUuid(claim.DecisionUuid, "authorization")
                        : null
                ),
            SetlistRemovalMaterializationDecision removal => removal.ExistingAuthorizationUuid
                ?? (
                    removal.Evaluation != null
                        ? PurposeUuid(removal.DecisionUuid, "authorization")
                        : null
                ),
            _ => null,
        };

    internal static bool SameImageFacts(
        SetlistStructureChange first,
        SetlistStructureChange second,
        bool equivalentReceiptVersions = false
    )
    {
        SetlistStructureChange Normalize(SetlistStructureChange image) =>
            image with
            {
                ExpectedRevision = 0,
                Edition = image.Edition with { ExpectedRevision = 0 },
                // Consumed withdrawal approvals audit a prior write. Their absence on the next
                // unchanged observation does not change the accepted setlist facts.
                RemovalAuthorizations = [],
                // Equivalent extraction receipts for the very same retained account are audit,
                // not a changed accepted image. Input revision and the full assertion stay exact.
                Sources = equivalentReceiptVersions
                    ? image
                        .Sources.Select(value => value with { ProposalAttemptUuid = Guid.Empty })
                        .ToArray()
                    : image.Sources,
                Occurrences = image
                    .Occurrences.Select(value => value with { ExpectedRevision = 0 })
                    .ToArray(),
            };
        return Same(Normalize(first), Normalize(second));
    }

    private static string Transition(ProviderPassageObservation passage) =>
        passage.Segue?.Value == true
        || passage.TransitionMarker is { } marker
            && Regex.IsMatch(marker.Value.Trim(), @"\A(?:-?>+|→)\z", RegexOptions.CultureInvariant)
            ? "segue"
            : "unknown";

    internal static MusicUsageClaimReference UsageClaim(
        SetlistStructureSource source,
        SetlistClaimReference claim
    ) =>
        new(
            source.ProposalAttemptUuid,
            source.InputRevisionUuid,
            source.AccountIndex,
            claim.ProposalUuid,
            claim.SetIndex
        );

    internal static string ClaimKey(SetlistStructureSource source, SetlistClaimReference claim) =>
        $"claim/{source.ProposalAttemptUuid:D}/{source.InputRevisionUuid:D}/{source.AccountIndex}/{(claim.SetIndex is { } index ? "set/" + index : "passage/" + claim.ProposalUuid!.Value.ToString("D"))}";

    private static string AccountKey(SetlistStructureSource source) =>
        $"account/{source.ProposalAttemptUuid:D}/{source.InputRevisionUuid:D}/{source.AccountIndex}";

    private static bool SameClaim(
        SetlistStructureSource first,
        SetlistClaimReference firstClaim,
        SetlistStructureSource second,
        SetlistClaimReference secondClaim
    ) =>
        first.ProposalAttemptUuid == second.ProposalAttemptUuid
        && first.InputRevisionUuid == second.InputRevisionUuid
        && first.AccountIndex == second.AccountIndex
        && firstClaim == secondClaim;

    private static NormalizedEvidenceReference[] AccountEvidence(SetlistStructureSource source) =>
        new[] { source.Account.Account.Origin.Evidence }
            .Concat(source.Account.Account.LiteralDate is { } date ? [date.Evidence] : [])
            .Distinct()
            .ToArray();

    internal static bool Same<T>(T first, T second) =>
        JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(first),
            JsonSerializer.SerializeToNode(second)
        );
}
