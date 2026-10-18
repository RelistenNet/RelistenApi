using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationSetlistMaterializer
{
    private async Task<NormalizationWorkTicket> AdmitContributionAsync(
        Guid workUuid,
        NormalizationSubjectKey owner,
        string version,
        NormalizationResourceKey contributor,
        MusicMaterializationOwnerContext context,
        RepertoireResolutionReadSet own,
        IReadOnlyList<NormalizationBindingInputReference> references,
        CancellationToken ct
    )
    {
        var contributions = context
            .Membership.Contributions.Where(state => state.Contributor != contributor)
            .Select(state =>
                PrepareContribution(
                    state.Contributor,
                    state.Revision,
                    RepertoireResolutionReadSets.ForContribution(
                        context.Bindings,
                        state,
                        context.InputReferences
                    ),
                    references
                )
            )
            .ToList();
        if (context.Membership.UnmanagedBaseline is { } baseline)
        {
            contributions.Add(
                PrepareContribution(
                    baseline.Contributor,
                    1,
                    RepertoireResolutionReadSets.ForContribution(
                        context.Bindings,
                        baseline,
                        context.InputReferences
                    ),
                    references
                )
            );
        }
        contributions.Add(
            PrepareContribution(
                contributor,
                context
                    .Membership.Contributions.SingleOrDefault(state =>
                        state.Contributor == contributor
                    )
                    ?.Revision
                    ?? 0,
                own,
                references
            )
        );
        var revisions = context
            .Membership.Contributions.Select(state => new NormalizationInputContributionRevision(
                state.Contributor,
                state.Revision
            ))
            .Concat(
                context.Membership.UnmanagedBaseline is { } old
                    ? [new NormalizationInputContributionRevision(old.Contributor, 0)]
                    : []
            )
            .ToArray();
        // The complete membership CAS preserves peers and unmanaged baseline. Original source
        // stamps still supply our own facts; current peer stamps are coordination only.
        return await work.BeginContributedBatchAsync(
            workUuid,
            owner,
            version,
            contributions,
            context.Membership.Owner?.DesiredGeneration ?? 0,
            ct,
            revisions
        );
    }

    private static NormalizationPreparedInputContribution PrepareContribution(
        NormalizationResourceKey contributor,
        long revision,
        RepertoireResolutionReadSet read,
        IReadOnlyList<NormalizationBindingInputReference> references
    )
    {
        var ids = read.Inputs.Select(input => input.RevisionUuid).ToHashSet();
        var inputs = references
            .Where(reference => ids.Contains(reference.InputRevisionUuid))
            .Distinct()
            .ToArray();
        if (inputs.Length != ids.Count)
        {
            throw new NormalizationConflictException(
                "An enrolled peer lacks its exact retained input references."
            );
        }
        return new(
            new(
                contributor,
                revision,
                inputs.Select(input => input.Resource).ToArray(),
                read.Dependencies.Select(value => value.SubjectUuid).ToArray(),
                read.OwnerObservations.Select(value => value.SubjectUuid).ToArray()
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

    internal static bool AdmissionMatches(
        NormalizationWorkTicket ticket,
        RepertoireResolutionReadSet expected,
        MusicMaterializationOwnerContext original
    ) =>
        ticket.EditorialRevision == (original.Membership.Owner?.EditorialRevision ?? 0)
        && (
            original.Membership.Owner == null
            || ticket.SubjectUuid == original.Membership.Owner.SubjectUuid
        )
        && TicketMatches(ticket, expected);

    internal static bool TicketMatches(
        NormalizationWorkTicket ticket,
        RepertoireResolutionReadSet expected
    ) =>
        SameMembers(ticket.Inputs, expected.Inputs)
        && SameMembers(ticket.Dependencies, expected.Dependencies)
        && SameMembers(ticket.CatalogInputs ?? [], expected.CatalogInputs)
        && SameMembers(ticket.SourceIdentities ?? [], expected.SourceIdentities)
        && ticket.MappingRegistry == expected.MappingRegistry
        && SameMembers(ticket.ImportedVenues ?? [], expected.ImportedVenues)
        && SameMembers(ticket.CandidateUniverses ?? [], expected.CandidateUniverses)
        && SameMembers(ticket.OwnerObservations ?? [], expected.OwnerObservations)
        && SameMembers(ticket.SourceUuids ?? [], expected.SourceUuids);

    private static bool SameMembers<T>(IEnumerable<T> first, IEnumerable<T> second) =>
        first.ToHashSet().SetEquals(second);
}
