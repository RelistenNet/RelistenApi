﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Console;
using Hangfire.RecurringJobExtensions;
using Hangfire.Server;
using Microsoft.Extensions.Configuration;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Import;
using Sentry;

namespace Relisten
{
    public class ScheduledService
    {
        private readonly ConcurrentDictionary<int, bool> artistsCurrentlySyncing = new();

        public ScheduledService(
            ImporterService importerService,
            ArtistService artistService,
            RedisService redisService,
            IConfiguration config
        )
        {
            _importerService = importerService;
            _artistService = artistService;
            _config = config;
            _redisService = redisService;
        }

        private ImporterService _importerService { get; }
        private ArtistService _artistService { get; }
        private IConfiguration _config { get; }
        private RedisService _redisService { get; }

        // run every day at 3 AM EST, midnight PST, 7 AM UTC
        [RecurringJob("0 7 * * *", Enabled = true)]
        [AutomaticRetry(Attempts = 0)]
        [Queue("artist_import")]
        [DisplayName("Refresh All Artists")]
        public async Task RefreshAllArtists(PerformContext context)
        {
            if (_config["ASPNETCORE_ENVIRONMENT"] != "Production")
            {
                context.WriteLine($"Not running in {_config["ASPNETCORE_ENVIRONMENT"]}");
                return;
            }

            var artists = (await _artistService.All()).ToList();

            context.WriteLine($"--> Updating all {artists.Count} artists");

            foreach (var artist in artists)
            {
                context.WriteLine($"--> Queueing {artist.name} ({artist.slug})");
                BackgroundJob.Enqueue(() => RefreshArtist(artist.slug, false, null));
            }

            context.WriteLine("--> Queued updates for all artists! ");
        }

        [AutomaticRetry(Attempts = 0)]
        [Queue("artist_import")]
        [DisplayName("Rebuild All Artists Shows/Years")]
        public async Task RebuildAllArtists(PerformContext context)
        {
            if (_config["ASPNETCORE_ENVIRONMENT"] != "Production")
            {
                context.WriteLine($"Not running in {_config["ASPNETCORE_ENVIRONMENT"]}");
                return;
            }

            var artists = (await _artistService.All()).ToList();

            context.WriteLine($"--> Rebuilding all {artists.Count} artists");
            var bar = context.WriteProgressBar();

            await artists.AsyncForEachWithProgress(bar, async artist =>
            {
                await _importerService.Rebuild(artist, context);
            });

            context.WriteLine("--> Rebuilt all artists! ");
        }

        // this actually runs all of them without enqueing anything else
        [Queue("artist_import")]
        [AutomaticRetry(Attempts = 0)]
        private async Task __old(PerformContext context)
        {
            var artists = (await _artistService.All()).ToList();

            context.WriteLine($"--> Updating all {artists.Count} artists");

            var progress = context.WriteProgressBar();

            var stats = new ImportStats();

            await artists.AsyncForEachWithProgress(progress, async artist =>
            {
                context.WriteLine($"--> Importing {artist.name}");

                var artistStats = await _importerService.Import(artist, null, context);

                context.WriteLine($"--> Imported {artist.name}! " + artistStats);

                stats += artistStats;
            });

            context.WriteLine("--> Imported all artists! " + stats);
        }

        [Queue("artist_import")]
        [DisplayName("Refresh Artist: {0}")]
        [AutomaticRetry(Attempts = 0)]
        public async Task RefreshArtist(string idOrSlug, bool deleteOldContent, PerformContext ctx)
        {
            await RefreshArtist(idOrSlug, null, deleteOldContent, null, ctx);
        }

        [Queue("artist_import")]
        [DisplayName("Refresh from Phish.in")]
        [AutomaticRetry(Attempts = 0)]
        public async Task RefreshPhishFromPhishinOnly(PerformContext ctx)
        {
            await RefreshArtist("phish", null,
                false, u => u.upstream_source_id == 3 /* phish.in */, ctx);
        }

        [Queue("artist_import")]
        [DisplayName("Refresh Artist Show: {0}, {1}, {2}")]
        [AutomaticRetry(Attempts = 0)]
        public async Task RefreshArtist(
            string idOrSlug,
            string specificShowId,
            bool deleteOldContent,
            PerformContext ctx
        )
        {
            await RefreshArtist(idOrSlug, specificShowId, deleteOldContent, null, ctx);
        }

        [Queue("artist_import")]
        [DisplayName("Refresh Artist Show: {0}, {1}, {2}")]
        [AutomaticRetry(Attempts = 0)]
        public async Task RefreshArtist(
            string idOrSlug,
            string specificShowId,
            bool deleteOldContent,
            Func<ArtistUpstreamSource, bool> filterUpstreamSources,
            PerformContext ctx
        )
        {
            var artist = await _artistService.FindArtistWithIdOrSlug(idOrSlug);

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
    }
}
