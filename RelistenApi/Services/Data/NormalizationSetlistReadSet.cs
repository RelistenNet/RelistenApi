using System;
using System.Linq;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>Every account's original binding contributes to the same complete proposal publication.</summary>
internal static class NormalizationSetlistReadSet
{
    internal static NormalizationPreparedInputContribution[] Contributions(
        NormalizationSetlistPreparation preparation
    ) =>
        preparation
            .Inputs.Select(input =>
            {
                var clocks = input
                    .Accounts.SelectMany(account => account.Binding.CatalogInputs)
                    .Distinct()
                    .ToArray();
                var registry = input
                    .Accounts.Select(account => account.Binding.MappingRegistry)
                    .Distinct()
                    .ToArray();
                if (
                    clocks.GroupBy(clock => clock.ArtistUuid).Any(group => group.Count() > 1)
                    || registry.Length != 1
                )
                {
                    throw new NormalizationConflictException(
                        "Setlist account bindings have incompatible original catalog observations."
                    );
                }
                return new NormalizationPreparedInputContribution(
                    new(
                        input.Contributor,
                        input.ExpectedContributionRevision,
                        [input.Input.Resource],
                        []
                    ),
                    [input.Input],
                    CatalogInputs: clocks,
                    MappingRegistry: registry[0]
                );
            })
            .ToArray();

    internal static bool IsComplete(
        NormalizationWorkTicket ticket,
        NormalizationSetlistPreparation preparation
    )
    {
        var bindings = preparation
            .Inputs.SelectMany(input => input.Accounts)
            .Select(account => account.Binding)
            .ToArray();
        var registry = bindings.Select(binding => binding.MappingRegistry).Distinct().ToArray();
        return ticket.Dependencies.Count == 0
            && (ticket.SourceIdentities?.Count ?? 0) == 0
            && (ticket.SourceUuids?.Count ?? 0) == 0
            && (ticket.ImportedVenues?.Count ?? 0) == 0
            && (ticket.CandidateUniverses?.Count ?? 0) == 0
            && (ticket.OwnerObservations?.Count ?? 0) == 0
            && ticket
                .Inputs.Select(input => input.RevisionUuid)
                .Order()
                .SequenceEqual(
                    preparation
                        .Inputs.Select(input => input.Input.InputRevisionUuid)
                        .Distinct()
                        .Order()
                )
            && (ticket.CatalogInputs ?? [])
                .OrderBy(input => input.ArtistUuid)
                .SequenceEqual(
                    bindings
                        .SelectMany(binding => binding.CatalogInputs)
                        .Distinct()
                        .OrderBy(input => input.ArtistUuid)
                )
            && registry.Length == 1
            && ticket.MappingRegistry == registry[0];
    }
}
