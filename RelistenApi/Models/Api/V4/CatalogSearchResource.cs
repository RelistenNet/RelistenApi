using System.Collections.Generic;
using System.Linq;
using Relisten.Catalog;

namespace Relisten.Api.Models.Api.V4;

/// <summary>Independent, limited query results; no category establishes complete catalog membership.</summary>
public sealed class CatalogSearchResource
{
    public IReadOnlyList<ArtistResource> Artists { get; init; } = [];
    public IReadOnlyList<ShowSummaryResource> Shows { get; init; } = [];
    public IReadOnlyList<ArtistSongEntryResource> Songs { get; init; } = [];
    public IReadOnlyList<SourceSummaryResource> Sources { get; init; } = [];
    public IReadOnlyList<TourResource> Tours { get; init; } = [];
    public IReadOnlyList<ArtistVenueEntryResource> Venues { get; init; } = [];

    public static CatalogSearchResource From(CatalogSearchResults value) =>
        new()
        {
            Artists = value.Artists.Select(CatalogBrowseProjection.Artist).ToArray(),
            Shows = value.Shows.Select(V4CatalogProjection.ShowSummary).ToArray(),
            Songs = value.Songs.Select(V4CatalogProjection.SongEntry).ToArray(),
            Sources = value.Sources.Select(V4CatalogProjection.SourceSummary).ToArray(),
            Tours = value.Tours.Select(V4CatalogProjection.Tour).ToArray(),
            Venues = value.Venues.Select(V4CatalogProjection.VenueEntry).ToArray(),
        };
}
