using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRecordingContentMaterializer
{
    internal static RecordingContentMaterializationPreparation BuildPreparation(
        RecordingContentMaterializationContext context,
        IReadOnlyList<RecordingContentMaterializationDecision>? allocations = null
    )
    {
        var plan = new RecordingFilePlan();
        var sources = new RecordingSourceIndex(context.Consumption);
        AddUnboundFileDecisions(context, plan, allocations);
        foreach (
            var files in ProjectSourceImages(context.Consumption)
                .GroupBy(image => image.Container.TrackUuid)
        )
        {
            plan.Include(BuildFilePlan(context, sources, files.ToArray(), allocations));
        }
        foreach (
            var row in context.Contents.Where(row =>
                row.Managed
                && !row.Record.Value.Retracted
                && !plan.Decisions.Any(decision => decision.ContentUuid == row.Record.Value.Uuid)
            )
        )
        {
            var value = row.Record.Value;
            plan.Decisions.Add(
                Decision(
                    "retain:" + value.Uuid.ToString("D"),
                    null,
                    MusicMaterializationDecisionDisposition.Retained,
                    "prior_content_retained",
                    value.Uuid,
                    row.Record.Revision,
                    value.Evidence,
                    allocations
                )
            );
        }
        return new(
            context,
            new(context.SourceUuid, plan.Contents.Values.ToArray(), plan.Maps, plan.Withdrawals),
            plan.Decisions
        )
        {
            CorrespondenceContexts = plan.Evaluations,
        };
    }

    private static void AddUnboundFileDecisions(
        RecordingContentMaterializationContext context,
        RecordingFilePlan plan,
        IReadOnlyList<RecordingContentMaterializationDecision>? allocations
    )
    {
        foreach (
            var input in NormalizationRecordingReadSet.Inputs(
                context.Consumption.Recording!.Preparation
            )
        )
        {
            foreach (
                var file in input.Binding.Files.Where(file =>
                    file.Binding.Status != NormalizationCatalogBindingStatus.Bound
                )
            )
            {
                var origin = file.Observation.Origin;
                var key =
                    "unbound:"
                    + input.Binding.InputRevisionUuid.ToString("D")
                    + ":"
                    + origin.Evidence.JsonPointer;
                plan.Decisions.Add(
                    Decision(
                        key,
                        null,
                        MusicMaterializationDecisionDisposition.Unresolved,
                        "file_binding_unresolved",
                        null,
                        0,
                        [origin.Evidence],
                        allocations
                    )
                );
            }
        }
    }

    private static RecordingFilePlan BuildFilePlan(
        RecordingContentMaterializationContext context,
        RecordingSourceIndex sources,
        MusicUsageSourceImage[] images,
        IReadOnlyList<RecordingContentMaterializationDecision>? allocations
    )
    {
        var plan = new RecordingFilePlan();
        if (images.Select(image => SequenceSignature(sources, image)).Distinct().Count() != 1)
        {
            foreach (var image in images)
            {
                AddFileDecisions(
                    sources,
                    image,
                    "conflicting_file_images",
                    MusicMaterializationDecisionDisposition.Review,
                    plan.Decisions,
                    allocations
                );
            }
            return plan;
        }

        var contents = context
            .Contents.Where(row =>
                row.Record.Value.SourceTrackUuid == images[0].Container.TrackUuid
            )
            .ToArray();
        foreach (var image in images)
        {
            var before = context
                .PriorSourceImages.Where(old =>
                    old.Container.TrackUuid == image.Container.TrackUuid
                    && old.Container.AccountIdentity == image.Container.AccountIdentity
                )
                .ToArray();
            var needsEvaluation =
                before.Length == 1
                && image.Claims.Any(claim =>
                    !context.PriorCorrespondence.Any(map =>
                        SameLiteralClaim(ClaimReference(map.Source), claim.Claim)
                        && contents.Any(row => row.Record.Value.Uuid == map.ContentUuid)
                    )
                );
            var evaluation = needsEvaluation
                ? new MusicUsageCorrespondenceContext(
                    before[0] with
                    {
                        Claims = before[0]
                            .Claims.Select(claim =>
                                claim with
                                {
                                    RepertoireSongUuid = CanonicalSong(
                                        context,
                                        claim.RepertoireSongUuid
                                    ),
                                }
                            )
                            .ToArray(),
                    },
                    image,
                    contents.Select(row => AcceptedTarget(context, row, before[0])).ToArray(),
                    AuthorizationHeads(context, image.Container.TrackUuid, contents),
                    SourceReadSet(context.Consumption, context.Policy),
                    context
                        .KnownCorrespondences.Where(known =>
                            known.Claim.TrackUuid == image.Container.TrackUuid
                        )
                        .ToArray()
                )
                : null;
            if (evaluation != null)
            {
                plan.Evaluations.Add(evaluation);
            }
            var evaluated =
                evaluation == null
                    ? []
                    : new RepertoireConsumerCorrespondenceEvaluator().Evaluate(
                        evaluation,
                        context.Policy.Active
                    );
            foreach (var claim in image.Claims)
            {
                PlanClaim(
                    context,
                    sources,
                    claim,
                    contents,
                    before.Length > 0,
                    evaluated,
                    plan,
                    allocations
                );
            }
            AddWithdrawals(
                context,
                image,
                plan.Decisions,
                plan.Contents,
                plan.Withdrawals,
                allocations
            );
        }
        plan.ValidateFinalPositions(contents);
        plan.PreservePriorImageWhenIncomplete();
        return plan;
    }

    private static void PlanClaim(
        RecordingContentMaterializationContext context,
        RecordingSourceIndex sources,
        MusicUsageClaimFact claim,
        RecordingContentAcceptedRow[] contents,
        bool hasPriorImage,
        IReadOnlyList<MusicUsageCorrespondenceDecision> evaluated,
        RecordingFilePlan plan,
        IReadOnlyList<RecordingContentMaterializationDecision>? allocations
    )
    {
        var source = sources.Reference(claim.Claim);
        var authorities = context
            .Authorizations.Where(authority =>
                authority.Active
                && !authority.Withdrawal
                && authority.Source != null
                && SameLiteralClaim(ClaimReference(authority.Source), claim.Claim)
            )
            .ToArray();
        var selected = ResolveClaimTarget(context, claim, contents, authorities, evaluated);
        var target = selected.Target;
        var reason = selected.InvalidAuthorization
            ? "authorization_target_changed"
            : ValidateClaim(
                context,
                claim,
                target,
                contents.Where(row => !row.Record.Value.Retracted).ToArray(),
                authorities,
                hasPriorImage,
                selected.AuthorizationUuid != null
            );
        if (reason != null)
        {
            plan.Decisions.Add(
                Decision(
                    source.CorrespondenceKey,
                    source,
                    reason == "repertoire_unresolved"
                        ? MusicMaterializationDecisionDisposition.Unresolved
                        : MusicMaterializationDecisionDisposition.Review,
                    reason,
                    null,
                    0,
                    claim.Evidence,
                    allocations
                )
            );
            return;
        }

        var priorDecision = allocations?.SingleOrDefault(decision =>
            decision.Key == source.CorrespondenceKey
        );
        var uuid =
            target?.Record.Value.Uuid
            ?? plan.Maps.FirstOrDefault(map =>
                map.Source.TrackUuid == source.TrackUuid
                && map.Source.SegmentPosition == source.SegmentPosition
            )?.ContentUuid
            ?? priorDecision?.ContentUuid
            ?? Guid.NewGuid();
        var segment = sources.Segment(claim.Claim)!;
        var change = new TrackContentChange
        {
            Uuid = uuid,
            ExpectedRevision = target?.Record.Revision ?? 0,
            SourceTrackUuid = source.TrackUuid,
            Position = source.SegmentPosition,
            Kind = claim.ContentKind,
            OriginalText = segment.Evidence.Quote,
            RepertoireSongUuid = claim.RepertoireSongUuid,
            Basis = "title_derived",
            Evidence = claim.Evidence,
            TitleFacts = TitleFacts(sources.Proposal(claim.Claim), claim.Claim, segment),
        };
        if (
            context.ReviewedContentExclusions.Contains(
                NormalizedMusicService.RecordingContentClaimHash(change)
            )
            || context.ReviewedContentExclusions.Contains(
                NormalizedMusicService.RecordingContentClaimHash(change with { TitleFacts = null })
            )
        )
        {
            plan.Decisions.Add(
                Decision(
                    source.CorrespondenceKey,
                    source,
                    MusicMaterializationDecisionDisposition.Review,
                    "reviewed_content_exclusion",
                    null,
                    0,
                    claim.Evidence,
                    allocations
                )
            );
            return;
        }
        var unchanged = target != null && SameContentFacts(target, change, context);
        var decision = Decision(
            source.CorrespondenceKey,
            source,
            unchanged
                ? MusicMaterializationDecisionDisposition.Retained
                : MusicMaterializationDecisionDisposition.Accepted,
            unchanged ? "exact_content_retained"
                : target == null ? "initial_source_assertion"
                : selected.Evaluation?.Reason ?? "exact_source_correspondence",
            uuid,
            target?.Record.Revision ?? 0,
            claim.Evidence,
            allocations
        ) with
        {
            ExistingAuthorizationUuid = selected.AuthorizationUuid,
            Evaluation = selected.Evaluation,
        };
        plan.Decisions.Add(decision);
        plan.Maps.Add(
            new(
                uuid,
                target?.Record.Revision ?? 0,
                source,
                selected.AuthorizationUuid
                    ?? (selected.Evaluation == null ? null : decision.DecisionUuid)
            )
        );
        if (!unchanged)
        {
            plan.Contents[uuid] = change;
        }
    }

    private static ClaimTarget ResolveClaimTarget(
        RecordingContentMaterializationContext context,
        MusicUsageClaimFact claim,
        RecordingContentAcceptedRow[] contents,
        RecordingContentAuthorizationRecord[] authorities,
        IReadOnlyList<MusicUsageCorrespondenceDecision> evaluated
    )
    {
        var exact = context
            .PriorCorrespondence.Where(map =>
                SameLiteralClaim(ClaimReference(map.Source), claim.Claim)
            )
            .Select(map =>
                contents.SingleOrDefault(row => row.Record.Value.Uuid == map.ContentUuid)
            )
            .OfType<RecordingContentAcceptedRow>()
            .DistinctBy(row => row.Record.Value.Uuid)
            .ToArray();
        var target = exact.Length == 1 ? exact[0] : null;
        var authority = authorities.SingleOrDefault(value =>
            value.Action == SetlistAuthorizationAction.Authorize
        );
        if (authority != null)
        {
            target = contents.SingleOrDefault(row =>
                row.Record.Value.Uuid == authority.Targets[0].Uuid
                && (
                    row.Record.Revision == authority.Targets[0].ExpectedRevision
                    || context.PriorCorrespondence.Any(map =>
                        map.ContentUuid == row.Record.Value.Uuid
                        && map.AuthorizationUuid == authority.AuthorizationUuid
                        && map.ExpectedRevision == row.Record.Revision
                    )
                )
            );
            return new(target, authority.AuthorizationUuid, null, target == null);
        }
        if (target != null)
        {
            return new(target, null, null, false);
        }
        var inferred = evaluated
            .Where(value =>
                value.Operation == MusicMaterializationOperation.Correspondence
                && value.CurrentClaim == claim.Claim
                && value.Disposition == MusicUsageDecisionDisposition.Authorized
            )
            .ToArray();
        if (inferred.Length != 1 || inferred[0].Targets.Count != 1)
        {
            return new(null, null, null, false);
        }
        var proof = inferred[0];
        target = contents.SingleOrDefault(row =>
            row.Record.Value.Uuid == proof.Targets[0].Uuid
            && row.Record.Revision == proof.Targets[0].ExpectedRevision
        );
        return new(target, null, target == null ? null : proof, false);
    }

    private sealed record ClaimTarget(
        RecordingContentAcceptedRow? Target,
        Guid? AuthorizationUuid,
        MusicUsageCorrespondenceDecision? Evaluation,
        bool InvalidAuthorization
    );

    private sealed class RecordingFilePlan
    {
        public Dictionary<Guid, TrackContentChange> Contents { get; } = [];
        public List<RecordingContentCorrespondence> Maps { get; } = [];
        public List<RecordingContentWithdrawal> Withdrawals { get; } = [];
        public List<RecordingContentMaterializationDecision> Decisions { get; } = [];
        public List<MusicUsageCorrespondenceContext> Evaluations { get; } = [];

        public void Include(RecordingFilePlan file)
        {
            foreach (var content in file.Contents)
            {
                Contents.Add(content.Key, content.Value);
            }
            Maps.AddRange(file.Maps);
            Withdrawals.AddRange(file.Withdrawals);
            Decisions.AddRange(file.Decisions);
            Evaluations.AddRange(file.Evaluations);
        }

        public void ValidateFinalPositions(RecordingContentAcceptedRow[] current)
        {
            // Moves and removals publish atomically. Check their final overlay rather than rejecting
            // a destination that is occupied only by a row removed in this same complete file image.
            var final = current
                .Where(row => !row.Record.Value.Retracted)
                .ToDictionary(row => row.Record.Value.Uuid, row => row.Record.Value);
            foreach (var change in Contents.Values)
            {
                if (change.Retracted)
                {
                    final.Remove(change.Uuid);
                }
                else
                {
                    final[change.Uuid] = change;
                }
            }
            if (!final.Values.GroupBy(value => value.Position).Any(group => group.Count() > 1))
            {
                return;
            }
            RequireReview("content_position_conflict");
        }

        public void PreservePriorImageWhenIncomplete()
        {
            var hasPositiveClaim = Decisions.Any(decision =>
                decision.Source != null
                && decision.Disposition
                    is MusicMaterializationDecisionDisposition.Accepted
                        or MusicMaterializationDecisionDisposition.Retained
            );
            var incomplete =
                Decisions.Any(decision =>
                    decision.Disposition == MusicMaterializationDecisionDisposition.Review
                )
                || hasPositiveClaim
                    && Decisions.Any(decision =>
                        decision.Disposition == MusicMaterializationDecisionDisposition.Unresolved
                    );
            if (!incomplete)
            {
                return;
            }
            // A partially understood current sequence cannot replace a complete file. A pure missing
            // title may still withdraw the entire prior image through independent reviewed authority.
            RequireReview("complete_file_image_requires_review");
        }

        private void RequireReview(string reason)
        {
            Contents.Clear();
            Maps.Clear();
            Withdrawals.Clear();
            for (var index = 0; index < Decisions.Count; index++)
            {
                if (
                    Decisions[index].Disposition
                    is MusicMaterializationDecisionDisposition.Accepted
                        or MusicMaterializationDecisionDisposition.Withdrawn
                        or MusicMaterializationDecisionDisposition.Retained
                )
                {
                    Decisions[index] = Decisions[index] with
                    {
                        Disposition = MusicMaterializationDecisionDisposition.Review,
                        Reason = reason,
                        ContentUuid = null,
                        ExpectedRevision = 0,
                        ExistingAuthorizationUuid = null,
                        Evaluation = null,
                    };
                }
            }
        }
    }

    private static string? ValidateClaim(
        RecordingContentMaterializationContext context,
        MusicUsageClaimFact claim,
        RecordingContentAcceptedRow? target,
        RecordingContentAcceptedRow[] active,
        RecordingContentAuthorizationRecord[] authorities,
        bool hasPriorImage,
        bool explicitAuthority
    )
    {
        if (
            claim.Position == null
            || claim.ContentKind == "unknown"
            || claim.ContentKind == "song" && claim.RepertoireSongUuid == null
        )
        {
            return "repertoire_unresolved";
        }
        if (authorities.Any(authority => authority.Action != SetlistAuthorizationAction.Authorize))
        {
            return "correspondence_blocked_or_revoked";
        }
        if (target?.Record.Value.Retracted == true && !explicitAuthority)
        {
            return "withdrawn_content_requires_authority";
        }
        if (
            target != null
            && (
                target.Record.Value.Basis == "reviewed"
                || !target.Managed
                || !explicitAuthority
                    && (
                        target.SourceUuid != context.SourceUuid
                        || target.ArtistUuid != context.ArtistUuid
                    )
            )
        )
        {
            return "reviewed_or_unmanaged_content";
        }
        if (target == null && (hasPriorImage || active.Any(row => row.Managed)))
        {
            return "revised_file_correspondence_required";
        }
        return null;
    }

    private static void AddWithdrawals(
        RecordingContentMaterializationContext context,
        MusicUsageSourceImage image,
        List<RecordingContentMaterializationDecision> decisions,
        Dictionary<Guid, TrackContentChange> contents,
        List<RecordingContentWithdrawal> withdrawals,
        IReadOnlyList<RecordingContentMaterializationDecision>? allocations
    )
    {
        var mapped = decisions
            .Where(value =>
                value.Source?.TrackUuid == image.Container.TrackUuid && !value.Withdrawal
            )
            .Select(value => value.ContentUuid)
            .OfType<Guid>()
            .ToHashSet();
        foreach (
            var row in context.Contents.Where(row =>
                row.Managed
                && !row.Record.Value.Retracted
                && row.Record.Value.SourceTrackUuid == image.Container.TrackUuid
                && !mapped.Contains(row.Record.Value.Uuid)
            )
        )
        {
            var value = row.Record.Value;
            var key = "withdraw:" + value.Uuid.ToString("D");
            if (decisions.Any(decision => decision.Key == key))
            {
                continue;
            }
            var authority = context.Authorizations.SingleOrDefault(item =>
                item.Active
                && item.Withdrawal
                && item.Action == SetlistAuthorizationAction.Authorize
                && item.Targets.Count == 1
                && item.Targets[0].Uuid == value.Uuid
                && item.Targets[0].ExpectedRevision == row.Record.Revision
            );
            // Changed medley segments have no native occurrence identifiers. Until an authenticated
            // current-neighbor proof is available, recording removals require exact manual authority.
            if (value.Basis == "reviewed" || authority == null)
            {
                decisions.Add(
                    Decision(
                        key,
                        null,
                        MusicMaterializationDecisionDisposition.Review,
                        "withdrawal_authority_required",
                        value.Uuid,
                        row.Record.Revision,
                        value.Evidence,
                        allocations
                    ) with
                    {
                        Withdrawal = true,
                    }
                );
                continue;
            }
            var decision = Decision(
                key,
                null,
                MusicMaterializationDecisionDisposition.Withdrawn,
                "authorized_withdrawal",
                value.Uuid,
                row.Record.Revision,
                value.Evidence,
                allocations
            ) with
            {
                Withdrawal = true,
                ExistingAuthorizationUuid = authority.AuthorizationUuid,
            };
            decisions.Add(decision);
            contents[value.Uuid] = value with
            {
                ExpectedRevision = row.Record.Revision,
                Retracted = true,
            };
            withdrawals.Add(
                new(value.Uuid, row.Record.Revision, authority.AuthorizationUuid, value.Evidence)
            );
        }
    }

    private static void AddFileDecisions(
        RecordingSourceIndex sources,
        MusicUsageSourceImage image,
        string reason,
        MusicMaterializationDecisionDisposition disposition,
        List<RecordingContentMaterializationDecision> decisions,
        IReadOnlyList<RecordingContentMaterializationDecision>? allocations
    )
    {
        foreach (var claim in image.Claims)
        {
            var source = sources.Reference(claim.Claim);
            decisions.Add(
                Decision(
                    source.CorrespondenceKey,
                    source,
                    disposition,
                    reason,
                    null,
                    0,
                    claim.Evidence,
                    allocations
                )
            );
        }
    }

    private static RecordingContentMaterializationDecision Decision(
        string key,
        RecordingContentSourceReference? source,
        MusicMaterializationDecisionDisposition disposition,
        string reason,
        Guid? content,
        long revision,
        IReadOnlyList<NormalizedEvidenceReference> evidence,
        IReadOnlyList<RecordingContentMaterializationDecision>? allocations
    ) =>
        new(
            allocations?.SingleOrDefault(value => value.Key == key)?.DecisionUuid ?? Guid.NewGuid(),
            key,
            disposition,
            reason,
            source,
            content,
            revision,
            evidence
        );

    internal static bool SameLiteralClaim(
        MusicUsageClaimReference left,
        MusicUsageClaimReference right
    ) =>
        left.InputRevisionUuid == right.InputRevisionUuid
        && left.ProposalUuid == right.ProposalUuid
        && left.TrackUuid == right.TrackUuid
        && left.SegmentPosition == right.SegmentPosition;

    private static TrackContentTitleFacts TitleFacts(
        NormalizationRecordingTitleProposal proposal,
        MusicUsageClaimReference reference,
        TrackTitleSegment segment
    )
    {
        var title = proposal.Extraction!;
        var qualifiers = title
            .Qualifiers.Where(qualifier =>
                qualifier.Kind switch
                {
                    "partial_marker" => qualifier.Evidence.EndCodePoint
                        <= segment.Evidence.StartCodePoint
                        && WhitespaceOnly(
                            title.OriginalTitle,
                            qualifier.Evidence.EndCodePoint,
                            segment.Evidence.StartCodePoint
                        ),
                    "partial_coverage" => qualifier.Evidence.StartCodePoint
                        >= segment.Evidence.EndCodePoint
                        && WhitespaceOnly(
                            title.OriginalTitle,
                            segment.Evidence.EndCodePoint,
                            qualifier.Evidence.StartCodePoint
                        ),
                    _ => false,
                }
            )
            .Select(qualifier => new TrackContentTitleQualifier(
                qualifier.Kind,
                new(
                    reference.InputRevisionUuid,
                    proposal.Title!.Evidence.JsonPointer,
                    (proposal.Title.Evidence.StartCodePoint ?? 0)
                        + qualifier.Evidence.StartCodePoint,
                    (proposal.Title.Evidence.StartCodePoint ?? 0) + qualifier.Evidence.EndCodePoint,
                    qualifier.Evidence.Quote
                )
            ))
            .ToArray();
        return new(
            segment.Coverage,
            segment.TransitionAfter,
            qualifiers,
            proposal.Title!.Evidence with
            {
                Quote = null,
            }
        );
    }

    private static bool WhitespaceOnly(string text, int start, int end) =>
        text.EnumerateRunes().Skip(start).Take(end - start).All(Rune.IsWhiteSpace);

    private static Guid? CanonicalSong(
        RecordingContentMaterializationContext context,
        Guid? song
    ) =>
        song is { } uuid
            ? context
                .Consumption.Targets.FirstOrDefault(target =>
                    target.IdentityPath.Any(identity => identity.Uuid == uuid)
                )
                ?.CanonicalUuid
                ?? uuid
            : null;

    private static MusicUsageAcceptedTarget AcceptedTarget(
        RecordingContentMaterializationContext context,
        RecordingContentAcceptedRow row,
        MusicUsageSourceImage before
    )
    {
        var map = context.PriorCorrespondence.LastOrDefault(map =>
            map.ContentUuid == row.Record.Value.Uuid
            && map.ExpectedRevision == row.Record.Revision
            && before.Claims.Any(claim => claim.Claim == ClaimReference(map.Source))
        );
        var value = row.Record.Value;
        return new(
            value.Uuid,
            row.Record.Revision,
            map == null ? null : ClaimReference(map.Source),
            value.Position,
            value.Kind,
            CanonicalSong(context, value.RepertoireSongUuid),
            value.Basis,
            value.Retracted,
            value.Evidence
        );
    }

    private static IReadOnlyList<MusicUsageAuthorizationHead> AuthorizationHeads(
        RecordingContentMaterializationContext context,
        Guid? track,
        RecordingContentAcceptedRow[] contents
    ) =>
        context
            .Authorizations.Where(authority =>
                authority.Source?.TrackUuid == track
                || authority.Targets.Any(target =>
                    contents.Any(row => row.Record.Value.Uuid == target.Uuid)
                )
            )
            .Select(authority => new MusicUsageAuthorizationHead(
                authority.AuthorizationUuid,
                authority.Withdrawal
                    ? MusicMaterializationOperation.Withdrawal
                    : MusicMaterializationOperation.Correspondence,
                authority.Source == null ? null : ClaimReference(authority.Source),
                authority.Targets.Count == 1 ? authority.Targets[0].Uuid : null,
                authority.Action
            ))
            .ToArray();

    private static string SequenceSignature(
        RecordingSourceIndex sources,
        MusicUsageSourceImage image
    ) =>
        JsonSerializer.Serialize(
            image.Claims.Select(claim =>
            {
                var segment = sources.Segment(claim.Claim);
                var facts =
                    segment == null
                        ? null
                        : TitleFacts(sources.Proposal(claim.Claim), claim.Claim, segment);
                return new
                {
                    claim.Position,
                    claim.ContentKind,
                    claim.RepertoireSongUuid,
                    Text = segment?.Evidence.Quote ?? claim.Evidence.FirstOrDefault()?.Quote,
                    Coverage = facts?.Coverage,
                    TransitionAfter = facts?.TransitionAfter,
                    Qualifiers = facts?.Qualifiers.Select(qualifier => new
                    {
                        qualifier.Kind,
                        qualifier.Evidence.Quote,
                    }),
                };
            })
        );

    private static bool SameTitleFacts(
        TrackContentTitleFacts? current,
        TrackContentTitleFacts? proposed
    ) =>
        current == null || proposed == null
            ? current == proposed
            : current.Coverage == proposed.Coverage
                && current.TransitionAfter == proposed.TransitionAfter
                && current
                    .Qualifiers.Select(qualifier => (qualifier.Kind, qualifier.Evidence.Quote))
                    .SequenceEqual(
                        proposed.Qualifiers.Select(qualifier =>
                            (qualifier.Kind, qualifier.Evidence.Quote)
                        )
                    );

    internal static bool SameContentFacts(
        RecordingContentAcceptedRow row,
        TrackContentChange proposed,
        RecordingContentMaterializationContext context
    )
    {
        var current = row.Record.Value;
        return row.SourceUuid == context.SourceUuid
            && row.ArtistUuid == context.ArtistUuid
            && current.SourceTrackUuid == proposed.SourceTrackUuid
            && current.Position == proposed.Position
            && current.Kind == proposed.Kind
            && current.OriginalText == proposed.OriginalText
            && current.RepertoireSongUuid == proposed.RepertoireSongUuid
            && current.OccurrenceUuid == null
            && current.StartMs == null
            && current.EndMs == null
            && !current.Retracted
            && SameTitleFacts(current.TitleFacts, proposed.TitleFacts);
    }
}
