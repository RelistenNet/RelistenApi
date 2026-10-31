using System;
using Relisten.Api.Models;

namespace Relisten.Services.Popularity;

/// <summary>
/// Flat storage for one calculated score. Population selection belongs to the
/// query; global and artist-local momentum must never overwrite each other.
/// </summary>
public sealed class StoredPopularity
{
    public Guid uuid { get; set; }
    public Guid artist_uuid { get; set; }
    public long plays_30d { get; set; }
    public double hours_30d { get; set; }
    public double hot_score_30d { get; set; }
    public long plays_7d { get; set; }
    public double hours_7d { get; set; }
    public double hot_score_7d { get; set; }
    public long plays_48h { get; set; }
    public double hours_48h { get; set; }
    public double hot_score_48h { get; set; }
    public long plays_6h { get; set; }
    public long plays_90d { get; set; }
    public double trend_ratio { get; set; }
    public double momentum_score { get; set; }
    public double? organic_momentum_score { get; set; }

    public PopularityMetrics ToMetrics(bool organic = false) =>
        new()
        {
            windows = new()
            {
                days_30d = new()
                {
                    plays = plays_30d,
                    hours = hours_30d,
                    hot_score = hot_score_30d,
                },
                days_7d = new()
                {
                    plays = plays_7d,
                    hours = hours_7d,
                    hot_score = hot_score_7d,
                },
                hours_48h = new()
                {
                    plays = plays_48h,
                    hours = hours_48h,
                    hot_score = hot_score_48h,
                },
            },
            plays_6h = plays_6h,
            plays_90d = plays_90d,
            trend_ratio = trend_ratio,
            momentum_score = organic ? organic_momentum_score ?? momentum_score : momentum_score,
        };

    internal static StoredPopularity From(
        Guid id,
        PopularityMetrics value,
        double? organic = null
    ) =>
        new()
        {
            uuid = id,
            plays_30d = value.windows.days_30d.plays,
            hours_30d = value.windows.days_30d.hours,
            hot_score_30d = value.windows.days_30d.hot_score,
            plays_7d = value.windows.days_7d.plays,
            hours_7d = value.windows.days_7d.hours,
            hot_score_7d = value.windows.days_7d.hot_score,
            plays_48h = value.windows.hours_48h.plays,
            hours_48h = value.windows.hours_48h.hours,
            hot_score_48h = value.windows.hours_48h.hot_score,
            plays_6h = value.plays_6h,
            plays_90d = value.plays_90d,
            trend_ratio = value.trend_ratio,
            momentum_score = value.momentum_score,
            organic_momentum_score = organic,
        };
}
