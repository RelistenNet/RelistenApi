using System.Text.Json.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Relisten.Search;

/// <summary>
/// Listening windows and scores in the endpoint's context: artist-local for
/// ordinary show reads, global for global rankings, organic for momentum.
/// </summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record PopularityResource(
    [property: JsonPropertyName("momentum_score")] double MomentumScore,
    [property: JsonPropertyName("trend_ratio")] double TrendRatio,
    [property: JsonPropertyName("windows")] PopularityWindowsResource Windows
);

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record PopularityWindowResource(
    [property: JsonPropertyName("plays")] long Plays,
    [property: JsonPropertyName("hours")] double Hours,
    [property: JsonPropertyName("hot_score")] double HotScore
);

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record PopularityWindowsResource(
    [property: JsonProperty("48h"), JsonPropertyName("48h")] PopularityWindowResource Hours48,
    [property: JsonProperty("7d"), JsonPropertyName("7d")] PopularityWindowResource Days7,
    [property: JsonProperty("30d"), JsonPropertyName("30d")] PopularityWindowResource Days30,
    [property:
        JsonProperty("1y", NullValueHandling = NullValueHandling.Ignore),
        JsonPropertyName("1y"),
        System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)
    ]
        PopularityWindowResource? Year = null
);
