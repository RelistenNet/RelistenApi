using System;
using System.Collections.Generic;
using System.Linq;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>Original extraction and candidate observations become contributor-owned publication fences.</summary>
internal static class NormalizationPeopleResolutionReadSet
{
    internal static NormalizationPreparedInputContribution[] Contributions(
        NormalizationPeopleResolutionPreparation preparation
    ) =>
        preparation
            .Inputs.Select(input =>
            {
                var binding = input.OriginalInput.Binding;
                var facts = input
                    .Claims.Where(claim => claim.Candidates != null)
                    .Select(claim => claim.Candidates!.ReadSet)
                    .ToArray();
                var observations = facts
                    .SelectMany(read => read.OwnerObservations)
                    .Append(preparation.ExtractionObservation)
                    .Concat(preparation.CreditContext.IdentityOwnerObservations)
                    .Concat(
                        new[]
                        {
                            preparation.CreditContext.RecordingOwnerObservation,
                        }.OfType<NormalizationSubjectObservation>()
                    )
                    .Distinct()
                    .ToArray();
                var catalog = binding
                    .CatalogInputs.Concat(facts.SelectMany(read => read.CatalogInputs))
                    .Distinct()
                    .ToArray();
                var universes = new[] { preparation.CreditContext.PeopleUniverse }
                    .Concat(facts.SelectMany(read => read.CandidateUniverses))
                    .Distinct()
                    .ToArray();
                RequireCoherent(catalog, value => value.ArtistUuid);
                RequireCoherent(universes, value => value.Universe);
                RequireCoherent(observations, value => value.SubjectUuid);
                return new NormalizationPreparedInputContribution(
                    new(
                        input.OriginalInput.Contributor,
                        input.ExpectedContributionRevision,
                        binding.Inputs.Select(reference => reference.Resource).ToArray(),
                        [preparation.ExtractionObservation.SubjectUuid],
                        observations
                            .Select(observation => observation.SubjectUuid)
                            .Distinct()
                            .ToArray()
                    ),
                    binding.Inputs,
                    catalog,
                    binding.SourceIdentities,
                    binding.MappingRegistry,
                    CandidateUniverses: universes,
                    ExpectedOwnerObservations: observations,
                    SourceUuids: binding.SourceUuids
                );
            })
            .ToArray();

    internal static bool IsComplete(
        NormalizationWorkTicket ticket,
        NormalizationPeopleResolutionPreparation preparation
    )
    {
        var inputs = Contributions(preparation);
        return Same(
                ticket.Inputs.Select(input => input.RevisionUuid),
                inputs
                    .SelectMany(input => input.ExpectedInputRevisions)
                    .Select(input => input.InputRevisionUuid)
            )
            && Same(
                ticket.Dependencies.Select(dependency => dependency.SubjectUuid),
                [preparation.ExtractionObservation.SubjectUuid]
            )
            && Same(
                ticket.CatalogInputs ?? [],
                inputs.SelectMany(input => input.CatalogInputs ?? [])
            )
            && Same(
                ticket.SourceIdentities ?? [],
                inputs.SelectMany(input => input.SourceIdentities ?? [])
            )
            && Same(ticket.SourceUuids ?? [], inputs.SelectMany(input => input.SourceUuids ?? []))
            && Same(
                ticket.OwnerObservations ?? [],
                inputs.SelectMany(input => input.ExpectedOwnerObservations ?? [])
            )
            && Same(
                ticket.CandidateUniverses ?? [],
                inputs.SelectMany(input => input.CandidateUniverses ?? [])
            )
            && (ticket.ImportedVenues?.Count ?? 0) == 0
            && inputs
                .Select(input => input.MappingRegistry)
                .Where(registry => registry != null)
                .Distinct()
                .ToHashSet()
                .SetEquals(ticket.MappingRegistry == null ? [] : [ticket.MappingRegistry]);
    }

    private static void RequireCoherent<T, TKey>(IEnumerable<T> values, Func<T, TKey> key)
    {
        if (values.GroupBy(key).Any(group => group.Distinct().Count() > 1))
        {
            throw new NormalizationConflictException(
                "Resolution snapshots observed different original revisions; prepare the complete recording again."
            );
        }
    }

    private static bool Same<T>(IEnumerable<T> actual, IEnumerable<T> expected) =>
        actual.ToHashSet().SetEquals(expected);
}
