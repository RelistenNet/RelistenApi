using Newtonsoft.Json;

namespace Relisten.Api.Models.Api.V4;

/// <summary>
/// Listening windows and scores in the endpoint's context: artist-local for
/// ordinary show reads, global for global rankings, organic for momentum.
/// </summary>
public sealed record PopularityResource(
    double MomentumScore,
    double TrendRatio,
    PopularityWindowsResource Windows
);

public sealed record PopularityWindowResource(long Plays, double Hours, double HotScore);

public sealed record PopularityWindowsResource(
    [property: JsonProperty("48h")] PopularityWindowResource Hours48,
    [property: JsonProperty("7d")] PopularityWindowResource Days7,
    [property: JsonProperty("30d")] PopularityWindowResource Days30
);
