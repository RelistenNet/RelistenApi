using System;
using System.Linq;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>Shared complete-input contract for processing and proposal publication.</summary>
internal static class NormalizationRecordingReadSet
{
    internal static NormalizationRecordingPreparedInput[] Inputs(
        NormalizationRecordingPreparation preparation
    ) =>
        [
            new(
                preparation.Contributor
                    ?? new("relisten", "recording", preparation.Binding.Resource.ProviderKey),
                preparation.ExpectedContributionRevision,
                preparation.Binding,
                preparation.FetchUuid,
                preparation.ProducerCatalogInputs
            ),
            .. preparation.AdditionalInputs ?? [],
        ];

    internal static bool IsComplete(
        NormalizationWorkTicket ticket,
        NormalizationRecordingPreparation preparation
    )
    {
        var inputs = Inputs(preparation);
        var bindings = inputs.Select(input => input.Binding).ToArray();
        var registry = bindings
            .Where(binding => binding.MappingRegistry != null)
            .Select(binding => binding.MappingRegistry!)
            .Distinct()
            .ToArray();
        return ticket.Dependencies.Count == 0
            && (ticket.ImportedVenues?.Count ?? 0) == 0
            && (ticket.CandidateUniverses?.Count ?? 0) == 0
            && (ticket.OwnerObservations?.Count ?? 0) == 0
            && ticket
                .Inputs.Select(input => input.RevisionUuid)
                .Order()
                .SequenceEqual(
                    bindings
                        .SelectMany(binding => binding.Inputs)
                        .Select(input => input.InputRevisionUuid)
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
            && (ticket.SourceIdentities ?? [])
                .OrderBy(input => input.UpstreamIdentifier, StringComparer.Ordinal)
                .SequenceEqual(
                    bindings
                        .SelectMany(binding => binding.SourceIdentities)
                        .Distinct()
                        .OrderBy(input => input.UpstreamIdentifier, StringComparer.Ordinal)
                )
            && (ticket.SourceUuids ?? [])
                .OrderBy(input => input.SourceUuid)
                .SequenceEqual(
                    bindings
                        .SelectMany(binding => binding.SourceUuids ?? [])
                        .Distinct()
                        .OrderBy(input => input.SourceUuid)
                )
            && registry.Length <= 1
            && ticket.MappingRegistry == registry.SingleOrDefault();
    }
}
