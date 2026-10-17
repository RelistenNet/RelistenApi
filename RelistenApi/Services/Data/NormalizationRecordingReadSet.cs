using System;
using System.Linq;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Defines the complete read set consumed by the single-recording extraction format.
/// A contributor union may contain additional facts; neither processing nor the proposal
/// store may acknowledge those facts until this format can actually consume them.
/// </summary>
internal static class NormalizationRecordingReadSet
{
    internal static bool IsComplete(
        NormalizationWorkTicket ticket,
        NormalizationRecordingPreparation preparation
    ) =>
        preparation.Binding.Inputs.Count == 1
        && ticket.Inputs.Count == 1
        && ticket.Inputs[0].RevisionUuid == preparation.Binding.InputRevisionUuid
        && ticket.Dependencies.Count == 0
        // The current format consumes one legacy recording snapshot and its exact clocks.
        // Other contributors can require facts this format cannot acknowledge.
        && (ticket.ImportedVenues?.Count ?? 0) == 0
        && (ticket.CandidateUniverses?.Count ?? 0) == 0
        && (ticket.OwnerObservations?.Count ?? 0) == 0
        && (ticket.CatalogInputs ?? [])
            .OrderBy(input => input.ArtistUuid)
            .SequenceEqual(preparation.Binding.CatalogInputs.OrderBy(input => input.ArtistUuid))
        && (ticket.SourceIdentities ?? [])
            .OrderBy(input => input.UpstreamIdentifier, StringComparer.Ordinal)
            .SequenceEqual(
                preparation.Binding.SourceIdentities.OrderBy(
                    input => input.UpstreamIdentifier,
                    StringComparer.Ordinal
                )
            )
        && (ticket.SourceUuids ?? [])
            .OrderBy(input => input.SourceUuid)
            .SequenceEqual(
                (preparation.Binding.SourceUuids ?? []).OrderBy(input => input.SourceUuid)
            )
        && ticket.MappingRegistry == preparation.Binding.MappingRegistry;
}
