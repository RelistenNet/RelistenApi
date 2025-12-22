using System;
using Relisten.Api.Models.Api;

namespace Relisten.Api.Models
{
    public class PopularityMetrics
    {
        [V3JsonOnly] public double hot_score { get; set; }
        [V3JsonOnly] public double momentum_score { get; set; }
        [V3JsonOnly] public double trend_ratio { get; set; }
        [V3JsonOnly] public long plays_30d { get; set; }
        [V3JsonOnly] public long plays_48h { get; set; }
    }
}
