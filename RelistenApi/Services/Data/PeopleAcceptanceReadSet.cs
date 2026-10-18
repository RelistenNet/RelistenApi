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
    ) => Contributions(facts, proposal, policy.OwnerObservation, membership, peopleDependencies);

    internal static NormalizationPreparedInputContribution[] Contributions(
        NormalizationPeopleResolutionPreparation facts,
        NormalizationSubjectObservation proposal,
        NormalizationSubjectObservation policyObservation,
        NormalizationInputContributionsSnapshot membership,
        IReadOnlyList<NormalizationSubjectObservation> peopleDependencies
    ) =>
        NormalizationPeopleResolutionReadSet
            .Contributions(facts)
            .Select(input =>
            {
                var observations = (input.ExpectedOwnerObservations ?? [])
                    .Append(proposal)
                    .Append(policyObservation)
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
        var ownContributors = stage
            .Facts.Inputs.Select(input => input.OriginalInput.Contributor)
            .ToHashSet();
        var peerInputs = inputs
            .Where(input => !ownContributors.Contains(input.Contribution.Contributor))
            .ToArray();
        var peerDependencyKeys = peerInputs
            .SelectMany(input => input.Contribution.DependencySubjectUuids)
            .ToHashSet();
        var peerRevisionUuids = peerInputs
            .SelectMany(input => input.ExpectedInputRevisions)
            .Select(input => input.InputRevisionUuid)
            .ToHashSet();
        var dependencyStamps = observations
            .Concat(
                (stage.PeerTicket?.Dependencies ?? [])
                    .Where(value => peerDependencyKeys.Contains(value.SubjectUuid))
                    .Select(value => new NormalizationSubjectObservation(
                        value.SubjectUuid,
                        value.DesiredGeneration,
                        value.EditorialRevision,
                        value.PublishedWorkUuid
                    ))
            )
            .Distinct()
            .ToArray();
        RequireCoherent(dependencyStamps, value => value.SubjectUuid);
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
            // Equal retained bytes can recur after a different observation. A peer's original
            // resource generation remains part of its proof even when its revision is current again.
            || stage.PeerTicket is { } peer
                && !Same(
                    ticket.Inputs.Where(input => peerRevisionUuids.Contains(input.RevisionUuid)),
                    peer.Inputs.Where(input => peerRevisionUuids.Contains(input.RevisionUuid))
                )
            || !Same(ticket.Dependencies.Select(input => input.SubjectUuid), expectedDependencies)
            || ticket.Dependencies.Any(dependency =>
                !dependencyStamps.Any(observation =>
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
            || !Same(
                ticket.ImportedVenues ?? [],
                inputs.SelectMany(input => input.ImportedVenues ?? [])
            )
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
        if (stage.DecisionOnly)
        {
            if (
                stage.PersonUuid != null
                || stage.Identity != null
                || stage.Reconciliation != null
                || stage.PolicyState == null
            )
            {
                throw new ArgumentException(
                    "A decision acknowledgment cannot contain accepted writes."
                );
            }
            return;
        }
        if (
            stage.Policy == null
            || stage.PersonUuid.HasValue == (stage.Identity == null)
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
