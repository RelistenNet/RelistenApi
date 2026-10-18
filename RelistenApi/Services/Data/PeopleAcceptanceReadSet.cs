using System;
using System.Collections.Generic;
using System.Linq;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>Accepted stages retain original provider proofs and replace only freshly reread derived people facts.</summary>
internal static class PeopleAcceptanceReadSet
{
    internal static NormalizationPreparedInputContribution[] Contributions(
        NormalizationPeopleResolutionPreparation facts,
        NormalizationSubjectObservation proposal,
        ActivePeopleAcceptancePolicy policy,
        NormalizationInputContributionsSnapshot membership,
        IReadOnlyList<NormalizationSubjectObservation> peopleDependencies
    ) =>
        NormalizationPeopleResolutionReadSet
            .Contributions(facts)
            .Select(input =>
            {
                var observations = (input.ExpectedOwnerObservations ?? [])
                    .Append(proposal)
                    .Append(policy.OwnerObservation)
                    .Concat(peopleDependencies)
                    .Distinct()
                    .ToArray();
                RequireCoherent(observations, value => value.SubjectUuid);
                var dependencies = input
                    .Contribution.DependencySubjectUuids.Append(proposal.SubjectUuid)
                    .Concat(peopleDependencies.Select(value => value.SubjectUuid))
                    .Distinct()
                    .ToArray();
                var revision =
                    membership
                        .Contributions.SingleOrDefault(value =>
                            value.Contributor == input.Contribution.Contributor
                        )
                        ?.Revision
                    ?? 0;
                return input with
                {
                    Contribution = input.Contribution with
                    {
                        ExpectedRevision = revision,
                        DependencySubjectUuids = dependencies,
                        OwnerObservationSubjectUuids = observations
                            .Select(value => value.SubjectUuid)
                            .ToArray(),
                    },
                    ExpectedOwnerObservations = observations,
                };
            })
            .ToArray();

    internal static void RequireTicket(
        PeopleAcceptanceStage stage,
        NormalizationWorkTicket ticket,
        Guid sourceUuid,
        string version
    )
    {
        var inputs = stage.Contributions;
        var expectedDependencies = inputs
            .SelectMany(input => input.Contribution.DependencySubjectUuids)
            .ToHashSet();
        var observations = inputs
            .SelectMany(input => input.ExpectedOwnerObservations ?? [])
            .Distinct()
            .ToArray();
        var expectedInputs = inputs
            .SelectMany(input => input.ExpectedInputRevisions)
            .Distinct()
            .ToArray();
        if (
            ticket.WorkUuid != stage.WorkUuid
            || ticket.PolicyVersion != version
            || !Same(
                ticket.Inputs.Select(input => input.RevisionUuid),
                expectedInputs.Select(input => input.InputRevisionUuid)
            )
            || !Same(ticket.Dependencies.Select(input => input.SubjectUuid), expectedDependencies)
            || ticket.Dependencies.Any(dependency =>
                !observations.Any(observation =>
                    observation.SubjectUuid == dependency.SubjectUuid
                    && observation.DesiredGeneration == dependency.DesiredGeneration
                    && observation.EditorialRevision == dependency.EditorialRevision
                    && observation.PublishedWorkUuid == dependency.PublishedWorkUuid
                )
            )
            || !Same(ticket.OwnerObservations ?? [], observations)
            || !Same(
                ticket.CatalogInputs ?? [],
                inputs.SelectMany(input => input.CatalogInputs ?? [])
            )
            || !Same(
                ticket.SourceIdentities ?? [],
                inputs.SelectMany(input => input.SourceIdentities ?? [])
            )
            || !Same(ticket.SourceUuids ?? [], inputs.SelectMany(input => input.SourceUuids ?? []))
            || !Same(
                ticket.CandidateUniverses ?? [],
                inputs.SelectMany(input => input.CandidateUniverses ?? [])
            )
            || (ticket.ImportedVenues?.Count ?? 0) != 0
            || !Same(
                inputs
                    .Select(input => input.MappingRegistry)
                    .OfType<NormalizationMappingRegistryDependency>(),
                ticket.MappingRegistry == null ? [] : [ticket.MappingRegistry]
            )
        )
        {
            throw new NormalizationConflictException(
                "Accepted resolution must consume its complete saved stage read set."
            );
        }
        if (
            stage.PersonUuid.HasValue == (stage.Identity == null)
            || stage.PersonUuid.HasValue == (stage.Reconciliation != null)
            || stage.Reconciliation != null && stage.Reconciliation.SourceUuid != sourceUuid
        )
        {
            throw new ArgumentException(
                "An accepted stage has exactly one identity or recording owner."
            );
        }
    }

    private static bool Same<T>(IEnumerable<T> left, IEnumerable<T> right) =>
        left.ToHashSet().SetEquals(right);

    private static void RequireCoherent<T, TKey>(IEnumerable<T> values, Func<T, TKey> key)
    {
        if (values.GroupBy(key).Any(group => group.Distinct().Count() > 1))
        {
            throw new NormalizationConflictException(
                "Accepted resolution requires one original observation per owner."
            );
        }
    }
}
