using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Hangfire;
using Hangfire.Console;
using Hangfire.RecurringJobExtensions;
using Hangfire.Server;
using Microsoft.Extensions.Configuration;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Import;
using Relisten.Services.Indexing;
using Relisten.Services.Popularity;
using Sentry;

namespace Relisten
{
    public class ScheduledService
    {
        private readonly ConcurrentDictionary<int, bool> artistsCurrentlySyncing = new();

        public ScheduledService(
            DbService db,
            ImporterService importerService,
            ArtistService artistService,
            ArchiveOrgArtistIndexer archiveOrgArtistIndexer,
            PopularityJobs popularityJobs,
            RedisService redisService,
            JerryGarciaComImporter jerryGarciaComImporter,
            IConfiguration config
        )
        {
            _db = db;
            _importerService = importerService;
            _artistService = artistService;
            _archiveOrgArtistIndexer = archiveOrgArtistIndexer;
            _popularityJobs = popularityJobs;
            _config = config;
            _redisService = redisService;
            _jerryGarciaComImporter = jerryGarciaComImporter;
        }

        private DbService _db { get; }
        private ImporterService _importerService { get; }
        private ArtistService _artistService { get; }
        private ArchiveOrgArtistIndexer _archiveOrgArtistIndexer { get; }
        private PopularityJobs _popularityJobs { get; }
        private IConfiguration _config { get; }
        private RedisService _redisService { get; }
        private JerryGarciaComImporter _jerryGarciaComImporter { get; }

        // run every day at 6:30 AM UTC to seed artists before the refresh
        [RecurringJob("30 6 * * *", Enabled = true)]
        [AutomaticRetry(Attempts = 0)]
        [Queue("artist_import")]
        [DisplayName("Index archive.org Artists")]
        public async Task IndexArchiveOrgArtists(PerformContext? context, bool allowedInDev = false)
        {
            if (!allowedInDev && _config["ASPNETCORE_ENVIRONMENT"] != "Production")
            {
                context?.WriteLine($"Not running in {_config["ASPNETCORE_ENVIRONMENT"]}");
                return;
            }

            await _archiveOrgArtistIndexer.IndexArtists(context);
        }

        // refresh 48h materialized view every hour
        [RecurringJob("15 * * * *", Enabled = true)]
        [AutomaticRetry(Attempts = 0)]
        [Queue("default")]
        [DisplayName("Refresh Materialized View: source_track_plays_by_hour_48h")]
        public async Task RefreshSourceTrackPlaysByHour48h(PerformContext? context, bool allowedInDev = false)
        {
            if (!allowedInDev && _config["ASPNETCORE_ENVIRONMENT"] != "Production")
            {
                context?.WriteLine($"Not running in {_config["ASPNETCORE_ENVIRONMENT"]}");
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            await _db.WithWriteConnection(con =>
                con.ExecuteAsync("REFRESH MATERIALIZED VIEW CONCURRENTLY source_track_plays_by_hour_48h",
                    commandTimeout: 900),
                longTimeout: true);
            stopwatch.Stop();
            context?.WriteLine($"Refreshed source_track_plays_by_hour_48h in {stopwatch.Elapsed.TotalSeconds:0.##}s");

            EnqueuePopularityRefreshes();
        }

        // refresh daily aggregates overnight
        [RecurringJob("10 5 * * *", Enabled = true)]
        [AutomaticRetry(Attempts = 0)]
        [Queue("default")]
        [DisplayName("Refresh Materialized View: source_track_plays_by_day_6mo")]
        public async Task RefreshSourceTrackPlaysByDay6mo(PerformContext? context, bool allowedInDev = false)
        {
            if (!allowedInDev && _config["ASPNETCORE_ENVIRONMENT"] != "Production")
            {
                context?.WriteLine($"Not running in {_config["ASPNETCORE_ENVIRONMENT"]}");
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            await _db.WithWriteConnection(con =>
                con.ExecuteAsync("REFRESH MATERIALIZED VIEW CONCURRENTLY source_track_plays_by_day_6mo",
                    commandTimeout: 900),
                longTimeout: true);
            stopwatch.Stop();
            context?.WriteLine($"Refreshed source_track_plays_by_day_6mo in {stopwatch.Elapsed.TotalSeconds:0.##}s");

            EnqueuePopularityRefreshes();
        }

        private void EnqueuePopularityRefreshes(int limit = 50)
        {
            BackgroundJob.Enqueue(() => _popularityJobs.RefreshArtistPopularityMap());
            BackgroundJob.Enqueue(() => _popularityJobs.RefreshPopularArtists(limit));
            BackgroundJob.Enqueue(() => _popularityJobs.RefreshTrendingArtists(limit));
            BackgroundJob.Enqueue(() => _popularityJobs.RefreshPopularShows(limit));
            BackgroundJob.Enqueue(() => _popularityJobs.RefreshTrendingShows(limit));
            BackgroundJob.Enqueue(() => _popularityJobs.RefreshPopularYears(limit));
            BackgroundJob.Enqueue(() => _popularityJobs.RefreshTrendingYears(limit));
        }

        // run every day at 3 AM EST, midnight PST, 7 AM UTC
        [RecurringJob("0 7 * * *", Enabled = true)]
        [AutomaticRetry(Attempts = 0)]
        [Queue("artist_import")]
        [DisplayName("Refresh All Artists")]
        public async Task RefreshAllArtists(PerformContext? context, bool allowedInDev = false)
        {
            if (!allowedInDev && _config["ASPNETCORE_ENVIRONMENT"] != "Production")
            {
                context?.WriteLine($"Not running in {_config["ASPNETCORE_ENVIRONMENT"]}");
                return;
            }

            var artists = (await _artistService.All()).ToList();

            context?.WriteLine($"--> Updating all {artists.Count} artists");

            foreach (var artist in artists)
            {
                context?.WriteLine($"--> Queueing {artist.name} ({artist.slug})");
                BackgroundJob.Enqueue(() => RefreshArtist(artist.slug, false, null));
            }

            context?.WriteLine("--> Queued updates for all artists! ");
        }

        [AutomaticRetry(Attempts = 0)]
        [Queue("artist_import")]
        [DisplayName("Rebuild All Artists Shows/Years")]
        public async Task RebuildAllArtists(PerformContext? context)
        {
            if (_config["ASPNETCORE_ENVIRONMENT"] != "Production")
            {
                context?.WriteLine($"Not running in {_config["ASPNETCORE_ENVIRONMENT"]}");
                return;
            }

            var artists = (await _artistService.All()).ToList();

            context?.WriteLine($"--> Rebuilding all {artists.Count} artists");
            var bar = context?.WriteProgressBar();

            Func<Artist, Task> rebuildArtist = async artist => { await _importerService.Rebuild(artist, context); };

            if (bar == null)
            {
                foreach (var artist in artists)
                {
                    await rebuildArtist(artist);
                }
            }
            else
            {
                await artists.AsyncForEachWithProgress(bar, rebuildArtist);
            }

            context?.WriteLine("--> Rebuilt all artists! ");
        }

        // this actually runs all of them without enqueing anything else
        [Queue("artist_import")]
        [AutomaticRetry(Attempts = 0)]
        private async Task __old(PerformContext? context)
        {
            var artists = (await _artistService.All()).ToList();

            context?.WriteLine($"--> Updating all {artists.Count} artists");

            var progress = context?.WriteProgressBar();

            var stats = new ImportStats();

            Func<Artist, Task> importArtist = async artist =>
            {
                context?.WriteLine($"--> Importing {artist.name}");

                var artistStats = await _importerService.Import(artist, null, context);

                context?.WriteLine($"--> Imported {artist.name}! " + artistStats);

                stats += artistStats;
            };

            if (progress == null)
            {
                foreach (var artist in artists)
                {
                    await importArtist(artist);
                }
            }
            else
            {
                await artists.AsyncForEachWithProgress(progress, importArtist);
            }

            context?.WriteLine("--> Imported all artists! " + stats);
        }

        [Queue("artist_import")]
        [DisplayName("Refresh Artist: {0}")]
        [AutomaticRetry(Attempts = 0)]
        public async Task RefreshArtist(string idOrSlug, bool deleteOldContent, PerformContext? ctx)
        {
            await RefreshArtist(idOrSlug, null, deleteOldContent, null, ctx);
        }

        [Queue("artist_import")]
        [DisplayName("Refresh from Phish.in")]
        [AutomaticRetry(Attempts = 0)]
        public async Task RefreshPhishFromPhishinOnly(PerformContext? ctx)
        {
            await RefreshArtist("phish", null,
                false, u => u.upstream_source_id == 3 /* phish.in */, ctx);
        }

        [Queue("artist_import")]
        [DisplayName("Refresh Artist Show: {0}, {1}, {2}")]
        [AutomaticRetry(Attempts = 0)]
        public async Task RefreshArtist(
            string idOrSlug,
            string? specificShowId,
            bool deleteOldContent,
            PerformContext? ctx
        )
        {
            await RefreshArtist(idOrSlug, specificShowId, deleteOldContent, null, ctx);
        }

        [Queue("artist_import")]
        [DisplayName("Refresh Artist Show: {0}, {1}, {2}")]
        [AutomaticRetry(Attempts = 0)]
        public async Task RefreshArtist(
            string idOrSlug,
            string? specificShowId,
            bool deleteOldContent,
            Func<ArtistUpstreamSource, bool>? filterUpstreamSources,
            PerformContext? ctx
        )
        {
            var artist = await _artistService.FindArtistWithIdOrSlug(idOrSlug);

            if (artist == null)
            {
                ctx?.WriteLine($"No artist found for {idOrSlug}");
                return;
            }

            if (artistsCurrentlySyncing.ContainsKey(artist.id))
            {
                ctx?.WriteLine($"Already syncing {artist.name}. Will not overlap.");
                return;
            }

            try
            {
                artistsCurrentlySyncing[artist.id] = true;

                if (deleteOldContent)
                {
                    ctx?.WriteLine("Removing content for " + artist.name);

                    var rows = await _artistService.RemoveAllContentForArtist(artist);

                    ctx?.WriteLine($"Removed {rows} rows!");
                }

                ImportStats artistStats;

                if (specificShowId != null)
                {
                    artistStats =
                        await _importerService.Import(artist, filterUpstreamSources, specificShowId, ctx);
                }
                else
                {
                    artistStats = await _importerService.Import(artist, filterUpstreamSources, ctx);
                }

                ctx?.WriteLine($"--> Imported {artist.name}! " + artistStats);
            }
            catch (Exception e)
            {
                ctx?.WriteLine($"Error processing {artist.name}:");
                ctx?.LogException(e);

                e.Data["artist"] = artist.name;

                SentrySdk.CaptureException(e);

                throw;
            }
            finally
            {
                artistsCurrentlySyncing.TryRemove(artist.id, out var _);
            }
        }

        [Queue("artist_import")]
        [DisplayName("Backfill JerryGarcia Venues: {0}")]
        [AutomaticRetry(Attempts = 0)]
        public async Task BackfillJerryGarciaVenues(string artistSlug, PerformContext? ctx)
        {
            var artist = await _artistService.FindArtistWithIdOrSlug(artistSlug);

            if (artist == null)
            {
                ctx?.WriteLine($"No artist found for {artistSlug}");
                return;
            }

            var src = artist.upstream_sources.FirstOrDefault(s =>
                s.upstream_source.name == JerryGarciaComImporter.DataSourceName);

            if (src == null)
            {
                ctx?.WriteLine($"No {JerryGarciaComImporter.DataSourceName} upstream source for {artist.name}");
                return;
            }

            var stats = await _jerryGarciaComImporter.BackfillVenuesForArtist(artist, src, ctx);

            ctx?.WriteLine($"Backfill complete for {artist.name}. {stats}");
        }
    }
}
