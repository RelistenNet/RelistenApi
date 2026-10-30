using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Relisten.Api.Models.Api.V4;

/// <summary>An artist's show group. Exactly one target is populated, as identified by kind.</summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record ArtistVenueEntryResource(
    VenueEntryKind Kind,
    int ShowCount,
    VenueResource? Venue,
    ImportedVenueResource? ImportedVenue
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
