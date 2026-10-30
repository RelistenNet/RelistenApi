using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Relisten.Identifiers;

namespace Relisten.Api.Models.Api.V4;

// Browse resources retain typed navigation identities. Collection results contain
// summaries; only detail resources claim ownership of recording hierarchies.
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public record ArtistResource
{
    public ArtistId Id { get; init; }
    public string Name { get; init; } = null!;
    public string Slug { get; init; } = null!;
    public string? SortName { get; init; }
    public string? MusicbrainzId { get; init; }

    /// <summary>Existing featured/auto-created flags, preserved as a bitmask.</summary>
    public int Featured { get; init; }
    public int ShowCount { get; init; }
    public int SourceCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public ArtistFeaturesResource Features { get; init; } = null!;
    public IReadOnlyList<ArtistProviderResource> UpstreamSources { get; init; } = [];
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class ArtistFeaturesResource
{
    public bool Descriptions { get; init; }
    public bool Eras { get; init; }
    public bool MultipleSources { get; init; }
    public bool Reviews { get; init; }
    public bool Ratings { get; init; }
    public bool Tours { get; init; }
    public bool TaperNotes { get; init; }
    public bool SourceInformation { get; init; }
    public bool Sets { get; init; }
    public bool PerShowVenues { get; init; }
    public bool PerSourceVenues { get; init; }
    public bool VenueCoords { get; init; }
    public bool Songs { get; init; }
    public bool Years { get; init; }
    public bool TrackMd5s { get; init; }
    public bool ReviewTitles { get; init; }
    public bool JamCharts { get; init; }
    public bool SetlistDataIncomplete { get; init; }
    public bool TrackNames { get; init; }
    public bool VenuePastNames { get; init; }
    public bool ReviewsHaveRatings { get; init; }
    public bool TrackDurations { get; init; }
    public bool CanHaveFlac { get; init; }

    /// <summary>Whether approved normalized interpretations may appear in this artist's reads.</summary>
    public bool Normalization { get; init; }
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class ArtistProviderResource
{
    public string Provider { get; init; } = null!;
    public string? UpstreamIdentifier { get; init; }
    public string? Url { get; init; }
    public string? Description { get; init; }
    public string? CreditLine { get; init; }
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record YearWithShowsResource : YearResource
{
    /// <summary>The complete year collection; summaries do not claim source/track ownership.</summary>
    public IReadOnlyList<ShowSummaryResource> Shows { get; init; } = [];
}
