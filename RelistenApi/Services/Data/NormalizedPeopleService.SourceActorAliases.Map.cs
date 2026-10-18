using System;
using System.Collections.Generic;
using System.Linq;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedPeopleService
{
    internal static void ApplySourceAliasAddition(
        List<SourceActorAliasScopeMapEntry> map,
        SourceActorAliasPersonStage stage,
        SourceActorAliasStageDelta delta,
        IReadOnlyList<SourceActorAliasMapBinding> additions
    )
    {
        var index = map.FindIndex(entry => entry.Scope == delta.Scope);
        var prior =
            index < 0 ? new SourceActorAliasScopeMapEntry(delta.Scope, [], null, []) : map[index];
        var reference = delta.Acceptance!.Claim;
        var scope = stage.Facts.Inputs[reference.InputIndex].Scopes[reference.ScopeIndex];
        var coverage = scope
            .OriginalScope.Extraction.Scopes.Where(value =>
                value.Evidence.Any(evidence => evidence.JsonPointer == delta.Scope.FieldJsonPointer)
            )
            .ToArray();
        if (coverage.Length != 1)
        {
            throw new NormalizationConflictException(
                "Alias addition requires one exact original field-coverage observation."
            );
        }
        var observation = new SourceActorAliasScopeObservation(
            reference.Proposal,
            reference.InputIndex,
            reference.ScopeIndex,
            delta.Scope.FieldJsonPointer,
            coverage[0].Complete,
            ExplicitClear: false
        );
        // Safe names can commit before field replacement is ready. Even complete parser
        // coverage remains a preliminary observation until every required name has a receipt.
        var bindings = prior.Bindings.Concat(additions).Distinct().ToArray();
        if (
            bindings.GroupBy(binding => binding.SupportUuid).Any(group => group.Count() != 1)
            || bindings
                .GroupBy(binding => (binding.AssignmentUuid, binding.AliasUuid))
                .Any(group => group.Count() != 1)
        )
        {
            throw new NormalizationConflictException(
                "Actual alias support differs from the complete original-person map."
            );
        }
        var updated = prior with
        {
            Bindings = bindings,
            IncompleteObservations = prior
                .IncompleteObservations.Append(observation)
                .Distinct()
                .ToArray(),
        };
        if (index < 0)
        {
            map.Add(updated);
        }
        else
        {
            map[index] = updated;
        }
    }

    internal static void ApplySourceAliasIncompleteObservation(
        List<SourceActorAliasScopeMapEntry> map,
        SourceActorAliasStageDelta delta
    )
    {
        var index = map.FindIndex(entry => entry.Scope == delta.Scope);
        var observation =
            delta.Observation
            ?? throw new ArgumentException(
                "Incomplete source observation requires its exact retained scope proof."
            );
        if (
            index < 0
            || observation.Complete
            || observation.ExplicitClear
            || observation.FieldJsonPointer != delta.Scope.FieldJsonPointer
        )
        {
            throw new ArgumentException(
                "An incomplete observation preserves an existing exact field and cannot authorize retirement."
            );
        }
        map[index] = map[index] with
        {
            IncompleteObservations = map[index]
                .IncompleteObservations.Append(observation)
                .Distinct()
                .ToArray(),
        };
    }

    internal static void ApplySourceAliasRetirement(
        List<SourceActorAliasScopeMapEntry> map,
        SourceActorAliasStageDelta delta
    )
    {
        var index = map.FindIndex(entry => entry.Scope == delta.Scope);
        if (index < 0)
        {
            throw new NormalizationConflictException(
                "Retirement cannot reconstruct a missing complete scope map from raw assignment rows."
            );
        }
        var prior = map[index];
        var retiredAssignments = delta.RetireAssignmentUuids.ToHashSet();
        var retiredSupports = delta.RetireSupportUuids.ToHashSet();
        var affected = prior
            .Bindings.Where(binding =>
                retiredAssignments.Contains(binding.AssignmentUuid)
                || retiredSupports.Contains(binding.SupportUuid)
            )
            .ToArray();
        if (
            !retiredAssignments.IsSubsetOf(
                affected.Select(binding => binding.AssignmentUuid).ToHashSet()
            )
            || prior.Bindings.Any(binding =>
                retiredAssignments.Contains(binding.AssignmentUuid) && !affected.Contains(binding)
            )
            || !retiredSupports.SetEquals(affected.Select(binding => binding.SupportUuid))
        )
        {
            throw new NormalizationConflictException(
                "Retirement must enumerate every support of each addressed original scope assignment."
            );
        }
        var remaining = prior.Bindings.Except(affected).ToArray();
        if (delta.Kind == SourceActorAliasDeltaKind.Retire)
        {
            var authority =
                delta.Acceptance
                ?? throw new ArgumentException(
                    "Negative source retirement requires its exact authenticated trusted receipt."
                );
            map[index] = prior with
            {
                Bindings = remaining,
                LastWithdrawalAuthorities = (prior.LastWithdrawalAuthorities ?? [])
                    .Append(authority)
                    .Distinct()
                    .ToArray(),
            };
            return;
        }
        var observation =
            delta.Observation
            ?? throw new ArgumentException(
                "Complete replacement requires its exact original field-coverage proof."
            );
        if (
            !observation.Complete
            || observation.FieldJsonPointer != delta.Scope.FieldJsonPointer
            || delta.Kind == SourceActorAliasDeltaKind.Clear
                && (!observation.ExplicitClear || remaining.Length != 0)
        )
        {
            throw new NormalizationConflictException(
                "Complete replacement or clear cannot consume another field or retain an uncleared binding."
            );
        }
        map[index] = prior with
        {
            Bindings = remaining,
            LastCompleteObservation = observation,
            IncompleteObservations = [],
            LastWithdrawalAuthorities = null,
        };
    }
}
