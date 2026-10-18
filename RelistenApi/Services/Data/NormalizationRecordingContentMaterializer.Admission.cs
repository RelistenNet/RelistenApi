using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRecordingContentMaterializer
{
    private Task<NormalizationWorkTicket> AdmitAsync(
        RecordingContentMaterializationAttempt attempt,
        CancellationToken ct
    )
    {
        var context = attempt.Preparation!.Context!;
        var owner = context.OwnerContext;
        var ownKey = Contribution(context.SourceUuid);
        var contributions = owner
            .Membership.Contributions.Where(state => state.Contributor != ownKey)
            .Select(state =>
                PrepareContribution(
                    state.Contributor,
                    state.Revision,
                    RepertoireResolutionReadSets.ForContribution(
                        owner.Bindings,
                        state,
                        owner.InputReferences
                    ),
                    context.InputReferences
                )
            )
            .ToList();
        if (owner.Membership.UnmanagedBaseline is { } baseline)
        {
            contributions.Add(
                PrepareContribution(
                    baseline.Contributor,
                    1,
                    RepertoireResolutionReadSets.ForContribution(
                        owner.Bindings,
                        baseline,
                        owner.InputReferences
                    ),
                    context.InputReferences
                )
            );
        }
        contributions.Add(
            PrepareContribution(
                ownKey,
                owner
                    .Membership.Contributions.SingleOrDefault(state => state.Contributor == ownKey)
                    ?.Revision
                    ?? 0,
                SourceReadSet(context.Consumption, context.Policy),
                context.InputReferences
            )
        );
        var membership = owner
            .Membership.Contributions.Select(state => new NormalizationInputContributionRevision(
                state.Contributor,
                state.Revision
            ))
            .Concat(owner.Membership.UnmanagedBaseline is { } old ? [new(old.Contributor, 0)] : [])
            .ToArray();
        return work.BeginContributedBatchAsync(
            attempt.AttemptUuid,
            Owner(context.SourceUuid),
            attempt.ProcessingVersion,
            contributions,
            owner.Membership.Owner?.DesiredGeneration ?? 0,
            ct,
            membership
        );
    }

    private static NormalizationPreparedInputContribution PrepareContribution(
        NormalizationResourceKey contributor,
        long revision,
        RepertoireResolutionReadSet read,
        IReadOnlyList<NormalizationBindingInputReference> references
    )
    {
        var wanted = read.Inputs.Select(input => input.RevisionUuid).ToHashSet();
        var inputs = references
            .Where(input => wanted.Contains(input.InputRevisionUuid))
            .Distinct()
            .ToArray();
        if (inputs.Length != wanted.Count)
        {
            throw new NormalizationConflictException(
                "The complete content contribution lacks its immutable input references."
            );
        }
        return new(
            new(
                contributor,
                revision,
                inputs.Select(input => input.Resource).ToArray(),
                read.Dependencies.Select(dependency => dependency.SubjectUuid).ToArray(),
                read.OwnerObservations.Select(observation => observation.SubjectUuid).ToArray()
            ),
            inputs,
            read.CatalogInputs,
            read.SourceIdentities,
            read.MappingRegistry,
            read.ImportedVenues,
            read.CandidateUniverses,
            read.OwnerObservations,
            read.SourceUuids
        );
    }
}
