using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Relisten.Identifiers;

namespace Relisten.Api.Models.Api.V4;

/// <summary>Upsert-only resolver patches. Shallow parents omit unrequested child collections.</summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class CatalogResolverEntitiesResource
{
    public IReadOnlyList<ArtistResource> Artists { get; init; } = [];
    public IReadOnlyList<ShowSummaryResource> Shows { get; init; } = [];
    public IReadOnlyList<SourceSummaryResource> Sources { get; init; } = [];
    public IReadOnlyList<SourceSetSummaryResource> SourceSets { get; init; } = [];
    public IReadOnlyList<TrackResource> Tracks { get; init; } = [];
    public IReadOnlyList<YearResource> Years { get; init; } = [];
    public IReadOnlyList<TourResource> Tours { get; init; } = [];
    public IReadOnlyList<ImportedVenueResource> ImportedVenues { get; init; } = [];
    public IReadOnlyList<ImportedSongResource> ImportedSongs { get; init; } = [];
    public IReadOnlyList<VenueResource> Venues { get; init; } = [];
    public IReadOnlyList<SongDetailResource> Songs { get; init; } = [];
    public IReadOnlyList<ArtistVenueResource> ArtistVenues { get; init; } = [];
    public IReadOnlyList<SourceReviewResource> SourceReviews { get; init; } = [];
    public IReadOnlyList<SetlistShowResource> SetlistShows { get; init; } = [];
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class SetlistShowResource
{
    public SetlistShowId Id { get; init; }
    public ArtistId ArtistId { get; init; }
    public DateTime Date { get; init; }
    public ImportedVenueId ImportedVenueId { get; init; }
    public TourId? TourId { get; init; }
    public EraResource? Era { get; init; }
    public string UpstreamIdentifier { get; init; } = null!;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
