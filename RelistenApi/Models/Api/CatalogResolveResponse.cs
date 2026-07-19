using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Api
{
    public sealed class CatalogResolveResponse
    {
        public int contract_version { get; init; }
        public DateTime checked_at { get; init; }
        public IReadOnlyList<ResolvedCatalogReference> references { get; init; } =
            Array.Empty<ResolvedCatalogReference>();
        public CatalogResolveEntities entities { get; init; } = new();
    }

    public sealed class ResolvedCatalogReference
    {
        public string catalog_type { get; init; } = string.Empty;
        public Guid catalog_uuid { get; init; }
        public string availability { get; init; } = string.Empty;
    }

    public sealed class CatalogResolveEntities
    {
        public IReadOnlyList<ResolvedArtist> artists { get; init; } = Array.Empty<ResolvedArtist>();
        public IReadOnlyList<ResolvedShow> shows { get; init; } = Array.Empty<ResolvedShow>();
        public IReadOnlyList<ResolvedSource> sources { get; init; } = Array.Empty<ResolvedSource>();
        public IReadOnlyList<ResolvedSourceTrack> source_tracks { get; init; } = Array.Empty<ResolvedSourceTrack>();
        public IReadOnlyList<ResolvedSong> songs { get; init; } = Array.Empty<ResolvedSong>();
        public IReadOnlyList<ResolvedTour> tours { get; init; } = Array.Empty<ResolvedTour>();
        public IReadOnlyList<ResolvedVenue> venues { get; init; } = Array.Empty<ResolvedVenue>();
    }
}
