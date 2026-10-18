using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Groups committed name outcomes once without reopening their original proof bodies.
/// This is a structural index, not publication authority: historical consumers authenticate
/// the supplied headers through the store and still validate current reviews and support.
/// </summary>
internal sealed class SourceActorAliasApplicationIndex
{
    internal SourceActorAliasApplicationIndex(IEnumerable<SourceActorAliasPersonStageHeader> stages)
    {
        var claims =
            new Dictionary<SourceActorAliasClaimReference, CommittedSourceActorAliasClaim>();
        var stageIds = new HashSet<Guid>();
        var workIds = new HashSet<Guid>();
        foreach (var stage in stages.Where(value => value.Receipt != null))
        {
            if (
                !stageIds.Add(stage.StageUuid)
                || !workIds.Add(stage.WorkUuid)
                || !stage.Admitted
                || stage.TerminalReason != null
            )
            {
                throw new NormalizationConflictException(
                    "Committed source claim headers require unique admitted stages without terminal refusal."
                );
            }
            var prepared = stage
                .PreparedNames.GroupBy(value => value.Claim)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(value => value.Name).ToArray()
                );
            var outcomes = stage
                .Receipt!.Names.GroupBy(value => value.Claim)
                .ToDictionary(group => group.Key, group => group.ToArray());
            var authorities = stage
                .Acceptances.GroupBy(value => value.Claim)
                .ToDictionary(group => group.Key, group => group.ToArray());
            if (
                !prepared.Keys.ToHashSet().SetEquals(outcomes.Keys)
                || stage
                    .PreparedNames.Select(value => value.Name.RequestedAliasUuid)
                    .Distinct()
                    .Count() != stage.PreparedNames.Count
            )
            {
                throw new NormalizationConflictException(
                    "Committed source outcomes must cover exactly their prepared name claims."
                );
            }
            // Withdrawal and finalization headers may have no prepared names. Their retained
            // acceptance references do not create a committed positive claim in this index.
            foreach (var (key, names) in prepared)
            {
                if (
                    !authorities.TryGetValue(key, out var acceptance)
                    || acceptance.Length != 1
                    || acceptance[0].OriginalSelectedPersonUuid != stage.OriginalPersonUuid
                )
                {
                    throw new NormalizationConflictException(
                        "A committed source claim requires exactly one original acceptance reference."
                    );
                }
                var actual = outcomes[key];
                var requested = names.Select(value => value.RequestedAliasUuid).ToHashSet();
                if (
                    requested.Count != names.Length
                    || actual.Length != names.Length
                    || !requested.SetEquals(actual.Select(value => value.RequestedAliasUuid))
                    || actual.Select(value => value.RequestedAliasUuid).Distinct().Count()
                        != actual.Length
                )
                {
                    throw new NormalizationConflictException(
                        "A committed source claim requires one outcome per unique prepared name UUID."
                    );
                }
                var committed = new CommittedSourceActorAliasClaim(
                    stage,
                    acceptance[0],
                    Array.AsReadOnly(names),
                    Array.AsReadOnly(actual)
                );
                if (
                    actual.Any(value =>
                        committed.NamesByRequestedUuid[value.RequestedAliasUuid].Wording
                        != value.Wording
                    ) || !claims.TryAdd(key, committed)
                )
                {
                    throw new NormalizationConflictException(
                        "A source claim cannot change its prepared wording or belong to multiple committed stages."
                    );
                }
            }
        }
        Claims = new ReadOnlyDictionary<
            SourceActorAliasClaimReference,
            CommittedSourceActorAliasClaim
        >(claims);
    }

    internal IReadOnlyDictionary<
        SourceActorAliasClaimReference,
        CommittedSourceActorAliasClaim
    > Claims { get; }
}

internal sealed record CommittedSourceActorAliasClaim(
    SourceActorAliasPersonStageHeader Stage,
    SourceActorAliasAcceptanceReference Acceptance,
    IReadOnlyList<SourceActorAliasPreparedName> PreparedNames,
    IReadOnlyList<SourceActorAliasNameOutcome> Outcomes
)
{
    internal IReadOnlyDictionary<Guid, SourceActorAliasPreparedName> NamesByRequestedUuid { get; } =
        new ReadOnlyDictionary<Guid, SourceActorAliasPreparedName>(
            PreparedNames.ToDictionary(value => value.RequestedAliasUuid)
        );
}
