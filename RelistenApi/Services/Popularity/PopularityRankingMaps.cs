using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Hangfire;
using Relisten.Api.Models;

namespace Relisten.Services.Popularity;

// Cached IDs and scores are independent of catalog response versions. Both legacy
// lists and v4 hydrate their own resources from these inputs; v4 never caches bodies.
public partial class PopularityService
{
    public async Task RefreshGlobalShowPopularityMap() =>
        await cache.SetAsync(
            "popularity:shows:map:30d-48h",
            NewCacheEntry(await ComputeGlobalShowPopularityMap())
        );

    public async Task RefreshGlobalYearPopularityMap() =>
        await cache.SetAsync(
            "popularity:years:map:30d-48h",
            NewCacheEntry(await ComputeGlobalYearPopularityMap())
        );

    private async Task<Dictionary<Guid, PopularityMetrics>> ComputeGlobalShowPopularityMap()
    {
        var generatedAt = DateTime.UtcNow;
        var rows = await db.WithConnection(con =>
            con.QueryAsync<PopularityRow>(
                @"
                WITH plays_90d AS (
                    SELECT show_uuid, SUM(plays) AS plays_90d
                    FROM source_track_plays_daily
                    WHERE play_day >= now() - interval '90 days'
                    GROUP BY 1
                ),
                plays_30d AS (
                    SELECT show_uuid, SUM(plays) AS plays_30d,
                        SUM(total_track_seconds)::bigint AS seconds_30d
                    FROM source_track_plays_daily
                    WHERE play_day >= now() - interval '30 days'
                    GROUP BY 1
                ),
                plays_7d AS (
                    SELECT show_uuid, SUM(plays) AS plays_7d,
                        SUM(total_track_seconds)::bigint AS seconds_7d
                    FROM source_track_plays_daily
                    WHERE play_day >= now() - interval '7 days'
                    GROUP BY 1
                ),
                plays_6h AS (
                    SELECT show_uuid, SUM(plays) AS plays_6h
                    FROM source_track_plays_hourly
                    WHERE play_hour >= now() - interval '6 hours'
                    GROUP BY 1
                ),
                plays_48h AS (
                    SELECT show_uuid, SUM(plays) AS plays_48h,
                        SUM(total_track_seconds)::bigint AS seconds_48h
                    FROM source_track_plays_hourly
                    WHERE play_hour >= now() - interval '48 hours'
                    GROUP BY 1
                )
                SELECT
                    s.uuid AS uuid,
                    COALESCE(p30.plays_30d, 0) AS plays_30d,
                    COALESCE(p7.plays_7d, 0) AS plays_7d,
                    COALESCE(p48.plays_48h, 0) AS plays_48h
                    , COALESCE(p90.plays_90d, 0) AS plays_90d
                    , COALESCE(p6.plays_6h, 0) AS plays_6h
                    , COALESCE(p30.seconds_30d, 0) AS seconds_30d
                    , COALESCE(p7.seconds_7d, 0) AS seconds_7d
                    , COALESCE(p48.seconds_48h, 0) AS seconds_48h
                FROM shows s
                LEFT JOIN plays_30d p30 ON p30.show_uuid = s.uuid
                LEFT JOIN plays_7d p7 ON p7.show_uuid = s.uuid
                LEFT JOIN plays_48h p48 ON p48.show_uuid = s.uuid
                LEFT JOIN plays_90d p90 ON p90.show_uuid = s.uuid
                LEFT JOIN plays_6h p6 ON p6.show_uuid = s.uuid
                WHERE p30.plays_30d IS NOT NULL
            "
            )
        );
        var map = BuildMetricsMap(rows);
        await store.ReplaceGlobalShows(map, generatedAt);
        return map;
    }

    private async Task<Dictionary<Guid, PopularityMetrics>> ComputeGlobalYearPopularityMap()
    {
        var generatedAt = DateTime.UtcNow;
        var rows = await db.WithConnection(con =>
            con.QueryAsync<PopularityRow>(
                @"
                WITH plays_90d AS (
                    SELECT s.year_id, SUM(p.plays) AS plays_90d
                    FROM source_track_plays_daily p
                    JOIN shows s ON s.uuid = p.show_uuid
                    WHERE play_day >= now() - interval '90 days'
                    GROUP BY 1
                ),
                plays_30d AS (
                    SELECT s.year_id, SUM(p.plays) AS plays_30d,
                        SUM(p.total_track_seconds)::bigint AS seconds_30d
                    FROM source_track_plays_daily p
                    JOIN shows s ON s.uuid = p.show_uuid
                    WHERE p.play_day >= now() - interval '30 days'
                    GROUP BY 1
                ),
                plays_7d AS (
                    SELECT s.year_id, SUM(p.plays) AS plays_7d,
                        SUM(p.total_track_seconds)::bigint AS seconds_7d
                    FROM source_track_plays_daily p
                    JOIN shows s ON s.uuid = p.show_uuid
                    WHERE p.play_day >= now() - interval '7 days'
                    GROUP BY 1
                ),
                plays_6h AS (
                    SELECT s.year_id, SUM(p.plays) AS plays_6h
                    FROM source_track_plays_hourly p
                    JOIN shows s ON s.uuid = p.show_uuid
                    WHERE p.play_hour >= now() - interval '6 hours'
                    GROUP BY 1
                ),
                plays_48h AS (
                    SELECT s.year_id, SUM(p.plays) AS plays_48h,
                        SUM(p.total_track_seconds)::bigint AS seconds_48h
                    FROM source_track_plays_hourly p
                    JOIN shows s ON s.uuid = p.show_uuid
                    WHERE p.play_hour >= now() - interval '48 hours'
                    GROUP BY 1
                )
                SELECT
                    y.uuid AS uuid,
                    COALESCE(p30.plays_30d, 0) AS plays_30d,
                    COALESCE(p7.plays_7d, 0) AS plays_7d,
                    COALESCE(p48.plays_48h, 0) AS plays_48h
                    , COALESCE(p90.plays_90d, 0) AS plays_90d
                    , COALESCE(p6.plays_6h, 0) AS plays_6h
                    , COALESCE(p30.seconds_30d, 0) AS seconds_30d
                    , COALESCE(p7.seconds_7d, 0) AS seconds_7d
                    , COALESCE(p48.seconds_48h, 0) AS seconds_48h
                FROM years y
                LEFT JOIN plays_30d p30 ON p30.year_id = y.id
                LEFT JOIN plays_7d p7 ON p7.year_id = y.id
                LEFT JOIN plays_48h p48 ON p48.year_id = y.id
                LEFT JOIN plays_90d p90 ON p90.year_id = y.id
                LEFT JOIN plays_6h p6 ON p6.year_id = y.id
                WHERE p30.plays_30d IS NOT NULL
            "
            )
        );
        var map = BuildMetricsMap(rows);
        await store.ReplaceYears(null, map, generatedAt);
        return map;
    }
}
