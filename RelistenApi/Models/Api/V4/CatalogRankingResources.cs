using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Relisten.Identifiers;

namespace Relisten.Api.Models.Api.V4;

public sealed record RankedArtistResource : ArtistResource
{
    public int Rank { get; init; }
    public PopularityResource Popularity { get; init; } = null!;
}

public sealed record RankedYearResource : YearResource
{
    public int Rank { get; init; }
    public PopularityResource Popularity { get; init; } = null!;
}

public sealed record RankedShowResource : ShowSummaryResource
{
    public int Rank { get; init; }
}

/// <summary>One artist's independently limited popular and trending lists.</summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record ArtistRankingResource(
    ArtistId ArtistId,
    IReadOnlyList<RankedShowResource> PopularShows,
    IReadOnlyList<RankedShowResource> TrendingShows
);
