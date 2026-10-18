using System;
using System.Collections.Generic;
using System.Linq;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationSetlistMaterializer
{
    internal static IReadOnlyList<MusicUsageCorrespondenceContext> CorrespondenceContexts(
        SetlistMaterializationContext context
    )
    {
        if (context.Structure?.Value.Sources.Count != 1 || context.Sources.Count != 1)
        {
            return [];
        }
        var previous = context.Structure.Value.Sources.Single();
        var current = context.Sources.Single();
        var accountContexts = new[] { MusicUsageKind.Set, MusicUsageKind.Occurrence }
            .Select(kind =>
            {
                var before = SourceImage(context, previous, kind, previous: true);
                var after = SourceImage(context, current, kind, previous: false);
                var targets =
                    kind == MusicUsageKind.Set
                        ? context
                            .Sets.Select(value => new MusicUsageAcceptedTarget(
                                value.Value.Uuid,
                                value.Revision,
                                PriorClaim(context, previous, kind, value.Value.Uuid),
                                value.Value.Position,
                                "set",
                                null,
                                context.Structure.Value.Basis,
                                value.Value.Retracted,
                                value.Value.Evidence
                            ))
                            .ToArray()
                        : context
                            .Occurrences.Select(value => new MusicUsageAcceptedTarget(
                                value.Value.Uuid,
                                value.Revision,
                                PriorClaim(context, previous, kind, value.Value.Uuid),
                                value.Value.Position,
                                value.Value.Kind,
                                value.Value.RepertoireSongUuid,
                                value.Value.Basis,
                                value.Value.Retracted,
                                value.Value.Evidence
                            ))
                            .ToArray();
                var heads = context
                    .Authorizations.SelectMany(value =>
                        value.Correspondence is { } claim
                            ? claim.Targets.Select(target => new MusicUsageAuthorizationHead(
                                value.AuthorizationUuid,
                                MusicMaterializationOperation.Correspondence,
                                UsageClaim(claim.Source, claim.Claim),
                                target.Uuid,
                                value.Action
                            ))
                            : new[]
                            {
                                new MusicUsageAuthorizationHead(
                                    value.AuthorizationUuid,
                                    MusicMaterializationOperation.Withdrawal,
                                    null,
                                    value.Removal?.Target.Uuid,
                                    value.Action
                                ),
                            }
                    )
                    .ToArray();
                return new MusicUsageCorrespondenceContext(
                    before,
                    after,
                    targets,
                    heads,
                    context.ReadSet
                );
            })
            .ToList();
        var wholePool = accountContexts.Single(value =>
            value.After.Kind == MusicUsageKind.Occurrence
        );
        foreach (
            var currentSet in current.Account.Account.Sets.Where(value => value.Identity != null)
        )
        {
            var previousSets = previous
                .Account.Account.Sets.Where(value =>
                    value.Identity?.Identity == currentSet.Identity!.Identity
                )
                .ToArray();
            if (previousSets.Length != 1)
            {
                continue;
            }
            var before = PerSetImage(wholePool.Before, previous, previousSets[0]);
            var after = PerSetImage(wholePool.After, current, currentSet);
            accountContexts.Add(
                new(
                    before,
                    after,
                    wholePool.AcceptedTargets,
                    wholePool.AuthorizationHeads,
                    context.ReadSet
                )
            );
        }
        return accountContexts;
    }

    private static MusicUsageSourceImage PerSetImage(
        MusicUsageSourceImage account,
        SetlistStructureSource source,
        ProviderSetObservation set
    )
    {
        var proposals = source
            .Account.Passages.Where(value => value.Passage.SetOrdinal == set.Ordinal)
            .Select(value => value.ProposalUuid)
            .ToHashSet();
        return account with
        {
            Container = account.Container with { SetIdentity = set.Identity },
            Claims = account
                .Claims.Where(value =>
                    value.Claim.ProposalUuid is { } uuid && proposals.Contains(uuid)
                )
                .ToArray(),
            CompetingClaims = account
                .Claims.Where(value =>
                    value.Claim.ProposalUuid is not { } uuid || !proposals.Contains(uuid)
                )
                .ToArray(),
        };
    }

    private static MusicUsageSourceImage SourceImage(
        SetlistMaterializationContext context,
        SetlistStructureSource source,
        MusicUsageKind kind,
        bool previous
    )
    {
        IReadOnlyList<MusicUsageClaimFact> facts;
        if (kind == MusicUsageKind.Set)
        {
            facts = source
                .Account.Account.Sets.Select(
                    (set, index) =>
                        new MusicUsageClaimFact(
                            UsageClaim(source, new(SetIndex: index)),
                            set.Ordinal,
                            "set",
                            null,
                            set.Identity,
                            [set.Evidence]
                        )
                )
                .ToArray();
        }
        else
        {
            facts = source
                .Account.Passages.Select(proposal =>
                {
                    var claim = UsageClaim(source, new(ProposalUuid: proposal.ProposalUuid));
                    string contentKind;
                    Guid? song;
                    if (previous)
                    {
                        var ids = context
                            .Structure!.Value.PassageCorrespondence.Where(value =>
                                value.SourceIndex == 0
                                && value.ProposalUuid == proposal.ProposalUuid
                            )
                            .SelectMany(value => value.OccurrenceUuids)
                            .ToArray();
                        var target =
                            ids.Length == 1
                                ? context.Occurrences.SingleOrDefault(value =>
                                    value.Value.Uuid == ids[0]
                                )
                                : null;
                        contentKind = target?.Value.Kind ?? "unknown";
                        song = target?.Value.RepertoireSongUuid;
                    }
                    else
                    {
                        var original = context.Consumption.Source.Claims.Single(value =>
                            value.ProposalUuid == proposal.ProposalUuid
                        );
                        var decision = context.Consumption.Decisions.Single(value =>
                            value.ClaimUuid == original.ClaimUuid
                        );
                        contentKind = original.Kind;
                        song =
                            decision.RepertoireSongUuid is { } requested
                            && decision.Disposition
                                is RepertoireResolutionClaimDisposition.Bound
                                    or RepertoireResolutionClaimDisposition.Provisional
                                ? context
                                    .Consumption.Targets.Single(value =>
                                        value.RequestedUuid == requested
                                    )
                                    .CanonicalUuid
                                : null;
                    }
                    return new MusicUsageClaimFact(
                        claim,
                        proposal.Passage.Ordinal,
                        contentKind,
                        song,
                        proposal.Passage.OccurrenceIdentity,
                        [proposal.Passage.Title.Evidence]
                    );
                })
                .ToArray();
        }
        return new(
            kind,
            new(context.Consumption.Source.Lineage, source.Account.Account.Origin.Item!),
            Version,
            RepertoireConsumerCorrespondenceEvaluator.SourceFactsVersion,
            facts,
            []
        );
    }

    private static MusicUsageClaimReference? PriorClaim(
        SetlistMaterializationContext context,
        SetlistStructureSource source,
        MusicUsageKind kind,
        Guid target
    )
    {
        if (kind == MusicUsageKind.Set)
        {
            var links = context
                .Structure!.Value.SetCorrespondence.Where(value =>
                    value.SourceIndex == 0 && value.SetUuid == target
                )
                .ToArray();
            return links.Length == 1 ? UsageClaim(source, new(SetIndex: links[0].SetIndex)) : null;
        }
        var passages = context
            .Structure!.Value.PassageCorrespondence.Where(value =>
                value.SourceIndex == 0 && value.OccurrenceUuids.Contains(target)
            )
            .ToArray();
        return passages.Length == 1
            ? UsageClaim(source, new(ProposalUuid: passages[0].ProposalUuid))
            : null;
    }
}
