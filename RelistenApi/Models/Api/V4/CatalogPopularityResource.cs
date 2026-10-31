using Newtonsoft.Json;

namespace Relisten.Api.Models.Api.V4;

/// <summary>
/// Listening windows and scores. A show's momentum is raw and relative to its
/// artist's shows; ranking order may use global or organic selection scores.
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
