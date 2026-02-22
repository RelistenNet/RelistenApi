using System;
using System.Collections.Generic;
using System.Linq;
using Relisten.Api.Models;

namespace Relisten.Services.Popularity
{
    internal static class CachedShowPopularityMapper
    {
        internal static Dictionary<Guid, CachedShowPopularity> ConvertLegacyShowPopularityMap(
            Dictionary<Guid, PopularityMetrics> map)
        {
            return map.ToDictionary(entry => entry.Key, entry => FromPopularityMetrics(entry.Value));
        }

        internal static Dictionary<Guid, CachedShowPopularity> NormalizeCachedShowPopularityMap(
            Dictionary<Guid, CachedShowPopularity>? map)
        {
            if (map == null)
            {
                return new Dictionary<Guid, CachedShowPopularity>();
            }

            foreach (var entry in map.Values)
            {
                entry.windows ??= new PopularityWindows();
            }

            return map;
        }

        internal static PopularityMetrics ToPopularityMetrics(CachedShowPopularity cached)
        {
            return new PopularityMetrics
            {
                windows = cached.windows ?? new PopularityWindows(),
                trend_ratio = cached.trend_ratio,
                momentum_score = cached.momentum_score,
                plays_6h = cached.plays_6h,
                plays_90d = cached.plays_90d
            };
        }

        internal static CachedShowPopularity FromPopularityMetrics(PopularityMetrics metrics)
        {
            return new CachedShowPopularity
            {
                windows = metrics.windows,
                trend_ratio = metrics.trend_ratio,
                momentum_score = metrics.momentum_score,
                plays_6h = metrics.plays_6h,
                plays_90d = metrics.plays_90d
            };
        }

        internal static double GetRankingMomentumScore(CachedShowPopularity cached)
        {
            return cached.momentum?.organic ?? cached.momentum?.raw ?? cached.momentum_score;
        }

        internal static double GetRawMomentumScore(CachedShowPopularity cached)
        {
            return cached.momentum?.raw ?? cached.momentum_score;
        }
    }
}
