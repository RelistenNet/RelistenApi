using System;
using System.Collections.Generic;
using System.Linq;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>The lookup producer may acknowledge only the exact facts saved in its preparation.</summary>
internal static class NormalizationRecordingLookupReadSet
{
    internal static bool IsComplete(
        NormalizationWorkTicket ticket,
        NormalizationRecordingLookupPreparation preparation
    )
    {
        var binding = preparation.Binding;
        IReadOnlyList<NormalizationSourceUuidDependency> sourceUuids =
            binding?.SourceUuids
            ?? (
                preparation.AbsenceProbe == null
                    ? []
                    : new[] { preparation.AbsenceProbe.Dependency }
            );
        return ticket.Dependencies.Count == 0
            && (ticket.ImportedVenues?.Count ?? 0) == 0
            && (ticket.CandidateUniverses?.Count ?? 0) == 0
            && (ticket.OwnerObservations?.Count ?? 0) == 0
            && ticket
                .Inputs.Select(input => input.RevisionUuid)
                .Order()
                .SequenceEqual(
                    (binding?.Inputs ?? []).Select(input => input.InputRevisionUuid).Order()
                )
            && (ticket.CatalogInputs ?? [])
                .OrderBy(input => input.ArtistUuid)
                .SequenceEqual((binding?.CatalogInputs ?? []).OrderBy(input => input.ArtistUuid))
            && (ticket.SourceIdentities ?? [])
                .OrderBy(input => input.UpstreamIdentifier, StringComparer.Ordinal)
                .SequenceEqual(
                    (binding?.SourceIdentities ?? []).OrderBy(
                        input => input.UpstreamIdentifier,
                        StringComparer.Ordinal
                    )
                )
            && (ticket.SourceUuids ?? [])
                .OrderBy(input => input.SourceUuid)
                .SequenceEqual(sourceUuids.OrderBy(input => input.SourceUuid))
            && ticket.MappingRegistry == binding?.MappingRegistry;
    }
}
