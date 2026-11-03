using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;
using Relisten.Data;

namespace Relisten.Services.Live
{
    /// <summary>
    /// Builds GET /api/v3/live/summary: plays in a recent window grouped in Postgres,
    /// ranked by <see cref="LiveSummaryRanking"/>, with each listed show's representative
    /// source attached. Responses are cached in Redis once per refresh interval.
    /// </summary>
    public class LiveSummaryService
    {
        private readonly SourceTrackPlaysService sourceTrackPlaysService;
        private readonly SourceService sourceService;
        private readonly RedisService redis;
        private readonly ILogger<LiveSummaryService> logger;

        public LiveSummaryService(
            SourceTrackPlaysService sourceTrackPlaysService,
            SourceService sourceService,
            RedisService redis,
            ILogger<LiveSummaryService> logger
        )
        {
            this.sourceTrackPlaysService = sourceTrackPlaysService;
            this.sourceService = sourceService;
            this.redis = redis;
            this.logger = logger;
        }

        /// <summary>
        /// The serialized summary. Every request in the same refresh interval shares one
        /// cache entry, because the key includes the window end. Redis is an optimization
        /// only: when it fails, the summary is computed and served uncached.
        /// </summary>
        public async Task<string> SummaryJson(
            LiveSummaryRequest request,
            LiveWindow window,
            DateTime generatedAt
        )
        {
            var cacheKey = request.CacheKey(window);
            var cached = await ReadCache(cacheKey);
            if (cached != null)
            {
                return cached;
            }

            var summary = await BuildSummary(request, window, generatedAt);
            var json = JsonConvert.SerializeObject(
                summary,
                RelistenApiJsonOptionsWrapper.ApiV3SerializerSettings
            );

            await WriteCache(cacheKey, json);
            return json;
        }

        public async Task<LiveSummary> BuildSummary(
            LiveSummaryRequest request,
            LiveWindow window,
            DateTime generatedAt
        )
        {
            var plays = await sourceTrackPlaysService.PlaysInLiveWindow(window);

            var scoredShows = LiveSummaryRanking.ScoreShows(plays.Shows, request, window);
            var topShows = LiveSummaryRanking.TopShows(scoredShows, request);
            var deepCuts = LiveSummaryRanking.DeepCuts(scoredShows, request);
            var fresh = LiveSummaryRanking.Fresh(scoredShows, request, window);
            var topArtists = LiveSummaryRanking.TopArtists(plays.Artists, request);
            var onThisDay = LiveSummaryRanking.OnThisDay(scoredShows, request.MonthDay);
            // Artists and shows come from separate statements, each with its own snapshot,
            // so an artist can in principle lack show rows; it then has no top_show.
            var allTopShowsByArtist = LiveSummaryRanking.TopShowPerArtist(scoredShows);
            var artistTopShows = topArtists
                .Where(artist => allTopShowsByArtist.ContainsKey(artist.Row.artist_id))
                .Select(artist => allTopShowsByArtist[artist.Row.artist_id])
                .ToList();

            var sources = await RepresentativeSources(
                topShows.Concat(deepCuts).Concat(fresh).Concat(artistTopShows)
            );

            var showDtos = ToShowDtos<LiveSummaryShow>(topShows, sources);
            var topShowDtosByArtist = artistTopShows
                .Where(show => sources.ContainsKey(show.Row.source_id))
                .ToDictionary(
                    show => show.Row.artist_id,
                    show => ToShowDto<LiveSummaryShow>(show, sources[show.Row.source_id])
                );
            var totalPlays = plays.AppTypes.Sum(appType => appType.plays);

            return new LiveSummary
            {
                generated_at = generatedAt,
                window_minutes = window.Minutes,
                window_start = window.Start,
                window_end = window.End,
                last_play_id =
                    plays.Shows.Count == 0 ? null : plays.Shows.Max(show => show.last_play_id),
                total_plays = totalPlays,
                listened_seconds = plays.Shows.Sum(show => show.listened_seconds),
                app_types = plays.AppTypes.Select(ToAppTypeDto).ToList(),
                timeline = plays.Timeline.Select(ToTimelineDto).ToList(),
                artist_count = plays.Artists.Count,
                other_artists_plays = totalPlays - topArtists.Sum(artist => artist.Row.plays),
                shows = showDtos,
                artists = topArtists
                    .Select(artist =>
                        ToArtistDto(
                            artist,
                            totalPlays,
                            topShowDtosByArtist.GetValueOrDefault(artist.Row.artist_id)
                        )
                    )
                    .ToList(),
                deep_cuts = ToShowDtos<LiveSummaryShow>(deepCuts, sources),
                fresh = ToShowDtos<LiveSummaryFreshShow>(fresh, sources),
                on_this_day = ToOnThisDayDto(onThisDay, showDtos, request.MonthDay),
            };
        }

        private async Task<
            IReadOnlyDictionary<long, SlimSourceWithShowVenueAndArtist>
        > RepresentativeSources(IEnumerable<ScoredShow> listedShows)
        {
            var sourceIds = listedShows.Select(show => (int)show.Row.source_id).Distinct().ToList();
            if (sourceIds.Count == 0)
            {
                return new Dictionary<long, SlimSourceWithShowVenueAndArtist>();
            }

            // The source lookup takes int ids; sources.id is bigint, but every id fits.
            var sources = await sourceService.SlimSourceWithShowAndArtistForIds(sourceIds);
            return sources.ToDictionary(source => (long)source.id);
        }

        // A show whose source was deleted after the window was queried is left out of the
        // show lists, the same way live/history drops plays it can no longer resolve. Its
        // plays still count in the totals and on_this_day, which the window query already
        // computed; the race is rare and only moves those counts by one show.
        private static IReadOnlyList<TShow> ToShowDtos<TShow>(
            IEnumerable<ScoredShow> shows,
            IReadOnlyDictionary<long, SlimSourceWithShowVenueAndArtist> sources
        )
            where TShow : LiveSummaryShow, new()
        {
            return shows
                .Where(show => sources.ContainsKey(show.Row.source_id))
                .Select(show => ToShowDto<TShow>(show, sources[show.Row.source_id]))
                .ToList();
        }

        private static TShow ToShowDto<TShow>(
            ScoredShow show,
            SlimSourceWithShowVenueAndArtist source
        )
            where TShow : LiveSummaryShow, new()
        {
            var row = show.Row;

            return new TShow
            {
                show_uuid = row.show_uuid,
                plays = row.plays,
                listened_seconds = row.listened_seconds,
                plays_30d = row.plays_30d,
                expected_plays = Math.Round(show.ExpectedPlays, 3),
                surprise = Math.Round(show.Surprise, 3),
                is_anniversary = show.IsAnniversary,
                source_created_at = row.source_created_at,
                source_count = row.source_count,
                first_played_at = row.first_played_at,
                last_played_at = row.last_played_at,
                source = source,
                latest_track = new LiveSummaryTrack
                {
                    uuid = row.track_uuid,
                    title = row.track_title,
                    slug = row.track_slug,
                    track_position = row.track_position,
                    duration = row.track_duration,
                    played_at = row.track_played_at,
                },
                timeline = row.timeline,
            };
        }

        private static LiveSummaryAppTypePlays ToAppTypeDto(LiveAppTypeRow row) =>
            new() { app_type = row.app_type, plays = row.plays };

        private static LiveSummaryTimelineBucket ToTimelineDto(LiveTimelineRow row) =>
            new() { bucket_start = row.bucket_start, plays = row.plays };

        private static LiveSummaryOnThisDay ToOnThisDayDto(
            IReadOnlyList<ScoredShow> onThisDay,
            IReadOnlyList<LiveSummaryShow> showDtos,
            string monthDay
        )
        {
            var matchingUuids = onThisDay.Select(show => show.Row.show_uuid).ToHashSet();

            return new LiveSummaryOnThisDay
            {
                month_day = monthDay,
                plays = onThisDay.Sum(show => show.Row.plays),
                show_count = onThisDay.Count,
                show_uuids = showDtos
                    .Select(show => show.show_uuid)
                    .Where(matchingUuids.Contains)
                    .ToList(),
            };
        }

        private static LiveSummaryArtist ToArtistDto(
            ScoredArtist artist,
            long totalPlays,
            LiveSummaryShow? topShow
        )
        {
            var row = artist.Row;

            return new LiveSummaryArtist
            {
                artist_uuid = row.artist_uuid,
                name = row.artist_name,
                slug = row.artist_slug,
                plays = row.plays,
                listened_seconds = row.listened_seconds,
                show_count = row.show_count,
                top_show = topShow,
                share = totalPlays == 0 ? 0 : Math.Round((double)row.plays / totalPlays, 4),
                expected_plays = Math.Round(row.expected_plays, 3),
                surprise = Math.Round(artist.Surprise, 3),
            };
        }

        private async Task<string?> ReadCache(string cacheKey)
        {
            try
            {
                var cached = await redis.db.StringGetAsync(cacheKey);
                return cached.HasValue && !cached.IsNullOrEmpty ? cached.ToString() : null;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Live summary cache read failed for {CacheKey}", cacheKey);
                return null;
            }
        }

        // The key includes the window end, so an entry is never served after its window
        // is replaced. The TTL only has to outlast the interval, including a summary that
        // finishes computing just after the boundary, before Redis evicts it.
        private async Task WriteCache(string cacheKey, string json)
        {
            try
            {
                await redis.db.StringSetAsync(cacheKey, json, LiveWindow.RefreshInterval * 2);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Live summary cache write failed for {CacheKey}", cacheKey);
            }
        }
    }
}
