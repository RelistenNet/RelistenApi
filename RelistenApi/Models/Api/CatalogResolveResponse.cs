using System;
using System.Collections.Generic;
using Relisten.Api.Models;

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

        // This describes whether the resolver returned a complete mobile catalog graph. Favorite
        // membership remains valid when a row is unavailable or temporarily cannot be hydrated.
        public string availability { get; init; } = string.Empty;
    }

    public sealed class CatalogResolveEntities
    {
        public IReadOnlyList<ArtistWithCounts> artists { get; init; } = Array.Empty<ArtistWithCounts>();
        public IReadOnlyList<Show> shows { get; init; } = Array.Empty<Show>();
        public IReadOnlyList<SourceFull> sources { get; init; } = Array.Empty<SourceFull>();
        public IReadOnlyList<SourceTrack> source_tracks { get; init; } = Array.Empty<SourceTrack>();
        public IReadOnlyList<SetlistSongWithPlayCount> songs { get; init; } =
            Array.Empty<SetlistSongWithPlayCount>();
        public IReadOnlyList<TourWithShowCount> tours { get; init; } = Array.Empty<TourWithShowCount>();
        public IReadOnlyList<VenueWithShowCount> venues { get; init; } =
            Array.Empty<VenueWithShowCount>();
        public IReadOnlyList<Year> years { get; init; } = Array.Empty<Year>();
        public IReadOnlyList<SourceSet> source_sets { get; init; } = Array.Empty<SourceSet>();
    }
}
