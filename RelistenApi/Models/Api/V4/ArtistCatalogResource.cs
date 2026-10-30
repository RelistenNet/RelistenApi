using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Relisten.Api.Models.Api.V4;

/// <summary>
/// Complete artist-owned collections and shared metadata patches. Show summaries
/// do not replace source/set/track collections or offline playback records.
/// </summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class ArtistCatalogResource
{
    public ArtistResource Artist { get; init; } = null!;
    public IReadOnlyList<YearResource> Years { get; init; } = [];
    public IReadOnlyList<ShowSummaryResource> Shows { get; init; } = [];
    public IReadOnlyList<TourResource> Tours { get; init; } = [];
    public IReadOnlyList<EraResource> Eras { get; init; } = [];
    public IReadOnlyList<ImportedVenueResource> ImportedVenues { get; init; } = [];
    public IReadOnlyList<ImportedSongResource> ImportedSongs { get; init; } = [];
    public IReadOnlyList<ArtistVenueEntryResource> VenueEntries { get; init; } = [];
    public IReadOnlyList<ArtistSongEntryResource> SongEntries { get; init; } = [];

    /// <summary>Entity patches include retained zero-count relationships; absence is not deletion.</summary>
    public IReadOnlyList<ArtistVenueResource> ArtistVenues { get; init; } = [];
    public IReadOnlyList<VenueResource> Venues { get; init; } = [];
    public IReadOnlyList<SongDetailResource> Songs { get; init; } = [];
}
