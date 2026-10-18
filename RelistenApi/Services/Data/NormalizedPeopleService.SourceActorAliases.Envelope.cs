using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedPeopleService
{
    private static async Task ValidateSourceAliasEnvelope(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasPersonStage stage,
        CancellationToken ct
    )
    {
        var ticket = stage.Ticket!;
        var read = stage.Facts.CurrentReadSet;
        var own = stage.Contributions.SingleOrDefault(value =>
            value.Contribution.Contributor == SourceActorAliasApplicationStore.StableContributor
        );
        var expectedMembers = stage
            .Facts.Membership.Contributions.Concat(
                stage.Facts.Membership.UnmanagedBaseline == null
                    ? []
                    : new[] { stage.Facts.Membership.UnmanagedBaseline }
            )
            .Select(value => value.Contributor)
            .Append(SourceActorAliasApplicationStore.StableContributor)
            .ToHashSet();
        if (
            own == null
            || stage.Contributions.Count != expectedMembers.Count
            || !expectedMembers.SetEquals(
                stage.Contributions.Select(value => value.Contribution.Contributor)
            )
            || !(read.Inputs ?? []).ToHashSet().SetEquals(ticket.Inputs)
            || !SourceAliasSetEqual(read.CatalogInputs, ticket.CatalogInputs)
            || !SourceAliasSetEqual(read.SourceIdentities, ticket.SourceIdentities)
            || read.MappingRegistry != ticket.MappingRegistry
            || !SourceAliasSetEqual(read.ImportedVenues, ticket.ImportedVenues)
            || !SourceAliasSetEqual(read.CandidateUniverses, ticket.CandidateUniverses)
            || !SourceAliasSetEqual(read.OwnerObservations, ticket.OwnerObservations)
            || !SourceAliasSetEqual(read.SourceUuids, ticket.SourceUuids)
        )
        {
            throw new ArgumentException(
                "The admitted person ticket must retain the complete prepared factual envelope and every configured producer."
            );
        }
        var rows = (
            await c.QueryAsync<SourceAliasResourceKey>(
                new CommandDefinition(
                    """
                    SELECT uuid AS "Uuid",provider AS "Provider",resource_type AS "ResourceType",provider_key AS "ProviderKey"
                    FROM normalization.input_resources WHERE uuid=ANY(@ids)
                    """,
                    new { ids = ticket.Inputs.Select(value => value.ResourceUuid).ToArray() },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToDictionary(
            value => new NormalizationResourceKey(
                value.Provider,
                value.ResourceType,
                value.ProviderKey
            ),
            value => value.Uuid
        );
        foreach (var contribution in stage.Contributions)
        {
            if (
                !contribution
                    .ExpectedInputRevisions.Select(value => value.Resource)
                    .ToHashSet()
                    .SetEquals(contribution.Contribution.Resources)
                || contribution.ExpectedInputRevisions.Any(reference =>
                    reference.Generation == null
                    || !rows.TryGetValue(reference.Resource, out var resource)
                    || !ticket.Inputs.Contains(
                        new(resource, reference.InputRevisionUuid, reference.Generation.Value)
                    )
                )
            )
            {
                throw new ArgumentException(
                    "Every new prepared peer input requires the exact current revision and generation read in the stage snapshot."
                );
            }
        }
        var dependencyKeys = stage
            .Contributions.SelectMany(value => value.Contribution.DependencySubjectUuids)
            .ToHashSet();
        var expectedDependencies = (read.OwnerObservations ?? [])
            .Where(value => dependencyKeys.Contains(value.SubjectUuid))
            .Select(value => new NormalizationSubjectDependency(
                value.SubjectUuid,
                value.DesiredGeneration,
                value.EditorialRevision,
                value.PublishedWorkUuid
            ));
        if (
            dependencyKeys.Count != expectedDependencies.Count()
            || !ticket.Dependencies.ToHashSet().SetEquals(expectedDependencies)
        )
        {
            throw new ArgumentException(
                "Every causal peer key requires its original complete current owner observation."
            );
        }
    }

    private static bool SourceAliasSetEqual<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right) =>
        (left ?? []).ToHashSet().SetEquals(right ?? []);

    private sealed record SourceAliasResourceKey(
        Guid Uuid,
        string Provider,
        string ResourceType,
        string ProviderKey
    );
}
