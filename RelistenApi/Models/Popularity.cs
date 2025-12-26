using Newtonsoft.Json;
using Relisten.Api.Models.Api;

namespace Relisten.Api.Models
{
    public class PopularityWindowMetrics
    {
        [V3JsonOnly] public long plays { get; set; }
        [V3JsonOnly] public double hours { get; set; }
        [V3JsonOnly] public double hot_score { get; set; }
    }

    public class PopularityWindows
    {
        [JsonProperty("48h")] [V3JsonOnly] public PopularityWindowMetrics hours_48h { get; set; } = new();
        [JsonProperty("7d")] [V3JsonOnly] public PopularityWindowMetrics days_7d { get; set; } = new();
        [JsonProperty("30d")] [V3JsonOnly] public PopularityWindowMetrics days_30d { get; set; } = new();
    }

    public class PopularityMetrics
    {
        [V3JsonOnly] public double momentum_score { get; set; }
        [V3JsonOnly] public double trend_ratio { get; set; }
        [V3JsonOnly] public PopularityWindows windows { get; set; } = new();
        [JsonIgnore] public long plays_6h { get; set; }
        [JsonIgnore] public long plays_90d { get; set; }
    }
}
