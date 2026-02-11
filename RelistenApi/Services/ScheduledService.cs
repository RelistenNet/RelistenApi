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
using Relisten.Services.Classification;
using Relisten.Services.Search;
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
            SearchIndexingService searchIndexingService,
            RecordingTypeClassifier recordingTypeClassifier,
            TrackSongMatcher trackSongMatcher,
            VenueCanonicalizer venueCanonicalizer,
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
            _searchIndexingService = searchIndexingService;
            _recordingTypeClassifier = recordingTypeClassifier;
            _trackSongMatcher = trackSongMatcher;
            _venueCanonicalizer = venueCanonicalizer;
        }

        private DbService _db { get; }
        private ImporterService _importerService { get; }
        private ArtistService _artistService { get; }
        private ArchiveOrgArtistIndexer _archiveOrgArtistIndexer { get; }
        private PopularityJobs _popularityJobs { get; }
        private IConfiguration _config { get; }
        private RedisService _redisService { get; }
        private JerryGarciaComImporter _jerryGarciaComImporter { get; }
        private SearchIndexingService _searchIndexingService { get; }
        private RecordingTypeClassifier _recordingTypeClassifier { get; }
        private TrackSongMatcher _trackSongMatcher { get; }
        private VenueCanonicalizer _venueCanonicalizer { get; }

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
        // [RecurringJob("15 * * * *", Enabled = true)]
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
        // [RecurringJob("10 5 * * *", Enabled = true)]
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

        // Run every 15 minutes to index new/updated sources for search
        [RecurringJob("*/15 * * * *", Enabled = true)]
        [AutomaticRetry(Attempts = 0)]
        [Queue("default")]
        [DisplayName("Index Search: Stale Sources")]
        public async Task IndexSearchStaleSources(PerformContext? context)
        {
            await _searchIndexingService.IndexStaleSourcesAsync(context);
        }

        [Queue("default")]
        [DisplayName("Index Search: Full Rebuild")]
        [AutomaticRetry(Attempts = 0)]
        public async Task IndexSearchFullRebuild(PerformContext? context)
        {
            await _searchIndexingService.IndexStaleSourcesAsync(context);
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

        // Run every 15 minutes to classify sources that don't have a recording type yet
        [RecurringJob("*/15 * * * *", Enabled = true)]
        [AutomaticRetry(Attempts = 0)]
        [Queue("default")]
        [DisplayName("Classify Recording Types: Unclassified Sources")]
        public async Task ClassifySourceRecordingTypes(PerformContext? context)
        {
            var stopwatch = Stopwatch.StartNew();

            // Find sources where recording_type is 'unknown' or has low confidence
            var unclassified = await _db.WithConnection(con =>
                con.QueryAsync<UnclassifiedSource>(@"
                    SELECT id, upstream_identifier, description, taper_notes,
                           source AS source_field, lineage, taper
                    FROM sources
                    WHERE recording_type = 'unknown'
                       OR (recording_type_confidence IS NOT NULL AND recording_type_confidence < 0.7
                           AND recording_type_verified = FALSE)
                    ORDER BY id
                    LIMIT 500
                ", commandTimeout: 120),
                longTimeout: true, readOnly: true
            );

            var sources = unclassified.ToList();
            context?.WriteLine($"Found {sources.Count} sources to classify");

            if (sources.Count == 0) return;

            var classified = 0;
            var llmCalls = 0;

            foreach (var batch in sources.Chunk(50))
            {
                foreach (var src in batch)
                {
                    var meta = new SourceMetadataForClassification
                    {
                        Identifier = src.upstream_identifier,
                        Source = src.source_field,
                        Lineage = src.lineage,
                        TaperNotes = src.taper_notes,
                        Description = src.description
                    };

                    var result = await _recordingTypeClassifier.ClassifyAsync(meta, allowLlm: true);

                    if (result.Method == "llm") llmCalls++;

                    var recordingTypeStr = result.RecordingType.ToDbString();

                    await _db.WithWriteConnection(con => con.ExecuteAsync(@"
                        UPDATE sources
                        SET recording_type = @recording_type,
                            recording_type_confidence = @confidence,
                            recording_type_method = @method,
                            is_soundboard = @is_soundboard,
                            updated_at = NOW()
                        WHERE id = @id
                    ", new
                    {
                        id = src.id,
                        recording_type = recordingTypeStr,
                        confidence = result.Confidence,
                        method = result.Method,
                        is_soundboard = result.RecordingType == RecordingType.Soundboard
                    }));

                    classified++;
                }

                // Rate limiting for LLM calls
                if (llmCalls > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500));
                }
            }

            stopwatch.Stop();
            context?.WriteLine(
                $"Classified {classified} sources ({llmCalls} LLM calls) in {stopwatch.Elapsed.TotalSeconds:0.#}s");
        }

        private class UnclassifiedSource
        {
            public long id { get; set; }
            public string upstream_identifier { get; set; } = "";
            public string? description { get; set; }
            public string? taper_notes { get; set; }
            public string? source_field { get; set; }
            public string? lineage { get; set; }
            public string? taper { get; set; }
        }

        // Run every 30 minutes to match unmatched tracks to canonical songs
        [RecurringJob("*/30 * * * *", Enabled = true)]
        [AutomaticRetry(Attempts = 0)]
        [Queue("default")]
        [DisplayName("Match Tracks to Songs: Unmatched Tracks")]
        public async Task MatchTracksToSongs(PerformContext? context)
        {
            var stopwatch = Stopwatch.StartNew();

            // Find artists that have both setlist songs and unmatched tracks
            var artistsWithUnmatched = await _db.WithConnection(con =>
                con.QueryAsync<ArtistWithUnmatchedCount>(@"
                    SELECT DISTINCT t.artist_id, COUNT(*) as unmatched_count
                    FROM source_tracks t
                    WHERE t.matched_song_id IS NULL
                      AND t.match_method IS NULL
                      AND t.is_orphaned = FALSE
                      AND EXISTS (
                          SELECT 1 FROM setlist_songs ss WHERE ss.artist_id = t.artist_id
                      )
                    GROUP BY t.artist_id
                    ORDER BY unmatched_count DESC
                    LIMIT 10
                ", commandTimeout: 120),
                longTimeout: true, readOnly: true
            );

            var artists = artistsWithUnmatched.ToList();
            context?.WriteLine($"Found {artists.Count} artists with unmatched tracks");

            if (artists.Count == 0) return;

            var totalMatched = 0;
            var totalProcessed = 0;

            foreach (var artist in artists)
            {
                // Load unmatched tracks for this artist (batch of 500)
                var tracks = (await _db.WithConnection(con =>
                    con.QueryAsync<SourceTrack>(@"
                        SELECT t.*, a.uuid as artist_uuid,
                               src.uuid as source_uuid, ss.uuid as source_set_uuid,
                               sh.uuid as show_uuid
                        FROM source_tracks t
                        JOIN sources src ON src.id = t.source_id
                        JOIN source_sets ss ON ss.id = t.source_set_id
                        JOIN artists a ON a.id = t.artist_id
                        JOIN shows sh ON sh.id = src.show_id
                        WHERE t.artist_id = @artistId
                          AND t.matched_song_id IS NULL
                          AND t.match_method IS NULL
                          AND t.is_orphaned = FALSE
                        ORDER BY t.id
                        LIMIT 500
                    ", new { artistId = artist.artist_id },
                    commandTimeout: 120),
                    longTimeout: true, readOnly: true
                )).ToList();

                if (tracks.Count == 0) continue;

                context?.WriteLine($"Processing {tracks.Count} tracks for artist {artist.artist_id}");

                // Match without LLM for backfill (rules + fuzzy only for speed)
                var results = await _trackSongMatcher.MatchTracksAsync(
                    artist.artist_id, tracks, allowLlm: false);

                await _trackSongMatcher.PersistMatchResults(results);

                var matched = results.Count(r => r.PrimaryMatchSongId.HasValue);
                totalMatched += matched;
                totalProcessed += results.Count;

                context?.WriteLine(
                    $"Artist {artist.artist_id}: {matched}/{results.Count} tracks matched");
            }

            stopwatch.Stop();
            context?.WriteLine(
                $"Track matching complete: {totalMatched}/{totalProcessed} matched in {stopwatch.Elapsed.TotalSeconds:0.#}s");
        }

        private class ArtistWithUnmatchedCount
        {
            public int artist_id { get; set; }
            public int unmatched_count { get; set; }
        }

        // Run daily to canonicalize venues that haven't been linked yet
        [RecurringJob("0 8 * * *", Enabled = true)]
        [AutomaticRetry(Attempts = 0)]
        [Queue("default")]
        [DisplayName("Canonicalize Venues: Unlinked Venues")]
        public async Task CanonicalizeVenues(PerformContext? context)
        {
            var stopwatch = Stopwatch.StartNew();

            context?.WriteLine("Starting venue canonicalization...");
            context?.WriteLine($"Batch size: 1000, LLM: disabled");

            await _venueCanonicalizer.CanonicalizeVenuesAsync(batchSize: 10000, allowLlm: false);

            stopwatch.Stop();
            context?.WriteLine($"Venue canonicalization complete in {stopwatch.Elapsed.TotalSeconds:0.#}s");
        }
    }
}
