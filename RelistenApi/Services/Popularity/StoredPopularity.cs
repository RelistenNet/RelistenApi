using System;
using Relisten.Api.Models;

namespace Relisten.Services.Popularity;

/// <summary>
/// Flat storage for one calculated score. Population selection belongs to the
/// query; global and artist-local momentum must never overwrite each other.
/// NULL momentum represents a calculated row without scored activity.
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
    public double trend_ratio { get; set; }
    public double? momentum_score { get; set; }
    public double? organic_momentum_score { get; set; }

    public PopularityMetrics? ToMetrics(bool organic = false)
    {
        if (momentum_score is not { } raw)
        {
            return null;
        }
        return new()
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
            trend_ratio = trend_ratio,
            momentum_score = organic ? organic_momentum_score ?? raw : raw,
        };
    }

    // Window DTOs round in both caches. Metric maps also round their scalar scores;
    // the artist-local show cache retains full-precision raw/organic scores.
    internal static StoredPopularity From(
        Guid id,
        PopularityMetrics value,
        double? organic = null,
        bool roundScores = true
    ) =>
        new()
        {
            uuid = id,
            plays_30d = value.windows.days_30d.plays,
            hours_30d = Math.Round(value.windows.days_30d.hours, 4),
            hot_score_30d = Math.Round(value.windows.days_30d.hot_score, 4),
            plays_7d = value.windows.days_7d.plays,
            hours_7d = Math.Round(value.windows.days_7d.hours, 4),
            hot_score_7d = Math.Round(value.windows.days_7d.hot_score, 4),
            plays_48h = value.windows.hours_48h.plays,
            hours_48h = Math.Round(value.windows.hours_48h.hours, 4),
            hot_score_48h = Math.Round(value.windows.hours_48h.hot_score, 4),
            trend_ratio = roundScores ? Math.Round(value.trend_ratio, 4) : value.trend_ratio,
            momentum_score = roundScores
                ? Math.Round(value.momentum_score, 4)
                : value.momentum_score,
            organic_momentum_score = organic,
        };
}
