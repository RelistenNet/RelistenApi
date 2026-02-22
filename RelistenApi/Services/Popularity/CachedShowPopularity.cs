using Relisten.Api.Models;

namespace Relisten.Services.Popularity
{
    internal class CachedShowPopularity
    {
        public PopularityWindows windows { get; set; } = new();
        public double trend_ratio { get; set; }
        public double momentum_score { get; set; }
        public long plays_6h { get; set; }
        public long plays_90d { get; set; }
        public CachedShowMomentum? momentum { get; set; }
    }

    internal class CachedShowMomentum
    {
        public double? raw { get; set; }
        public double? organic { get; set; }
        public double? otd_penalty_ratio_7d { get; set; }
    }
}
