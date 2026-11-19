using System;
using Newtonsoft.Json;
using Relisten.Api.Models;

namespace Relisten.Services.Popularity;

/// <summary>
/// Flat storage for one calculated score. Population selection belongs to the
/// query; global and artist-local momentum must never overwrite each other.
/// NULL momentum represents a calculated row without scored activity.
/// </summary>
public sealed class StoredPopularity
{
    // Keep the result shape stable when a migration adds columns. Read queries
    // alias the popularity table as p.
    internal const string ReadColumns = """
        p.uuid, p.plays_30d, p.hours_30d, p.hot_score_30d,
        p.plays_7d, p.hours_7d, p.hot_score_7d,
        p.plays_48h, p.hours_48h, p.hot_score_48h,
        p.trend_ratio, p.momentum_score, p.plays_1y, p.hours_1y, p.hot_score_1y
        """;

    // Metadata includes yearly-only targets. Ranking queries require a momentum score.
    internal const string MetadataFilter = """
        (p.momentum_score IS NOT NULL
            OR p.plays_1y > 0 OR p.plays_30d > 0 OR p.plays_7d > 0 OR p.plays_48h > 0)
        """;

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

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public long? plays_1y { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public double? hours_1y { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public double? hot_score_1y { get; set; }
    public double trend_ratio { get; set; }
    public double? momentum_score { get; set; }
    public double? organic_momentum_score { get; set; }

    public PopularityMetrics? ToMetrics(bool organic = false)
    {
        if (momentum_score is null && !HasPlays())
        {
            return null;
        }

        var raw = momentum_score.GetValueOrDefault();
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
                Year = plays_1y is { } plays
                    ? new()
                    {
                        plays = plays,
                        hours = hours_1y.GetValueOrDefault(),
                        hot_score = hot_score_1y.GetValueOrDefault(),
                    }
                    : null,
            },
            trend_ratio = trend_ratio,
            momentum_score = organic ? organic_momentum_score ?? raw : raw,
        };
    }

    private bool HasPlays() => plays_1y > 0 || plays_30d > 0 || plays_7d > 0 || plays_48h > 0;

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
