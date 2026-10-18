using System;
using System.Collections.Generic;
using System.Linq;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>Exact original observations are combined by key, never by choosing a newer stamp.</summary>
internal static class RepertoireResolutionReadSets
{
    internal static RepertoireResolutionReadSet Empty => new([], [], [], [], null, [], [], [], []);

    internal static RepertoireResolutionReadSet Merge(params RepertoireResolutionReadSet[] sets)
    {
        var mappings = sets.Select(x => x.MappingRegistry)
            .OfType<NormalizationMappingRegistryDependency>()
            .Distinct()
            .ToArray();
        if (mappings.Length > 1)
        {
            throw new NormalizationConflictException(
                "Conflicting original mapping registry observations."
            );
        }
        return new(
            Combine(sets.SelectMany(x => x.Inputs), x => x.ResourceUuid),
            Combine(sets.SelectMany(x => x.Dependencies), x => x.SubjectUuid),
            Combine(sets.SelectMany(x => x.CatalogInputs), x => x.ArtistUuid),
            Combine(sets.SelectMany(x => x.SourceIdentities), x => x.UpstreamIdentifier),
            mappings.SingleOrDefault(),
            Combine(sets.SelectMany(x => x.ImportedVenues), x => x.VenueUuid),
            Combine(sets.SelectMany(x => x.CandidateUniverses), x => x.Universe),
            Combine(sets.SelectMany(x => x.OwnerObservations), x => x.SubjectUuid),
            Combine(sets.SelectMany(x => x.SourceUuids), x => x.SourceUuid)
        );
    }

    internal static RepertoireResolutionReadSet ForContribution(
        RepertoireResolutionReadSet read,
        NormalizationInputContributionState state,
        IReadOnlyList<NormalizationBindingInputReference> references
    )
    {
        var resources = state.Resources.ToHashSet();
        var revisions = references
            .Where(x => resources.Contains(x.Resource))
            .Select(x => x.InputRevisionUuid)
            .ToHashSet();
        return new(
            read.Inputs.Where(x => revisions.Contains(x.RevisionUuid)).ToArray(),
            read.Dependencies.Where(x => state.DependencySubjectUuids.Contains(x.SubjectUuid))
                .ToArray(),
            read.CatalogInputs.Where(x => state.CatalogArtistUuids.Contains(x.ArtistUuid))
                .ToArray(),
            read.SourceIdentities.Where(x => state.SourceIdentifiers.Contains(x.UpstreamIdentifier))
                .ToArray(),
            state.UsesMappingRegistry ? read.MappingRegistry : null,
            read.ImportedVenues.Where(x => (state.ImportedVenueUuids ?? []).Contains(x.VenueUuid))
                .ToArray(),
            read.CandidateUniverses.Where(x =>
                    (state.CandidateUniverses ?? []).Contains(x.Universe)
                )
                .ToArray(),
            read.OwnerObservations.Where(x =>
                    (state.OwnerObservationSubjectUuids ?? []).Contains(x.SubjectUuid)
                )
                .ToArray(),
            read.SourceUuids.Where(x => (state.SourceUuids ?? []).Contains(x.SourceUuid)).ToArray()
        );
    }

    private static T[] Combine<T, TKey>(IEnumerable<T> items, Func<T, TKey> key)
        where TKey : notnull
    {
        var result = new Dictionary<TKey, T>();
        foreach (var item in items)
        {
            var itemKey = key(item);
            if (
                result.TryGetValue(itemKey, out var previous)
                && !EqualityComparer<T>.Default.Equals(previous, item)
            )
            {
                throw new NormalizationConflictException(
                    "Conflicting original repertoire read-set observations."
                );
            }
            result[itemKey] = item;
        }
        return result.Values.ToArray();
    }
}
