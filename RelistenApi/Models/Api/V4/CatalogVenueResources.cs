using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Relisten.Identifiers;
using Relisten.Normalization;

namespace Relisten.Api.Models.Api.V4;

/// <summary>Stable artist/place membership. Its count spans all of the artist's years and recordings.</summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class ArtistVenueResource
{
    public ArtistVenueId Id { get; init; }
    public ArtistId ArtistId { get; init; }
    public int ArtistShowCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public VenueResource Venue { get; init; } = null!;
}

/// <summary>A browse target: a stored artist/place relationship or an imported fallback group.</summary>
/// <param name="FallbackShowCount">Show membership of this fallback group, independent of the imported venue's full collection.</param>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record ArtistVenueEntryResource(
    VenueEntryKind Kind,
    ArtistVenueResource? ArtistVenue,
    ImportedVenueResource? ImportedVenue,
    int? FallbackShowCount
);

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public record ImportedVenueWithShowCountResource : ImportedVenueResource
{
    /// <summary>Distinct catalog shows whose show-level venue is this imported row.</summary>
    public int ShowCount { get; init; }
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record ImportedVenueWithShowsResource : ImportedVenueWithShowCountResource
{
    /// <summary>The complete artist-owned show collection for this imported row.</summary>
    public IReadOnlyList<ShowSummaryResource> Shows { get; init; } = [];
}
