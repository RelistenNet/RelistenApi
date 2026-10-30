using System.Collections.Generic;
using Newtonsoft.Json;
using Relisten.Identifiers;

namespace Relisten.Api.Models.Api.V4;

/// <summary>Playback windows and existing scores; selection order is represented by rank.</summary>
public sealed record RankingMetricsResource(
    double MomentumScore,
    double TrendRatio,
    RankingWindowsResource Windows
);

public sealed record RankingWindowResource(long Plays, double Hours, double HotScore);

public sealed record RankingWindowsResource(
    [property: JsonProperty("48h")] RankingWindowResource Hours48,
    [property: JsonProperty("7d")] RankingWindowResource Days7,
    [property: JsonProperty("30d")] RankingWindowResource Days30
);

public sealed record RankedArtistResource : ArtistResource
{
    public int Rank { get; init; }
    public RankingMetricsResource Popularity { get; init; } = null!;
}

public sealed record RankedYearResource : YearResource
{
    public int Rank { get; init; }
    public RankingMetricsResource Popularity { get; init; } = null!;
}

public sealed record RankedShowResource : ShowSummaryResource
{
    public int Rank { get; init; }
    public RankingMetricsResource Popularity { get; init; } = null!;
}

/// <summary>One artist's independently limited popular and trending lists.</summary>
public sealed record ArtistRankingResource(
    ArtistId ArtistId,
    IReadOnlyList<RankedShowResource> PopularShows,
    IReadOnlyList<RankedShowResource> TrendingShows
);
