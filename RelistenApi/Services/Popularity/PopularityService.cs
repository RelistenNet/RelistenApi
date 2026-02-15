using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Hangfire;
using Microsoft.Extensions.Logging;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;
using Relisten.Data;

namespace Relisten.Services.Popularity
{
    public enum PopularitySortWindow
    {
        Hours48,
        Days7,
        Days30
    }

    public class PopularityService
    {
        private const int DefaultStaleAfterSeconds = 60 * 60;
        private const int ArtistTrendingPlays48hFloor = 130;
        private const int ArtistTrendingPlays30dFloor = 1808;
        private const int ShowTrendingPlays48hFloor = 9;
        private const int ShowTrendingPlays30dFloor = 7;
        private const int YearTrendingPlays48hFloor = 12;
        private const int YearTrendingPlays30dFloor = 67;

        private readonly DbService db;
        private readonly PopularityCacheService cache;
        private readonly ILogger<PopularityService> logger;

        public PopularityService(DbService db, PopularityCacheService cache, ILogger<PopularityService> logger)
        {
            this.db = db;
            this.cache = cache;
            this.logger = logger;
        }

        public async Task<Dictionary<Guid, PopularityMetrics>> GetArtistPopularityMap(bool allowStale = true)
        {
            var key = "popularity:artists:map:30d-48h";
            return await GetOrRefresh(key, DefaultStaleAfterSeconds, ComputeArtistPopularityMap,
                () => BackgroundJob.Enqueue<PopularityJobs>(job => job.RefreshArtistPopularityMap()),
                allowStale);
        }

        public async Task<Dictionary<Guid, PopularityMetrics>> GetShowPopularityMapForArtist(Guid artistUuid,
            bool allowStale = true)
        {
            var key = $"popularity:artist:{artistUuid}:shows:map:30d-48h";
            return await GetOrRefresh(key, DefaultStaleAfterSeconds,
                () => ComputeShowPopularityMapForArtist(artistUuid),
                () => BackgroundJob.Enqueue<PopularityJobs>(job => job.RefreshShowPopularityMapForArtist(artistUuid)),
                allowStale);
        }

        public async Task<Dictionary<Guid, PopularityMetrics>> GetYearPopularityMapForArtist(Guid artistUuid,
            bool allowStale = true)
        {
            var key = $"popularity:artist:{artistUuid}:years:map:30d-48h";
            return await GetOrRefresh(key, DefaultStaleAfterSeconds,
                () => ComputeYearPopularityMapForArtist(artistUuid),
                () => BackgroundJob.Enqueue<PopularityJobs>(job => job.RefreshYearPopularityMapForArtist(artistUuid)),
                allowStale);
        }

        public async Task<IReadOnlyList<PopularArtistListItem>> GetPopularArtists(int limit, bool allowStale = true)
        {
            var key = $"popularity:artists:hot:30d:{limit}";
            return await GetOrRefresh(key, DefaultStaleAfterSeconds,
                () => ComputePopularArtists(limit),
                () => BackgroundJob.Enqueue<PopularityJobs>(job => job.RefreshPopularArtists(limit)),
                allowStale);
        }

        public async Task<IReadOnlyList<PopularArtistListItem>> GetTrendingArtists(int limit, bool allowStale = true)
        {
            var key = $"popularity:artists:trending:48h:{limit}";
            return await GetOrRefresh(key, DefaultStaleAfterSeconds,
                () => ComputeTrendingArtists(limit),
                () => BackgroundJob.Enqueue<PopularityJobs>(job => job.RefreshTrendingArtists(limit)),
                allowStale);
        }

        public async Task<IReadOnlyList<Show>> GetPopularShows(int limit, bool allowStale = true,
            PopularitySortWindow sortWindow = PopularitySortWindow.Days30)
        {
            var key = GetShowPopularityCacheKey("popularity:shows:hot:30d", limit, sortWindow);
            return await GetOrRefresh(key, DefaultStaleAfterSeconds,
                () => ComputePopularShows(limit, sortWindow),
                () => BackgroundJob.Enqueue<PopularityJobs>(job => job.RefreshPopularShows(limit, sortWindow)),
                allowStale);
        }

        public async Task<IReadOnlyList<Show>> GetTrendingShows(int limit, bool allowStale = true,
            PopularitySortWindow sortWindow = PopularitySortWindow.Days30)
        {
            var key = GetShowPopularityCacheKey("popularity:shows:trending:48h", limit, sortWindow);
            return await GetOrRefresh(key, DefaultStaleAfterSeconds,
                () => ComputeTrendingShows(limit, sortWindow),
                () => BackgroundJob.Enqueue<PopularityJobs>(job => job.RefreshTrendingShows(limit, sortWindow)),
                allowStale);
        }

        public async Task<ArtistPopularTrendingShowsResponse> GetArtistPopularTrendingShows(Artist artist, int limit,
            bool allowStale = true, PopularitySortWindow sortWindow = PopularitySortWindow.Days30)
        {
            var candidateShows = await BuildArtistShowCandidates(artist, allowStale);
            return CreateArtistPopularTrendingShowsResponse(artist, candidateShows, limit, sortWindow);
        }

        public async Task<MultiArtistPopularTrendingShowsResponse> GetArtistsPopularTrendingShows(
            IReadOnlyList<Artist>? artists, int limit, bool allowStale = true,
            PopularitySortWindow sortWindow = PopularitySortWindow.Days30)
        {
            if (artists == null || artists.Count == 0)
            {
                return new MultiArtistPopularTrendingShowsResponse();
            }

            var artistResults = await Task.WhenAll(artists
                .Select(artist => GetArtistPopularTrendingShows(artist, limit, allowStale, sortWindow)));

            return new MultiArtistPopularTrendingShowsResponse
            {
                artists = artistResults.ToList()
            };
        }

        public async Task<IReadOnlyList<Show>> GetArtistsMomentumShows(IReadOnlyList<Artist>? artists, int limit,
            bool allowStale = true)
        {
            if (artists == null || artists.Count == 0)
            {
                return [];
            }

            var mapsByArtist = await Task.WhenAll(artists.Select(async artist => new
            {
                artist,
                map = await GetShowPopularityMapForArtist(artist.uuid, allowStale)
            }));

            var topCandidates = mapsByArtist
                .SelectMany(item => item.map.Select(entry => new
                {
                    item.artist,
                    showUuid = entry.Key,
                    metrics = entry.Value
                }))
                .OrderByDescending(item => item.metrics.momentum_score)
                .ThenByDescending(item => item.metrics.windows.days_30d.hot_score)
                .ThenByDescending(item => item.metrics.windows.days_30d.plays)
                .Take(limit)
                .ToList();

            if (topCandidates.Count == 0)
            {
                return [];
            }

            var featuresByArtistUuid = artists
                .GroupBy(artist => artist.uuid)
                .ToDictionary(group => group.Key, group => group.First().features);

            var showDetailsByUuid = await LoadShowDetailsByUuid(topCandidates.Select(item => item.showUuid),
                featuresByArtistUuid);
            
            if (showDetailsByUuid.Count == 0)
            {
                return [];
            }

            var ordered = new List<Show>(topCandidates.Count);
            foreach (var candidate in topCandidates)
            {
                if (!showDetailsByUuid.TryGetValue(candidate.showUuid, out var show))
                {
                    continue;
                }

                show.popularity = candidate.metrics;
                ordered.Add(show);
            }

            return ordered;
        }

        public async Task<IReadOnlyList<PopularYearListItem>> GetPopularYears(int limit, bool allowStale = true)
        {
            var key = $"popularity:years:hot:30d:{limit}";
            return await GetOrRefresh(key, DefaultStaleAfterSeconds,
                () => ComputePopularYears(limit),
                () => BackgroundJob.Enqueue<PopularityJobs>(job => job.RefreshPopularYears(limit)),
                allowStale);
        }

        public async Task<IReadOnlyList<PopularYearListItem>> GetTrendingYears(int limit, bool allowStale = true)
        {
            var key = $"popularity:years:trending:48h:{limit}";
            return await GetOrRefresh(key, DefaultStaleAfterSeconds,
                () => ComputeTrendingYears(limit),
                () => BackgroundJob.Enqueue<PopularityJobs>(job => job.RefreshTrendingYears(limit)),
                allowStale);
        }

        public async Task RefreshArtistPopularityMap()
        {
            var map = await ComputeArtistPopularityMap();
            await cache.SetAsync("popularity:artists:map:30d-48h", NewCacheEntry(map));
        }

        public async Task RefreshShowPopularityMapForArtist(Guid artistUuid)
        {
            var map = await ComputeShowPopularityMapForArtist(artistUuid);
            await cache.SetAsync($"popularity:artist:{artistUuid}:shows:map:30d-48h", NewCacheEntry(map));
        }

        public async Task RefreshYearPopularityMapForArtist(Guid artistUuid)
        {
            var map = await ComputeYearPopularityMapForArtist(artistUuid);
            await cache.SetAsync($"popularity:artist:{artistUuid}:years:map:30d-48h", NewCacheEntry(map));
        }

        public async Task RefreshPopularArtists(int limit)
        {
            var list = await ComputePopularArtists(limit);
            await cache.SetAsync($"popularity:artists:hot:30d:{limit}", NewCacheEntry(list));
        }

        public async Task RefreshTrendingArtists(int limit)
        {
            var list = await ComputeTrendingArtists(limit);
            await cache.SetAsync($"popularity:artists:trending:48h:{limit}", NewCacheEntry(list));
        }

        public async Task RefreshPopularShows(int limit,
            PopularitySortWindow sortWindow = PopularitySortWindow.Days30)
        {
            var list = await ComputePopularShows(limit, sortWindow);
            await cache.SetAsync(GetShowPopularityCacheKey("popularity:shows:hot:30d", limit, sortWindow),
                NewCacheEntry(list));
        }

        public async Task RefreshTrendingShows(int limit,
            PopularitySortWindow sortWindow = PopularitySortWindow.Days30)
        {
            var list = await ComputeTrendingShows(limit, sortWindow);
            await cache.SetAsync(GetShowPopularityCacheKey("popularity:shows:trending:48h", limit, sortWindow),
                NewCacheEntry(list));
        }

        public async Task RefreshPopularYears(int limit)
        {
            var list = await ComputePopularYears(limit);
            await cache.SetAsync($"popularity:years:hot:30d:{limit}", NewCacheEntry(list));
        }

        public async Task RefreshTrendingYears(int limit)
        {
            var list = await ComputeTrendingYears(limit);
            await cache.SetAsync($"popularity:years:trending:48h:{limit}", NewCacheEntry(list));
        }

        public void ApplyArtistPopularity(IEnumerable<ArtistWithCounts> artists,
            Dictionary<Guid, PopularityMetrics> map)
        {
            foreach (var artist in artists)
            {
                if (map.TryGetValue(artist.uuid, out var metrics))
                {
                    artist.popularity = metrics;
                }
            }
        }

        public void ApplyYearPopularity(IEnumerable<Year> years, Dictionary<Guid, PopularityMetrics> map)
        {
            foreach (var year in years)
            {
                if (map.TryGetValue(year.uuid, out var metrics))
                {
                    year.popularity = metrics;
                }
            }
        }

        public void ApplyShowPopularity(IEnumerable<Show> shows, Dictionary<Guid, PopularityMetrics> map)
        {
            foreach (var show in shows)
            {
                if (map.TryGetValue(show.uuid, out var metrics))
                {
                    show.popularity = metrics;
                }
            }
        }

        private async Task<T> GetOrRefresh<T>(string key, int staleAfterSeconds, Func<Task<T>> factory,
            Action refreshAction, bool allowStale)
        {
            var cached = await cache.GetAsync<T>(key);
            if (cached.HasValue && cached.Entry != null)
            {
                if (!cached.IsStale)
                {
                    return cached.Entry.data;
                }

                if (allowStale)
                {
                    refreshAction();
                    return cached.Entry.data;
                }
            }

            var fresh = await factory();
            await cache.SetAsync(key, NewCacheEntry(fresh, staleAfterSeconds));
            return fresh;
        }

        private PopularityCacheEntry<T> NewCacheEntry<T>(T data, int staleAfterSeconds = DefaultStaleAfterSeconds)
        {
            return new PopularityCacheEntry<T>
            {
                data = data,
                generated_at = DateTime.UtcNow,
                stale_after_seconds = staleAfterSeconds
            };
        }

        private async Task<Dictionary<Guid, PopularityMetrics>> ComputeArtistPopularityMap()
        {
            var rows = await db.WithConnection(con => con.QueryAsync<PopularityRow>(@"
                WITH plays_90d AS (
                    SELECT artist_uuid, SUM(plays) AS plays_90d
                    FROM source_track_plays_daily
                    WHERE play_day >= now() - interval '90 days'
                    GROUP BY 1
                ),
                plays_30d AS (
                    SELECT artist_uuid, SUM(plays) AS plays_30d,
                        SUM(total_track_seconds)::bigint AS seconds_30d
                    FROM source_track_plays_daily
                    WHERE play_day >= now() - interval '30 days'
                    GROUP BY 1
                ),
                plays_7d AS (
                    SELECT artist_uuid, SUM(plays) AS plays_7d,
                        SUM(total_track_seconds)::bigint AS seconds_7d
                    FROM source_track_plays_daily
                    WHERE play_day >= now() - interval '7 days'
                    GROUP BY 1
                ),
                plays_6h AS (
                    SELECT artist_uuid, SUM(plays) AS plays_6h
                    FROM source_track_plays_hourly
                    WHERE play_hour >= now() - interval '6 hours'
                    GROUP BY 1
                ),
                plays_48h AS (
                    SELECT artist_uuid, SUM(plays) AS plays_48h,
                        SUM(total_track_seconds)::bigint AS seconds_48h
                    FROM source_track_plays_hourly
                    WHERE play_hour >= now() - interval '48 hours'
                    GROUP BY 1
                )
                SELECT
                    COALESCE(p30.artist_uuid, p48.artist_uuid, p7.artist_uuid, p90.artist_uuid, p6.artist_uuid) AS uuid,
                    COALESCE(p30.plays_30d, 0) AS plays_30d,
                    COALESCE(p7.plays_7d, 0) AS plays_7d,
                    COALESCE(p48.plays_48h, 0) AS plays_48h
                    , COALESCE(p90.plays_90d, 0) AS plays_90d
                    , COALESCE(p6.plays_6h, 0) AS plays_6h
                    , COALESCE(p30.seconds_30d, 0) AS seconds_30d
                    , COALESCE(p7.seconds_7d, 0) AS seconds_7d
                    , COALESCE(p48.seconds_48h, 0) AS seconds_48h
                FROM plays_30d p30
                FULL OUTER JOIN plays_48h p48 ON p48.artist_uuid = p30.artist_uuid
                FULL OUTER JOIN plays_7d p7 ON p7.artist_uuid = COALESCE(p30.artist_uuid, p48.artist_uuid)
                FULL OUTER JOIN plays_90d p90 ON p90.artist_uuid = COALESCE(p30.artist_uuid, p48.artist_uuid, p7.artist_uuid)
                FULL OUTER JOIN plays_6h p6 ON p6.artist_uuid = COALESCE(p30.artist_uuid, p48.artist_uuid, p7.artist_uuid, p90.artist_uuid)
            "));

            return BuildMetricsMap(rows);
        }

        private async Task<Dictionary<Guid, PopularityMetrics>> ComputeShowPopularityMapForArtist(Guid artistUuid)
        {
            var rows = await db.WithConnection(con => con.QueryAsync<PopularityRow>(@"
                WITH plays_90d AS (
                    SELECT show_uuid AS uuid, SUM(plays) AS plays_90d
                    FROM source_track_plays_daily
                    WHERE artist_uuid = @artistUuid
                      AND play_day >= now() - interval '90 days'
                    GROUP BY 1
                ),
                plays_30d AS (
                    SELECT show_uuid AS uuid, SUM(plays) AS plays_30d,
                        SUM(total_track_seconds)::bigint AS seconds_30d
                    FROM source_track_plays_daily
                    WHERE artist_uuid = @artistUuid
                      AND play_day >= now() - interval '30 days'
                    GROUP BY 1
                ),
                plays_7d AS (
                    SELECT show_uuid AS uuid, SUM(plays) AS plays_7d,
                        SUM(total_track_seconds)::bigint AS seconds_7d
                    FROM source_track_plays_daily
                    WHERE artist_uuid = @artistUuid
                      AND play_day >= now() - interval '7 days'
                    GROUP BY 1
                ),
                plays_6h AS (
                    SELECT show_uuid AS uuid, SUM(plays) AS plays_6h
                    FROM source_track_plays_hourly
                    WHERE artist_uuid = @artistUuid
                      AND play_hour >= now() - interval '6 hours'
                    GROUP BY 1
                ),
                plays_48h AS (
                    SELECT show_uuid AS uuid, SUM(plays) AS plays_48h,
                        SUM(total_track_seconds)::bigint AS seconds_48h
                    FROM source_track_plays_hourly
                    WHERE artist_uuid = @artistUuid
                      AND play_hour >= now() - interval '48 hours'
                    GROUP BY 1
                )
                SELECT
                    COALESCE(p30.uuid, p48.uuid, p7.uuid, p90.uuid, p6.uuid) AS uuid,
                    COALESCE(p30.plays_30d, 0) AS plays_30d,
                    COALESCE(p7.plays_7d, 0) AS plays_7d,
                    COALESCE(p48.plays_48h, 0) AS plays_48h
                    , COALESCE(p90.plays_90d, 0) AS plays_90d
                    , COALESCE(p6.plays_6h, 0) AS plays_6h
                    , COALESCE(p30.seconds_30d, 0) AS seconds_30d
                    , COALESCE(p7.seconds_7d, 0) AS seconds_7d
                    , COALESCE(p48.seconds_48h, 0) AS seconds_48h
                FROM plays_30d p30
                FULL OUTER JOIN plays_48h p48 ON p48.uuid = p30.uuid
                FULL OUTER JOIN plays_7d p7 ON p7.uuid = COALESCE(p30.uuid, p48.uuid)
                FULL OUTER JOIN plays_90d p90 ON p90.uuid = COALESCE(p30.uuid, p48.uuid, p7.uuid)
                FULL OUTER JOIN plays_6h p6 ON p6.uuid = COALESCE(p30.uuid, p48.uuid, p7.uuid, p90.uuid)
            ", new { artistUuid }));

            return BuildMetricsMap(rows);
        }

        private async Task<Dictionary<Guid, PopularityMetrics>> ComputeYearPopularityMapForArtist(Guid artistUuid)
        {
            var rows = await db.WithConnection(con => con.QueryAsync<PopularityRow>(@"
                WITH plays_90d AS (
                    SELECT y.uuid AS uuid, SUM(p.plays) AS plays_90d
                    FROM source_track_plays_daily p
                    JOIN shows s ON s.uuid = p.show_uuid
                    JOIN years y ON y.id = s.year_id
                    WHERE p.artist_uuid = @artistUuid
                      AND p.play_day >= now() - interval '90 days'
                    GROUP BY 1
                ),
                plays_30d AS (
                    SELECT y.uuid AS uuid, SUM(p.plays) AS plays_30d,
                        SUM(p.total_track_seconds)::bigint AS seconds_30d
                    FROM source_track_plays_daily p
                    JOIN shows s ON s.uuid = p.show_uuid
                    JOIN years y ON y.id = s.year_id
                    WHERE p.artist_uuid = @artistUuid
                      AND p.play_day >= now() - interval '30 days'
                    GROUP BY 1
                ),
                plays_7d AS (
                    SELECT y.uuid AS uuid, SUM(p.plays) AS plays_7d,
                        SUM(p.total_track_seconds)::bigint AS seconds_7d
                    FROM source_track_plays_daily p
                    JOIN shows s ON s.uuid = p.show_uuid
                    JOIN years y ON y.id = s.year_id
                    WHERE p.artist_uuid = @artistUuid
                      AND p.play_day >= now() - interval '7 days'
                    GROUP BY 1
                ),
                plays_6h AS (
                    SELECT y.uuid AS uuid, SUM(p.plays) AS plays_6h
                    FROM source_track_plays_hourly p
                    JOIN shows s ON s.uuid = p.show_uuid
                    JOIN years y ON y.id = s.year_id
                    WHERE p.artist_uuid = @artistUuid
                      AND p.play_hour >= now() - interval '6 hours'
                    GROUP BY 1
                ),
                plays_48h AS (
                    SELECT y.uuid AS uuid, SUM(p.plays) AS plays_48h,
                        SUM(p.total_track_seconds)::bigint AS seconds_48h
                    FROM source_track_plays_hourly p
                    JOIN shows s ON s.uuid = p.show_uuid
                    JOIN years y ON y.id = s.year_id
                    WHERE p.artist_uuid = @artistUuid
                      AND p.play_hour >= now() - interval '48 hours'
                    GROUP BY 1
                )
                SELECT
                    COALESCE(p30.uuid, p48.uuid, p7.uuid, p90.uuid, p6.uuid) AS uuid,
                    COALESCE(p30.plays_30d, 0) AS plays_30d,
                    COALESCE(p7.plays_7d, 0) AS plays_7d,
                    COALESCE(p48.plays_48h, 0) AS plays_48h
                    , COALESCE(p90.plays_90d, 0) AS plays_90d
                    , COALESCE(p6.plays_6h, 0) AS plays_6h
                    , COALESCE(p30.seconds_30d, 0) AS seconds_30d
                    , COALESCE(p7.seconds_7d, 0) AS seconds_7d
                    , COALESCE(p48.seconds_48h, 0) AS seconds_48h
                FROM plays_30d p30
                FULL OUTER JOIN plays_48h p48 ON p48.uuid = p30.uuid
                FULL OUTER JOIN plays_7d p7 ON p7.uuid = COALESCE(p30.uuid, p48.uuid)
                FULL OUTER JOIN plays_90d p90 ON p90.uuid = COALESCE(p30.uuid, p48.uuid, p7.uuid)
                FULL OUTER JOIN plays_6h p6 ON p6.uuid = COALESCE(p30.uuid, p48.uuid, p7.uuid, p90.uuid)
            ", new { artistUuid }));

            return BuildMetricsMap(rows);
        }

        private async Task<IReadOnlyList<PopularArtistListItem>> ComputePopularArtists(int limit)
        {
            var rows = await db.WithConnection(con => con.QueryAsync<PopularityArtistRow>(@"
                WITH plays_90d AS (
                    SELECT artist_uuid, SUM(plays) AS plays_90d
                    FROM source_track_plays_daily
                    WHERE play_day >= now() - interval '90 days'
                    GROUP BY 1
                ),
                plays_30d AS (
                    SELECT artist_uuid, SUM(plays) AS plays_30d,
                        SUM(total_track_seconds)::bigint AS seconds_30d
                    FROM source_track_plays_daily
                    WHERE play_day >= now() - interval '30 days'
                    GROUP BY 1
                ),
                plays_7d AS (
                    SELECT artist_uuid, SUM(plays) AS plays_7d,
                        SUM(total_track_seconds)::bigint AS seconds_7d
                    FROM source_track_plays_daily
                    WHERE play_day >= now() - interval '7 days'
                    GROUP BY 1
                ),
                plays_6h AS (
                    SELECT artist_uuid, SUM(plays) AS plays_6h
                    FROM source_track_plays_hourly
                    WHERE play_hour >= now() - interval '6 hours'
                    GROUP BY 1
                ),
                plays_48h AS (
                    SELECT artist_uuid, SUM(plays) AS plays_48h,
                        SUM(total_track_seconds)::bigint AS seconds_48h
                    FROM source_track_plays_hourly
                    WHERE play_hour >= now() - interval '48 hours'
                    GROUP BY 1
                )
                SELECT
                    a.uuid AS artist_uuid,
                    a.name,
                    COALESCE(p30.plays_30d, 0) AS plays_30d,
                    COALESCE(p7.plays_7d, 0) AS plays_7d,
                    COALESCE(p48.plays_48h, 0) AS plays_48h
                    , COALESCE(p90.plays_90d, 0) AS plays_90d
                    , COALESCE(p6.plays_6h, 0) AS plays_6h
                    , COALESCE(p30.seconds_30d, 0) AS seconds_30d
                    , COALESCE(p7.seconds_7d, 0) AS seconds_7d
                    , COALESCE(p48.seconds_48h, 0) AS seconds_48h
                FROM artists a
                LEFT JOIN plays_30d p30 ON p30.artist_uuid = a.uuid
                LEFT JOIN plays_7d p7 ON p7.artist_uuid = a.uuid
                LEFT JOIN plays_48h p48 ON p48.artist_uuid = a.uuid
                LEFT JOIN plays_90d p90 ON p90.artist_uuid = a.uuid
                LEFT JOIN plays_6h p6 ON p6.artist_uuid = a.uuid
                WHERE p30.plays_30d IS NOT NULL
            "));

            var items = rows.Select(row => new PopularArtistListItem
            {
                artist_uuid = row.artist_uuid,
                name = row.name,
                plays_30d = row.plays_30d,
                plays_48h = row.plays_48h,
                trend_ratio = ComputeTrendRatio(row.plays_48h, row.plays_90d),
                popularity = CreateMetrics(row.plays_30d, row.plays_7d, row.plays_6h, row.plays_48h, row.plays_90d,
                    row.seconds_30d, row.seconds_7d, row.seconds_48h)
            }).ToList();

            ApplyMomentumScores(items.Select(item => item.popularity).ToList());

            var ordered = items
                .OrderByDescending(item => item.popularity.windows.days_30d.hot_score)
                .ThenByDescending(item => item.plays_30d)
                .Take(limit)
                .ToList();

            AssignRanks(ordered);
            return ordered;
        }

        private async Task<IReadOnlyList<PopularArtistListItem>> ComputeTrendingArtists(int limit)
        {
            var items = await ComputePopularArtists(int.MaxValue);
            var filtered = items
                .Where(item => item.plays_48h >= ArtistTrendingPlays48hFloor &&
                               item.plays_30d >= ArtistTrendingPlays30dFloor)
                .OrderByDescending(item => item.trend_ratio)
                .ThenByDescending(item => item.popularity.windows.days_30d.hot_score)
                .Take(limit)
                .ToList();

            AssignRanks(filtered);
            return filtered;
        }

        private async Task<IReadOnlyList<Show>> ComputePopularShows(int limit, PopularitySortWindow sortWindow)
        {
            var rows = await db.WithConnection(con => con.QueryAsync<PopularityShowRow>(@"
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
                    s.uuid AS show_uuid,
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
            "));

            var showDetails = await LoadShowDetailsByUuid(rows.Select(row => row.show_uuid), null);
            var items = new List<Show>();

            foreach (var row in rows)
            {
                if (!showDetails.TryGetValue(row.show_uuid, out var show))
                {
                    continue;
                }

                show.popularity = CreateMetrics(row.plays_30d, row.plays_7d, row.plays_6h, row.plays_48h,
                    row.plays_90d, row.seconds_30d, row.seconds_7d, row.seconds_48h);
                items.Add(show);
            }

            ApplyMomentumScores(items
                .Select(item => item.popularity)
                .Where(metrics => metrics != null)
                .Select(metrics => metrics!)
                .ToList());

            var ordered = items
                .OrderByDescending(item => GetSortWindowHotScore(item, sortWindow))
                .ThenByDescending(item => GetSortWindowPlays(item, sortWindow))
                .Take(limit)
                .ToList();

            return ordered;
        }

        private async Task<IReadOnlyList<Show>> ComputeTrendingShows(int limit, PopularitySortWindow sortWindow)
        {
            var items = await ComputePopularShows(int.MaxValue, sortWindow);
            var filtered = items
                .Where(item => (item.popularity?.windows.hours_48h.plays ?? 0) >= ShowTrendingPlays48hFloor &&
                               (item.popularity?.windows.days_30d.plays ?? 0) >= ShowTrendingPlays30dFloor)
                .OrderByDescending(item => GetSortWindowHotScore(item, sortWindow))
                .ThenByDescending(item => GetSortWindowPlays(item, sortWindow))
                .ThenByDescending(item => item.popularity?.trend_ratio ?? 0)
                .Take(limit)
                .ToList();

            return filtered;
        }

        private async Task<IReadOnlyList<Show>> BuildArtistShowCandidates(Artist artist,
            bool allowStale)
        {
            var showPopularity = await GetShowPopularityMapForArtist(artist.uuid, allowStale);
            if (showPopularity.Count == 0)
            {
                return [];
            }

            IReadOnlyDictionary<Guid, Features>? featuresByArtistUuid = artist.features == null
                ? null
                : new Dictionary<Guid, Features>
                {
                    [artist.uuid] = artist.features
                };

            var showDetails = await LoadShowDetailsByUuid(showPopularity.Keys, featuresByArtistUuid);
            if (showDetails.Count == 0)
            {
                return [];
            }

            var items = new List<Show>(showDetails.Count);
            foreach (var show in showDetails.Values)
            {
                if (!showPopularity.TryGetValue(show.uuid, out var metrics))
                {
                    continue;
                }

                show.popularity = metrics;
                items.Add(show);
            }

            return items;
        }

        internal static ArtistPopularTrendingShowsResponse CreateArtistPopularTrendingShowsResponse(Artist artist,
            IReadOnlyList<Show> candidateShows, int limit,
            PopularitySortWindow sortWindow = PopularitySortWindow.Days30)
        {
            return new ArtistPopularTrendingShowsResponse
            {
                artist_uuid = artist.uuid,
                artist_name = artist.name,
                popular_shows = RankPopularArtistShows(candidateShows, limit, sortWindow),
                trending_shows = RankTrendingArtistShows(candidateShows, limit, sortWindow)
            };
        }

        internal static IReadOnlyList<Show> RankPopularArtistShows(
            IReadOnlyList<Show> candidateShows, int limit,
            PopularitySortWindow sortWindow = PopularitySortWindow.Days30)
        {
            var ordered = candidateShows
                .OrderByDescending(item => GetSortWindowHotScore(item, sortWindow))
                .ThenByDescending(item => GetSortWindowPlays(item, sortWindow))
                .Take(limit)
                .ToList();

            return ordered;
        }

        internal static IReadOnlyList<Show> RankTrendingArtistShows(
            IReadOnlyList<Show> candidateShows, int limit,
            PopularitySortWindow sortWindow = PopularitySortWindow.Days30)
        {
            var filtered = candidateShows
                .Where(item => (item.popularity?.windows.hours_48h.plays ?? 0) >= ShowTrendingPlays48hFloor &&
                               (item.popularity?.windows.days_30d.plays ?? 0) >= ShowTrendingPlays30dFloor)
                .OrderByDescending(item => GetSortWindowHotScore(item, sortWindow))
                .ThenByDescending(item => GetSortWindowPlays(item, sortWindow))
                .ThenByDescending(item => item.popularity?.trend_ratio ?? 0)
                .Take(limit)
                .ToList();

            return filtered;
        }

        private static double GetSortWindowHotScore(Show show, PopularitySortWindow sortWindow)
        {
            var windows = show.popularity?.windows;
            if (windows == null)
            {
                return 0;
            }

            return sortWindow switch
            {
                PopularitySortWindow.Hours48 => windows.hours_48h.hot_score,
                PopularitySortWindow.Days7 => windows.days_7d.hot_score,
                _ => windows.days_30d.hot_score
            };
        }

        private static long GetSortWindowPlays(Show show, PopularitySortWindow sortWindow)
        {
            var windows = show.popularity?.windows;
            if (windows == null)
            {
                return 0;
            }

            return sortWindow switch
            {
                PopularitySortWindow.Hours48 => windows.hours_48h.plays,
                PopularitySortWindow.Days7 => windows.days_7d.plays,
                _ => windows.days_30d.plays
            };
        }

        private static string GetShowPopularityCacheKey(string baseKey, int limit, PopularitySortWindow sortWindow)
        {
            if (sortWindow == PopularitySortWindow.Days30)
            {
                return $"{baseKey}:{limit}";
            }

            return $"{baseKey}:{limit}:sort:{ToSortWindowToken(sortWindow)}";
        }

        private static string ToSortWindowToken(PopularitySortWindow sortWindow)
        {
            return sortWindow switch
            {
                PopularitySortWindow.Hours48 => "48h",
                PopularitySortWindow.Days7 => "7d",
                _ => "30d"
            };
        }

        private async Task<Dictionary<Guid, Show>> LoadShowDetailsByUuid(IEnumerable<Guid> showUuids,
            IReadOnlyDictionary<Guid, Features>? featuresByArtistUuid)
        {
            var uuids = showUuids.Distinct().ToArray();
            if (uuids.Length == 0)
            {
                return new Dictionary<Guid, Show>();
            }

            var shows = (await db.WithConnection(con => con.QueryAsync<Show, VenueWithShowCount, Tour, Era, Year, Show>(
                @"
                SELECT
                    s.*,
                    a.uuid as artist_uuid,
                    cnt.max_updated_at as most_recent_source_updated_at,
                    cnt.source_count,
                    cnt.has_soundboard_source,
                    cnt.has_flac as has_streamable_flac_source,
                    v.uuid as venue_uuid,
                    t.uuid as tour_uuid,
                    y.uuid as year_uuid,
                    v.*,
                    a.uuid as artist_uuid,
                    COALESCE(venue_counts.shows_at_venue, 0) as shows_at_venue,
                    t.*,
                    a.uuid as artist_uuid,
                    e.*,
                    a.uuid as artist_uuid,
                    y.*,
                    a.uuid as artist_uuid
                FROM
                    shows s
                    JOIN artists a ON s.artist_id = a.id
                    LEFT JOIN venues v ON s.venue_id = v.id
                    LEFT JOIN tours t ON s.tour_id = t.id
                    LEFT JOIN eras e ON s.era_id = e.id
                    LEFT JOIN years y ON s.year_id = y.id

                    INNER JOIN show_source_information cnt ON cnt.show_id = s.id
                    LEFT JOIN venue_show_counts venue_counts ON venue_counts.id = s.venue_id
                WHERE
                    s.uuid = ANY(@uuids)
            ", (show, venue, tour, era, year) =>
                {
                    show.venue = venue;

                    var includeTours = true;
                    var includeEras = true;
                    var includeYears = true;
                    if (featuresByArtistUuid != null &&
                        featuresByArtistUuid.TryGetValue(show.artist_uuid, out var features))
                    {
                        includeTours = features.tours;
                        includeEras = features.eras;
                        includeYears = features.years;
                    }

                    if (includeTours)
                    {
                        show.tour = tour;
                    }

                    if (includeEras)
                    {
                        show.era = era;
                    }

                    if (includeYears)
                    {
                        show.year = year;
                    }

                    return show;
                }, new { uuids }))).ToList();

            return shows.ToDictionary(show => show.uuid, show => show);
        }

        private async Task<IReadOnlyList<PopularYearListItem>> ComputePopularYears(int limit)
        {
            var rows = await db.WithConnection(con => con.QueryAsync<PopularityYearRow>(@"
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
                    y.uuid AS year_uuid,
                    y.year,
                    a.uuid AS artist_uuid,
                    a.name AS artist_name,
                    COALESCE(p30.plays_30d, 0) AS plays_30d,
                    COALESCE(p7.plays_7d, 0) AS plays_7d,
                    COALESCE(p48.plays_48h, 0) AS plays_48h
                    , COALESCE(p90.plays_90d, 0) AS plays_90d
                    , COALESCE(p6.plays_6h, 0) AS plays_6h
                    , COALESCE(p30.seconds_30d, 0) AS seconds_30d
                    , COALESCE(p7.seconds_7d, 0) AS seconds_7d
                    , COALESCE(p48.seconds_48h, 0) AS seconds_48h
                FROM years y
                JOIN artists a ON a.id = y.artist_id
                LEFT JOIN plays_30d p30 ON p30.year_id = y.id
                LEFT JOIN plays_7d p7 ON p7.year_id = y.id
                LEFT JOIN plays_48h p48 ON p48.year_id = y.id
                LEFT JOIN plays_90d p90 ON p90.year_id = y.id
                LEFT JOIN plays_6h p6 ON p6.year_id = y.id
                WHERE p30.plays_30d IS NOT NULL
            "));

            var items = rows.Select(row => new PopularYearListItem
            {
                year_uuid = row.year_uuid,
                year = row.year,
                artist_uuid = row.artist_uuid,
                artist_name = row.artist_name,
                plays_30d = row.plays_30d,
                plays_48h = row.plays_48h,
                trend_ratio = ComputeTrendRatio(row.plays_48h, row.plays_90d),
                popularity = CreateMetrics(row.plays_30d, row.plays_7d, row.plays_6h, row.plays_48h, row.plays_90d,
                    row.seconds_30d, row.seconds_7d, row.seconds_48h)
            }).ToList();

            ApplyMomentumScores(items.Select(item => item.popularity).ToList());

            var ordered = items
                .OrderByDescending(item => item.popularity.windows.days_30d.hot_score)
                .ThenByDescending(item => item.plays_30d)
                .Take(limit)
                .ToList();

            AssignRanks(ordered);
            return ordered;
        }

        private async Task<IReadOnlyList<PopularYearListItem>> ComputeTrendingYears(int limit)
        {
            var items = await ComputePopularYears(int.MaxValue);
            var filtered = items
                .Where(item => item.plays_48h >= YearTrendingPlays48hFloor &&
                               item.plays_30d >= YearTrendingPlays30dFloor)
                .OrderByDescending(item => item.trend_ratio)
                .ThenByDescending(item => item.popularity.windows.days_30d.hot_score)
                .Take(limit)
                .ToList();

            AssignRanks(filtered);
            return filtered;
        }

        private Dictionary<Guid, PopularityMetrics> BuildMetricsMap(IEnumerable<PopularityRow> rows)
        {
            var metrics = rows.Select(row => new
            {
                row.uuid,
                metrics = CreateMetrics(row.plays_30d, row.plays_7d, row.plays_6h, row.plays_48h, row.plays_90d,
                    row.seconds_30d, row.seconds_7d, row.seconds_48h)
            }).ToList();

            ApplyMomentumScores(metrics.Select(item => item.metrics).ToList());

            return metrics.ToDictionary(item => item.uuid, item => item.metrics);
        }

        internal static PopularityMetrics CreateMetrics(long plays30d, long plays7d, long plays6h, long plays48h,
            long plays90d, long seconds30d, long seconds7d, long seconds48h)
        {
            return new PopularityMetrics
            {
                windows = new PopularityWindows
                {
                    days_30d = new PopularityWindowMetrics
                    {
                        plays = plays30d,
                        hours = seconds30d / 3600.0,
                        hot_score = Math.Sqrt(Math.Max(plays30d, 0))
                    },
                    days_7d = new PopularityWindowMetrics
                    {
                        plays = plays7d,
                        hours = seconds7d / 3600.0,
                        hot_score = Math.Sqrt(Math.Max(plays7d, 0))
                    },
                    hours_48h = new PopularityWindowMetrics
                    {
                        plays = plays48h,
                        hours = seconds48h / 3600.0,
                        hot_score = Math.Sqrt(Math.Max(plays48h, 0))
                    }
                },
                plays_6h = plays6h,
                plays_90d = plays90d,
                trend_ratio = ComputeTrendRatio(plays48h, plays90d),
                momentum_score = 0
            };
        }

        internal static double ComputeTrendRatio(long plays48h, long plays90d)
        {
            if (plays90d <= 0)
            {
                return 0;
            }

            return (plays48h / 48.0) / (plays90d / 2160.0);
        }

        internal static double ComputeShortTrendRatio(long plays6h, long plays7d)
        {
            if (plays7d <= 0)
            {
                return 0;
            }

            return (plays6h / 6.0) / (plays7d / 168.0);
        }

        internal static void ApplyMomentumScores(IReadOnlyList<PopularityMetrics> metrics)
        {
            if (metrics.Count == 0)
            {
                return;
            }

            if (metrics.Count == 1)
            {
                metrics[0].momentum_score = 1;
                return;
            }

                var trendRanks = PercentileRanks(metrics.Select(item => item.trend_ratio).ToList());
                var hotRanks = PercentileRanks(metrics
                    .Select(item => item.windows.days_30d.hot_score)
                    .ToList());
                var shortTrendRanks = PercentileRanks(metrics
                    .Select(item =>
                    {
                        return ComputeShortTrendRatio(item.plays_6h, item.windows.days_7d.plays);
                    })
                    .ToList());

            for (var i = 0; i < metrics.Count; i++)
            {
                var trendNorm = trendRanks[i];
                var hotNorm = hotRanks[i];
                var shortNorm = shortTrendRanks[i];
                metrics[i].momentum_score = Math.Clamp(0.6 * trendNorm + 0.2 * shortNorm + 0.2 * hotNorm, 0, 1);
            }
        }

        private static IReadOnlyList<double> PercentileRanks(IReadOnlyList<double> values)
        {
            if (values.Count > 1 && values.All(value => value == values[0]))
            {
                return Enumerable.Repeat(0.5, values.Count).ToList();
            }

            var indexed = values.Select((value, index) => new { value, index }).ToList();
            indexed.Sort((a, b) => a.value.CompareTo(b.value));

            var ranks = new double[values.Count];
            var currentRank = 1;

            for (var i = 0; i < indexed.Count; i++)
            {
                if (i > 0 && indexed[i].value != indexed[i - 1].value)
                {
                    currentRank = i + 1;
                }

                var percentRank = (currentRank - 1) / (double)(values.Count - 1);
                ranks[indexed[i].index] = percentRank;
            }

            return ranks;
        }

        private void AssignRanks<T>(IReadOnlyList<T> items) where T : class
        {
            for (var i = 0; i < items.Count; i++)
            {
                switch (items[i])
                {
                    case PopularArtistListItem artist:
                        artist.rank = i + 1;
                        break;
                    case PopularYearListItem year:
                        year.rank = i + 1;
                        break;
                    default:
                        logger.LogWarning("Unknown list item type for rank assignment.");
                        break;
                }
            }
        }

        private class PopularityRow
        {
            public Guid uuid { get; set; }
            public long plays_30d { get; set; }
            public long plays_7d { get; set; }
            public long plays_48h { get; set; }
            public long plays_6h { get; set; }
            public long plays_90d { get; set; }
            public long seconds_30d { get; set; }
            public long seconds_7d { get; set; }
            public long seconds_48h { get; set; }
        }

        private class PopularityArtistRow
        {
            public Guid artist_uuid { get; set; }
            public string name { get; set; } = string.Empty;
            public long plays_30d { get; set; }
            public long plays_7d { get; set; }
            public long plays_48h { get; set; }
            public long plays_6h { get; set; }
            public long plays_90d { get; set; }
            public long seconds_30d { get; set; }
            public long seconds_7d { get; set; }
            public long seconds_48h { get; set; }
        }

        private class PopularityShowRow
        {
            public Guid show_uuid { get; set; }
            public long plays_30d { get; set; }
            public long plays_7d { get; set; }
            public long plays_48h { get; set; }
            public long plays_6h { get; set; }
            public long plays_90d { get; set; }
            public long seconds_30d { get; set; }
            public long seconds_7d { get; set; }
            public long seconds_48h { get; set; }
        }

        private class PopularityYearRow
        {
            public Guid year_uuid { get; set; }
            public string year { get; set; } = string.Empty;
            public Guid artist_uuid { get; set; }
            public string artist_name { get; set; } = string.Empty;
            public long plays_30d { get; set; }
            public long plays_7d { get; set; }
            public long plays_48h { get; set; }
            public long plays_6h { get; set; }
            public long plays_90d { get; set; }
            public long seconds_30d { get; set; }
            public long seconds_7d { get; set; }
            public long seconds_48h { get; set; }
        }
    }
}
