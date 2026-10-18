using System;
using System.Collections.Generic;
using System.Linq;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Carries the original extraction proof and each input's actual candidate reads into Work.
/// Candidate and policy owners are observations; only original producer dependencies and the
/// extraction owner are causal dependencies. No generation is refreshed from a current head.
/// </summary>
internal static class SourceActorResolutionReadSet
{
    internal static NormalizationPreparedInputContribution[] Contributions(
        SourceActorResolutionPreparation preparation
    )
    {
        RequireOriginalExtraction(preparation);

        var contributions = preparation
            .Inputs.Select(
                (input, index) =>
                {
                    var binding = input.OriginalInput.Binding;
                    var inherited = index == 0 ? preparation.ExtractionTicket : null;
                    var facts = input
                        .Scopes.SelectMany(scope => scope.Claims)
                        .Where(claim => claim.CandidatePoolIndex.HasValue)
                        .Select(claim => claim.CandidatePoolIndex!.Value)
                        .Distinct()
                        .Select(pool => preparation.CandidatePools[pool].ReadSet)
                        .ToArray();
                    var observations = Coherent(
                        facts
                            .SelectMany(read => read.OwnerObservations)
                            .Append(preparation.ExtractionObservation)
                            .Append(preparation.PolicyState.OwnerObservation)
                            .Concat(
                                new[]
                                {
                                    preparation.SourceReviews.OwnerObservation,
                                }.OfType<NormalizationSubjectObservation>()
                            )
                            .Concat(inherited?.OwnerObservations ?? [])
                            .Concat((inherited?.Dependencies ?? []).Select(Observe))
                            .Concat(
                                index == 0
                                    ? (preparation.SourceReviews.Correspondences ?? []).SelectMany(
                                        proof => proof.Target.OwnerObservations
                                    )
                                    : []
                            ),
                        value => value.SubjectUuid
                    );
                    var catalog = Coherent(
                        binding
                            .CatalogInputs.Concat(facts.SelectMany(read => read.CatalogInputs))
                            .Concat(inherited?.CatalogInputs ?? []),
                        value => value.ArtistUuid
                    );
                    var sources = Coherent(
                        binding.SourceIdentities.Concat(inherited?.SourceIdentities ?? []),
                        value => value.UpstreamIdentifier
                    );
                    var sourceUuids = Coherent(
                        (binding.SourceUuids ?? []).Concat(inherited?.SourceUuids ?? []),
                        value => value.SourceUuid
                    );
                    var universes = Coherent(
                        new[] { preparation.PeopleUniverse }
                            .Concat(facts.SelectMany(read => read.CandidateUniverses))
                            .Concat(inherited?.CandidateUniverses ?? []),
                        value => value.Universe
                    );
                    var registries = Coherent(
                        new[]
                        {
                            binding.MappingRegistry,
                            inherited?.MappingRegistry,
                        }.OfType<NormalizationMappingRegistryDependency>(),
                        _ => 0
                    );
                    var dependencies = (inherited?.Dependencies ?? [])
                        .Select(dependency => dependency.SubjectUuid)
                        .Append(preparation.ExtractionObservation.SubjectUuid)
                        .Distinct()
                        .ToArray();

                    return new NormalizationPreparedInputContribution(
                        new(
                            input.OriginalInput.Contributor,
                            input.ExpectedContributionRevision,
                            binding
                                .Inputs.Select(reference => reference.Resource)
                                .Distinct()
                                .ToArray(),
                            dependencies,
                            observations.Select(observation => observation.SubjectUuid).ToArray()
                        ),
                        // Keep the historical binding untouched. Admission consumes the exact
                        // resource generation from its original published extraction ticket.
                        binding
                            .Inputs.Select(reference =>
                                reference with
                                {
                                    Generation = preparation
                                        .ExtractionTicket.Inputs.Single(input =>
                                            input.RevisionUuid == reference.InputRevisionUuid
                                        )
                                        .Generation,
                                }
                            )
                            .ToArray(),
                        catalog,
                        sources,
                        registries.SingleOrDefault(),
                        ImportedVenues: inherited?.ImportedVenues,
                        CandidateUniverses: universes,
                        ExpectedOwnerObservations: observations,
                        SourceUuids: sourceUuids
                    );
                }
            )
            .ToArray();

        RequireCoherentUnion(contributions);
        return contributions;
    }

    internal static bool IsComplete(
        NormalizationWorkTicket ticket,
        SourceActorResolutionPreparation preparation
    )
    {
        var inputs = Contributions(preparation);
        var dependencies = preparation.ExtractionTicket.Dependencies.Append(
            new(
                preparation.ExtractionObservation.SubjectUuid,
                preparation.ExtractionObservation.DesiredGeneration,
                preparation.ExtractionObservation.EditorialRevision,
                preparation.ExtractionObservation.PublishedWorkUuid
            )
        );
        return Same(ticket.Inputs, preparation.ExtractionTicket.Inputs)
            && Same(ticket.Dependencies, dependencies)
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
                ticket.ImportedVenues ?? [],
                inputs.SelectMany(input => input.ImportedVenues ?? [])
            )
            && Same(
                ticket.CandidateUniverses ?? [],
                inputs.SelectMany(input => input.CandidateUniverses ?? [])
            )
            && Same(
                ticket.OwnerObservations ?? [],
                inputs.SelectMany(input => input.ExpectedOwnerObservations ?? [])
            )
            && Same(
                ticket.MappingRegistry == null ? [] : new[] { ticket.MappingRegistry },
                inputs
                    .Select(input => input.MappingRegistry)
                    .OfType<NormalizationMappingRegistryDependency>()
            );
    }

    /// <summary>
    /// Bounds the proposed complete membership, including retained empty contributor headers.
    /// The service has already refused unrepresented contributors with nonempty bindings;
    /// those retained headers therefore contribute zero bindings, but still consume a slot.
    /// </summary>
    internal static void RequireCapacity(
        SourceActorResolutionPreparation preparation,
        IReadOnlyList<NormalizationPreparedInputContribution> contributions
    )
    {
        var limits = PeopleResolutionCapacity.ContributionMembership;
        var contributorCount = preparation
            .ExpectedContributions.Select(value => value.Contributor)
            .Concat(contributions.Select(value => value.Contribution.Contributor))
            .Distinct()
            .Count();
        var bindingCount = contributions
            .GroupBy(value => value.Contribution.Contributor)
            .Sum(CountBindings);
        if (contributorCount > limits.MaximumContributors || bindingCount > limits.MaximumBindings)
        {
            throw new SourceActorResolutionCapacityException("contribution_capacity_exceeded");
        }
    }

    private static long CountBindings(
        IGrouping<NormalizationResourceKey, NormalizationPreparedInputContribution> contributions
    ) =>
        (long)contributions.SelectMany(value => value.Contribution.Resources).Distinct().Count()
        + contributions
            .SelectMany(value => value.Contribution.DependencySubjectUuids)
            .Distinct()
            .Count()
        + contributions
            .SelectMany(value => value.CatalogInputs ?? [])
            .Select(value => value.ArtistUuid)
            .Distinct()
            .Count()
        + contributions
            .SelectMany(value => value.SourceIdentities ?? [])
            .Select(value => value.UpstreamIdentifier)
            .Distinct(StringComparer.Ordinal)
            .Count()
        + (contributions.Any(value => value.MappingRegistry != null) ? 1 : 0)
        + contributions
            .SelectMany(value => value.ImportedVenues ?? [])
            .Select(value => value.VenueUuid)
            .Distinct()
            .Count()
        + contributions
            .SelectMany(value => value.CandidateUniverses ?? [])
            .Select(value => value.Universe)
            .Distinct()
            .Count()
        + contributions
            .SelectMany(value => value.Contribution.OwnerObservationSubjectUuids ?? [])
            .Distinct()
            .Count()
        + contributions
            .SelectMany(value => value.SourceUuids ?? [])
            .Select(value => value.SourceUuid)
            .Distinct()
            .Count();

    private static void RequireOriginalExtraction(SourceActorResolutionPreparation preparation)
    {
        var ticket = preparation.ExtractionTicket;
        var observation = new NormalizationSubjectObservation(
            ticket.SubjectUuid,
            ticket.DesiredGeneration,
            ticket.EditorialRevision,
            ticket.WorkUuid
        );
        if (
            preparation.Inputs.Count == 0
            || preparation.ExtractionObservation != observation
            || !Same(
                ticket.Inputs.Select(input => input.RevisionUuid),
                preparation
                    .Inputs.SelectMany(input => input.OriginalInput.Binding.Inputs)
                    .Select(input => input.InputRevisionUuid)
            )
        )
        {
            throw new NormalizationConflictException(
                "Source resolution requires its complete original extraction ticket and inputs."
            );
        }

        Coherent(ticket.Inputs, value => value.ResourceUuid);
        Coherent(ticket.Dependencies, value => value.SubjectUuid);
        Coherent(
            ticket.Dependencies.Select(Observe).Concat(ticket.OwnerObservations ?? []),
            value => value.SubjectUuid
        );
    }

    private static void RequireCoherentUnion(
        IReadOnlyList<NormalizationPreparedInputContribution> inputs
    )
    {
        Coherent(inputs.SelectMany(input => input.ExpectedInputRevisions), value => value.Resource);
        Coherent(inputs.SelectMany(input => input.CatalogInputs ?? []), value => value.ArtistUuid);
        Coherent(
            inputs.SelectMany(input => input.SourceIdentities ?? []),
            value => value.UpstreamIdentifier
        );
        Coherent(inputs.SelectMany(input => input.SourceUuids ?? []), value => value.SourceUuid);
        Coherent(inputs.SelectMany(input => input.ImportedVenues ?? []), value => value.VenueUuid);
        Coherent(
            inputs.SelectMany(input => input.CandidateUniverses ?? []),
            value => value.Universe
        );
        Coherent(
            inputs.SelectMany(input => input.ExpectedOwnerObservations ?? []),
            value => value.SubjectUuid
        );
        Coherent(
            inputs
                .Select(input => input.MappingRegistry)
                .OfType<NormalizationMappingRegistryDependency>(),
            _ => 0
        );
    }

    private static NormalizationSubjectObservation Observe(
        NormalizationSubjectDependency dependency
    ) =>
        new(
            dependency.SubjectUuid,
            dependency.DesiredGeneration,
            dependency.EditorialRevision,
            dependency.PublishedWorkUuid
        );

    private static T[] Coherent<T, TKey>(IEnumerable<T> values, Func<T, TKey> key)
    {
        var distinct = values.Distinct().ToArray();
        if (distinct.GroupBy(key).Any(group => group.Count() > 1))
        {
            throw new NormalizationConflictException(
                "Source resolution snapshots contain different original observations for the same key."
            );
        }
        return distinct;
    }

    private static bool Same<T>(IEnumerable<T> actual, IEnumerable<T> expected) =>
        actual.ToHashSet().SetEquals(expected);
}
