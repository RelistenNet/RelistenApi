using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Transactions;
using Hangfire.Console;
using Hangfire.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Ingestion;
using Relisten.Vendor;
using Relisten.Vendor.Phishin;
using Sentry;

namespace Relisten.Import
{
    public partial class PhishinImporter : ImporterBase
    {
        private readonly LinkService linkService;
        private readonly ShowPerformanceService performances;

        private readonly IDictionary<string, Era?> yearToEraMapping =
            new Dictionary<string, Era?>();
        private IDictionary<string, Era?> existingEras = new Dictionary<string, Era?>();
        private IDictionary<string, SetlistShow?> existingSetlistShows =
            new Dictionary<string, SetlistShow?>();
        private IDictionary<string, SetlistSong?> existingSetlistSongs =
            new Dictionary<string, SetlistSong?>();

        private IDictionary<string, Source?> existingSources = new Dictionary<string, Source?>();
        private IDictionary<string, Tour?> existingTours = new Dictionary<string, Tour?>();
        private IDictionary<string, VenueWithShowCount?> existingVenues =
            new Dictionary<string, VenueWithShowCount?>();

        public PhishinImporter(
            CatalogRebuildService catalog,
            VenueService venueService,
            TourService tourService,
            SourceService sourceService,
            SourceSetService sourceSetService,
            SourceReviewService sourceReviewService,
            SourceTrackService sourceTrackService,
            SetlistSongService setlistSongService,
            LinkService linkService,
            SetlistShowService setlistShowService,
            EraService eraService,
            ILogger<PhishinImporter> log,
            IConfiguration configuration,
            ShowPerformanceService performances
        )
            : base(catalog, log)
        {
            this.linkService = linkService;
            this.performances = performances;
            _setlistSongService = setlistSongService;
            _setlistShowService = setlistShowService;
            _sourceService = sourceService;
            _venueService = venueService;
            _tourService = tourService;
            _log = log;
            _configuration = configuration;
            _sourceReviewService = sourceReviewService;
            _sourceTrackService = sourceTrackService;
            _sourceSetService = sourceSetService;
            _eraService = eraService;
            _configuration = configuration;

            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                configuration["PHISHIN_KEY"]!
            );
        }

        protected SourceService _sourceService { get; set; }
        protected SourceSetService _sourceSetService { get; set; }
        protected SourceReviewService _sourceReviewService { get; set; }
        protected SourceTrackService _sourceTrackService { get; set; }
        protected VenueService _venueService { get; set; }
        protected TourService _tourService { get; set; }
        protected EraService _eraService { get; set; }
        protected SetlistSongService _setlistSongService { get; set; }
        protected SetlistShowService _setlistShowService { get; set; }
        protected ILogger<PhishinImporter> _log { get; set; }
        public IConfiguration _configuration { get; }

        public override UpstreamProvider Provider => UpstreamProvider.Phishin;

        public override ImportableData ImportableDataForArtist(Artist artist)
        {
            return ImportableData.Sources
                | ImportableData.Venues
                | ImportableData.Tours
                | ImportableData.Eras
                | ImportableData.SetlistShowsAndSongs;
        }

        public override async Task<ImportStats> ImportDataForArtist(
            Artist artist,
            ArtistUpstreamSource src,
            PerformContext? ctx
        )
        {
            await PreloadData(artist);

            var stats = new ImportStats();

            ImportProgress.WriteLine(log, ctx, "Processing Eras");
            stats += await ProcessEras(artist, ctx);

            ImportProgress.WriteLine(log, ctx, "Processing Tours");
            stats += await ProcessTours(artist, ctx);

            ImportProgress.WriteLine(log, ctx, "Processing Songs");
            stats += await ProcessSongs(artist, ctx);

            ImportProgress.WriteLine(log, ctx, "Processing Venues");
            stats += await ProcessVenues(artist, ctx);

            ImportProgress.WriteLine(log, ctx, "Processing Shows");
            stats += await ProcessShows(artist, src, ctx);

            ImportProgress.WriteLine(log, ctx, $"Import stats: {stats}");

            if (stats.Created > 0 || stats.Updated > 0 || stats.Removed > 0)
            {
                ImportProgress.WriteLine(log, ctx, "Rebuilding shows and years...");
                await RebuildShows(artist);
                await RebuildYears(artist);
                ImportProgress.WriteLine(log, ctx, "--> rebuilt!");
            }
            else
            {
                ImportProgress.WriteLine(
                    log,
                    ctx,
                    "No changes detected, skipping show/year rebuild."
                );
            }

            return stats;
        }

        public override Task<ImportStats> ImportSpecificShowDataForArtist(
            Artist artist,
            ArtistUpstreamSource src,
            string? showIdentifier,
            PerformContext? ctx
        )
        {
            return Task.FromResult(new ImportStats());
        }

        private async Task PreloadData(Artist artist)
        {
            // Use primary database to ensure read-after-write consistency during full refresh
            existingSources = (await _sourceService.AllForArtistFromPrimary(artist))
                .GroupBy(venue => venue.upstream_identifier)
                .ToDictionary(grp => grp.Key, grp => (Source?)grp.First());

            existingEras = (await _eraService.AllForArtist(artist))
                .GroupBy(era => era.name)
                .ToDictionary(grp => grp.Key, grp => (Era?)grp.First());

            existingVenues = (await _venueService.AllIncludingUnusedForArtist(artist))
                .GroupBy(venue => venue.upstream_identifier)
                .ToDictionary(grp => grp.Key, grp => (VenueWithShowCount?)grp.First());

            existingTours = (await _tourService.AllForArtist(artist))
                .GroupBy(tour => tour.upstream_identifier)
                .ToDictionary(grp => grp.Key, grp => (Tour?)grp.First());

            existingSetlistShows = (await _setlistShowService.AllForArtist(artist))
                .GroupBy(show => show.upstream_identifier)
                .ToDictionary(grp => grp.Key, grp => (SetlistShow?)grp.First());

            existingSetlistSongs = (await _setlistSongService.AllForArtist(artist))
                .GroupBy(song => song.upstream_identifier)
                .ToDictionary(grp => grp.Key, grp => (SetlistSong?)grp.First());
        }

        private string PhishinApiUrl(
            string api,
            string? sort_attr = null,
            int per_page = 99999,
            int? page = null
        )
        {
            // Phish.in ignores sorting unless both parameters are supplied. Show
            // pagination relies on ascending dates to stop at the thin-scrape cutoff.
            var sorting = sort_attr == null ? "" : $"&sort_attr={sort_attr}&sort_dir=asc";

            return $"https://phish.in/api/v1/{api}.json?per_page={per_page}{sorting}{(page != null ? "&page=" + page.Value : "")}";
        }

        private async Task<PhishinRootObject<T>> PhishinApiRequest<T>(
            string apiRoute,
            PerformContext? ctx,
            string? sort_attr = null,
            int per_page = 99999,
            int? page = null
        )
        {
            var url = PhishinApiUrl(apiRoute, sort_attr, per_page, page);
            ImportProgress.WriteLine(log, ctx, $"Requesting {url}");
            using var resp = await http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            var result = JsonConvert.DeserializeObject<PhishinRootObject<T>>(
                await resp.Content.ReadAsStringAsync()
            );
            if (result == null || !result.success || result.data == null)
                throw new System.IO.InvalidDataException("Phish.in returned an invalid response.");
            return result;
        }

        public async Task<ImportStats> ProcessTours(
            Artist artist,
            PerformContext? ctx,
            IEnumerable<PhishinSmallTour>? staged = null
        )
        {
            var stats = new ImportStats();

            foreach (
                var tour in (
                    staged
                    ?? (await PhishinApiRequest<IEnumerable<PhishinSmallTour>>("tours", ctx)).data
                )
            )
            {
                Tour? dbTour = existingTours.GetValue(tour.id.ToString());

                if (dbTour == null)
                {
                    dbTour = await _tourService.Save(
                        new Tour
                        {
                            updated_at = tour.updated_at,
                            artist_id = artist.id,
                            start_date = DateTime.Parse(tour.starts_on),
                            end_date = DateTime.Parse(tour.ends_on),
                            name = tour.name,
                            slug = Slugify(tour.name),
                            upstream_identifier = tour.id.ToString(),
                        }
                    );

                    existingTours[dbTour.upstream_identifier] = dbTour!;

                    stats.Created++;
                }
                else if (tour.updated_at > dbTour.updated_at)
                {
                    dbTour.start_date = DateTime.Parse(tour.starts_on);
                    dbTour.end_date = DateTime.Parse(tour.ends_on);
                    dbTour.name = tour.name;

                    dbTour = await _tourService.Save(dbTour);

                    existingTours[dbTour.upstream_identifier] = dbTour!;

                    stats.Updated++;
                }
            }

            return stats;
        }

        public async Task<ImportStats> ProcessEras(
            Artist artist,
            PerformContext? ctx,
            IDictionary<string, IList<string>>? staged = null
        )
        {
            var stats = new ImportStats();

            var order = 0;

            foreach (
                var era in (
                    staged
                    ?? (
                        await PhishinApiRequest<IDictionary<string, IList<string>>>("eras", ctx)
                    ).data
                )
            )
            {
                Era? dbEra = existingEras.GetValue(era.Key);

                if (dbEra == null)
                {
                    dbEra = await _eraService.Save(
                        new Era
                        {
                            artist_id = artist.id,
                            name = era.Key,
                            order = order,
                            updated_at = DateTime.Now,
                        }
                    );

                    existingEras[dbEra.name] = dbEra;

                    stats.Created++;
                }

                foreach (var year in era.Value)
                {
                    yearToEraMapping[year] = dbEra!;
                }

                order++;
            }

            return stats;
        }

        public async Task<ImportStats> ProcessSongs(
            Artist artist,
            PerformContext? ctx,
            IEnumerable<PhishinSmallSong>? staged = null
        )
        {
            var stats = new ImportStats();

            var songsToSave = new List<SetlistSong>();
            var songs = (
                staged
                ?? (await PhishinApiRequest<IEnumerable<PhishinSmallSong>>("songs", ctx)).data
            );

            foreach (var song in songs)
            {
                SetlistSong? dbSong = existingSetlistSongs.GetValue(song.id.ToString());

                // skip aliases for now
                if (dbSong == null && song.alias_for.HasValue == false)
                {
                    songsToSave.Add(
                        new SetlistSong
                        {
                            updated_at = song.updated_at,
                            artist_id = artist.id,
                            name = song.title,
                            slug = song.slug,
                            upstream_identifier = song.id.ToString(),
                        }
                    );
                }
            }

            var groupedBySlug = songsToSave.GroupBy(s => s.slug).ToList();
            var deduped = groupedBySlug.Select(g => g.First()).ToList();

            var newSongs = await _setlistSongService.InsertAll(artist, deduped);

            var newSongsBySlug = newSongs.ToDictionary(s => s.slug);
            foreach (var group in groupedBySlug)
            {
                if (newSongsBySlug.TryGetValue(group.Key, out var dbSong))
                {
                    foreach (var song in group)
                    {
                        existingSetlistSongs[song.upstream_identifier] = dbSong;
                    }
                }
            }

            stats.Created += newSongs.Count();

            return stats;
        }

        public async Task<ImportStats> ProcessVenues(
            Artist artist,
            PerformContext? ctx,
            IEnumerable<PhishinSmallVenue>? staged = null
        )
        {
            var stats = new ImportStats();

            foreach (
                var venue in (
                    staged
                    ?? (await PhishinApiRequest<IEnumerable<PhishinSmallVenue>>("venues", ctx)).data
                )
            )
            {
                VenueWithShowCount? dbVenue = existingVenues.GetValue(venue.id.ToString());

                if (dbVenue == null)
                {
                    var sc = new VenueWithShowCount
                    {
                        updated_at = venue.updated_at,
                        artist_id = artist.id,
                        name = venue.name,
                        location = venue.location,
                        slug = Slugify(venue.name),
                        latitude = venue.latitude,
                        longitude = venue.longitude,
                        past_names = venue.past_names,
                        upstream_identifier = venue.id.ToString(),
                    };

                    var createdDb = await _venueService.Save(sc);

                    sc.id = createdDb.id;

                    existingVenues[sc.upstream_identifier] = sc;

                    stats.Created++;

                    dbVenue = sc;
                }
                else if (venue.updated_at > dbVenue.updated_at)
                {
                    dbVenue.name = venue.name;
                    dbVenue.location = venue.location;
                    dbVenue.longitude = venue.longitude;
                    dbVenue.latitude = venue.latitude;
                    dbVenue.past_names = venue.past_names;
                    dbVenue.updated_at = venue.updated_at;

                    await _venueService.Save(dbVenue);

                    existingVenues[dbVenue.upstream_identifier] = dbVenue!;

                    stats.Updated++;
                }
            }

            return stats;
        }

        private Dictionary<string, int> BuildSetIndexMap(IEnumerable<PhishinShowTrack> tracks)
        {
            var map = new Dictionary<string, int>();
            var nextIndex = 0;

            foreach (var track in tracks)
            {
                if (!map.ContainsKey(track.set))
                {
                    map[track.set] = nextIndex++;
                }
            }

            return map;
        }

        private async Task ProcessSetlistShow(
            ImportStats stats,
            PhishinShow show,
            Artist artist,
            Source dbSource
        )
        {
            var identifier = show.id.ToString(CultureInfo.InvariantCulture);
            SetlistShow? dbShow = existingSetlistShows.GetValue(identifier);
            var addSongs = false;

            if (dbShow == null)
            {
                dbShow = await _setlistShowService.Save(
                    new SetlistShow
                    {
                        artist_id = artist.id,
                        upstream_identifier = identifier,
                        date = DateTime.Parse(show.date),
                        venue_id = existingVenues[show.venue.id.ToString()]!.id,
                        tour_id = existingTours[show.tour_id.ToString()]!.id,
                        era_id = yearToEraMapping
                            .GetValue(show.date.Substring(0, 4), yearToEraMapping["1983-1987"]!)!
                            .id,
                        updated_at = dbSource.updated_at,
                    }
                );

                stats.Created++;

                addSongs = true;
            }
            else if (show.updated_at > dbShow.updated_at)
            {
                dbShow.date = DateTime.Parse(show.date);
                dbShow.venue_id = existingVenues[show.venue.id.ToString()]!.id;
                dbShow.tour_id = existingTours[show.tour_id.ToString()]!.id;
                dbShow.era_id = yearToEraMapping
                    .GetValue(show.date.Substring(0, 4), yearToEraMapping["1983-1987"]!)!
                    .id;
                dbShow.updated_at = dbSource.updated_at;

                dbShow = await _setlistShowService.Save(dbShow);

                stats.Updated++;

                addSongs = true;
            }

            existingSetlistShows[identifier] = dbShow;

            if (addSongs)
            {
                var dbSongs = show
                    .tracks.SelectMany(phishinTrack =>
                        phishinTrack.song_ids.Select(song_id =>
                            existingSetlistSongs.GetValue(song_id.ToString())
                        )
                    )
                    .Where(t => t != null)
                    .Select(t => t!)
                    .GroupBy(t => t.upstream_identifier)
                    .Select(g => g.First())
                    .ToList();

                stats += await _setlistShowService.UpdateSongPlays(dbShow!, dbSongs);
            }
        }

        private async Task UpgradeLegacySetlistKey(
            ImportStats stats,
            Source? source,
            int providerShowId
        )
        {
            var identifier = providerShowId.ToString(CultureInfo.InvariantCulture);
            if (source == null || existingSetlistShows.ContainsKey(identifier))
            {
                return;
            }

            var legacyRecordings = existingSources
                .Values.Where(recording => recording?.display_date == source.display_date)
                .ToArray();
            if (
                legacyRecordings.Length != 1
                || legacyRecordings[0]!.id != source.id
                || !existingSetlistShows.Remove(source.display_date, out var legacy)
            )
            {
                return;
            }

            // Match the old recording date before applying corrections. A unique recording
            // supports retaining the legacy setlist identity; collapsed dates need review.
            legacy!.upstream_identifier = identifier;
            existingSetlistShows[identifier] = await _setlistShowService.Save(legacy);
            stats.Updated++;
        }

        private async Task<Source> ProcessShow(
            ImportStats stats,
            Artist artist,
            PhishinShow fullShow,
            ArtistUpstreamSource src,
            Source dbSource,
            PerformContext? ctx
        )
        {
            dbSource.has_jamcharts = fullShow.tags.Count(t => t.name == "Jamcharts") > 0;
            dbSource = await _sourceService.Save(dbSource);

            var setIndexMap = BuildSetIndexMap(fullShow.tracks);
            var sets = new Dictionary<string, SourceSet?>();

            foreach (var track in fullShow.tracks)
            {
                SourceSet? set = sets.GetValue(track.set);

                if (set == null)
                {
                    set = new SourceSet
                    {
                        source_id = dbSource.id,
                        index = setIndexMap[track.set],
                        name = track.set_name,
                        is_encore = track.set[0] == 'E',
                        updated_at = dbSource.updated_at,
                    };

                    // this needs to be set after loading from the db
                    set.tracks = new List<SourceTrack>();

                    sets[track.set] = set;
                }
            }

            var setMaps = (
                await _sourceSetService.UpdateAll(
                    dbSource,
                    sets.Values.Where(s => s != null).Select(s => s!),
                    SourceSetUuidVersion.V2
                )
            )
                .GroupBy(s => s.index)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Single());

            foreach (var kvp in setMaps)
            {
                kvp.Value.tracks = new List<SourceTrack>();
            }

            // shows with incomplete=true won't have mp3s for every track. we don't care about those.
            var tracksWithMp3s = fullShow.tracks.Where(t => t.mp3 != null);

            foreach (var track in tracksWithMp3s)
            {
                var set = setMaps[setIndexMap[track.set]];
                set.tracks.Add(
                    new SourceTrack
                    {
                        source_set_id = set.id,
                        source_id = dbSource.id,
                        title = track.title,
                        duration = track.duration / 1000,
                        track_position = track.position,
                        // Phish.in slugs could stay the same when the track changes
                        slug =
                            SlugifyTrack(track.title)
                            + "-"
                            + track.id.ToString(CultureInfo.InvariantCulture),
                        mp3_url = track.mp3.Replace("http:", "https:"),
                        updated_at = track.updated_at ?? dbSource.updated_at,
                        artist_id = artist.id,
                    }
                );
            }

            stats.Created += (
                await _sourceTrackService.InsertAll(
                    dbSource,
                    setMaps.SelectMany(kvp => kvp.Value.tracks)
                )
            ).Count();

            await ProcessSetlistShow(stats, fullShow, artist, dbSource);

            ResetTrackSlugCounts();

            return dbSource;
        }

        public async Task<ImportStats> ProcessShows(
            Artist artist,
            ArtistUpstreamSource src,
            PerformContext? ctx
        )
        {
            var stats = new ImportStats();

            var prog = ctx?.WriteProgressBar();
            var pageSize = 20;

            var isThinScrape = CurrentImportOptions.IsThinScrape;
            var yearCutoff = isThinScrape ? $"{CurrentImportOptions.OnlyYear}-01-01" : null;

            var pages = 80;

            if (isThinScrape)
            {
                // Fetch page 1 just to learn total_pages, then start from the last page
                var firstPage = await PhishinApiRequest<IEnumerable<PhishinShow>>(
                    "shows",
                    ctx,
                    "date",
                    pageSize,
                    1
                );
                pages = firstPage.total_pages;
            }

            // For thin scrapes: iterate last page → first (newest shows first, since pages are date ASC)
            // For full imports: iterate first page → last (all shows)
            var startPage = isThinScrape ? pages : 1;
            var pageStep = isThinScrape ? -1 : 1;
            var allShowsBeforeCutoff = false;

            for (
                var currentPage = startPage;
                !allShowsBeforeCutoff && (isThinScrape ? currentPage >= 1 : currentPage <= pages);
                currentPage += pageStep
            )
            {
                var apiShows = await PhishinApiRequest<IEnumerable<PhishinShow>>(
                    "shows",
                    ctx,
                    "date",
                    pageSize,
                    currentPage
                );

                if (!isThinScrape)
                {
                    pages = apiShows.total_pages;
                }

                var shows = apiShows.data.ToList();
                var processedAnyOnPage = false;

                foreach (var (idx, show) in shows.Select((s, i) => (i, s)))
                {
                    // Within each page, shows are date ASC. Skip pre-cutoff shows but
                    // keep processing — later shows on the same page may be in range.
                    if (
                        isThinScrape
                        && string.Compare(show.date, yearCutoff, StringComparison.Ordinal) < 0
                    )
                    {
                        continue;
                    }

                    processedAnyOnPage = true;

                    try
                    {
                        await ProcessOneShow(stats, artist, src, show, ctx);
                    }
                    catch (Exception e)
                    {
                        ImportProgress.WriteLine(
                            log,
                            ctx,
                            $"Error processing show (but continuing): {show.date} (id: {show.id})"
                        );
                        ImportProgress.LogException(log, ctx, e);

                        e.Data["phishin_show_date"] = show.date;
                        e.Data["phishin_show_id"] = show.id;

                        SentrySdk.CaptureException(e);
                    }

                    prog?.SetValue(
                        100.0 * (((currentPage - 1) * pageSize) + idx + 1) / apiShows.total_entries
                    );
                }

                // If we're doing a thin scrape and an entire page had no shows in range,
                // all remaining pages (earlier dates) won't either
                if (isThinScrape && !processedAnyOnPage)
                {
                    ImportProgress.WriteLine(
                        log,
                        ctx,
                        $"Thin scrape: no shows on page {currentPage} matched year {CurrentImportOptions.OnlyYear}, stopping"
                    );
                    allShowsBeforeCutoff = true;
                }
            }

            return stats;
        }

        private async Task ProcessOneShow(
            ImportStats stats,
            Artist artist,
            ArtistUpstreamSource src,
            PhishinShow show,
            PerformContext? ctx
        )
        {
            using var scope = new TransactionScope(
                TransactionScopeOption.Required,
                new TransactionOptions()
                {
                    IsolationLevel =
                        Transaction.Current?.IsolationLevel ?? IsolationLevel.RepeatableRead,
                },
                TransactionScopeAsyncFlowOption.Enabled
            );

            await performances.LockArtistAsync(artist);
            Source? dbSource = existingSources.GetValue(show.id.ToString());
            var creating = dbSource == null;
            await UpgradeLegacySetlistKey(stats, dbSource, show.id);

            if (dbSource == null)
            {
                dbSource = await ProcessShow(
                    stats,
                    artist,
                    show,
                    src,
                    new Source
                    {
                        updated_at = show.updated_at,
                        artist_id = artist.id,
                        venue_id = existingVenues[show.venue.id.ToString()]!.id,
                        display_date = show.date,
                        upstream_identifier = show.id.ToString(),
                        is_soundboard = show.sbd,
                        is_remaster = show.remastered,
                        description = "",
                        taper_notes = show.taper_notes,
                    },
                    ctx
                );

                existingSources[dbSource.upstream_identifier] = dbSource!;

                stats.Created++;
            }
            else if (show.updated_at > dbSource.updated_at)
            {
                dbSource.updated_at = show.updated_at;
                dbSource.venue_id = existingVenues[show.venue.id.ToString()]!.id;
                dbSource.display_date = show.date;
                dbSource.upstream_identifier = show.id.ToString();
                dbSource.is_soundboard = show.sbd;
                dbSource.is_remaster = show.remastered;
                dbSource.description = "";
                dbSource.taper_notes = show.taper_notes;

                dbSource = await ProcessShow(stats, artist, show, src, dbSource, ctx);

                existingSources[dbSource.upstream_identifier] = dbSource!;

                stats.Updated++;
            }
            else if (
                !existingSetlistShows.ContainsKey(show.id.ToString(CultureInfo.InvariantCulture))
            )
            {
                // A recording can outlive its setlist row. Restore that identity without
                // overwriting newer recording metadata with this listing's older fields.
                await ProcessSetlistShow(stats, show, artist, dbSource);
            }

            // Metadata can correct the billed date; refresh the existing provider link too.
            var links = await linkService.AddLinksForSource(
                dbSource,
                new[]
                {
                    new Link
                    {
                        source_id = dbSource.id,
                        for_ratings = false,
                        for_source = true,
                        for_reviews = false,
                        upstream_source_id = src.upstream_source_id,
                        url = $"https://phish.in/{dbSource.display_date}",
                        label = "View on phish.in",
                    },
                }
            );
            if (creating)
            {
                stats.Created += links.Count();
            }

            // Provider show identity distinguishes concerts; source sets remain subdivisions.
            var performance = await performances.AssignSourceAsync(
                artist,
                dbSource!.id,
                "phish.in:" + show.id.ToString(CultureInfo.InvariantCulture),
                dbSource.display_date,
                evidence: JsonConvert.SerializeObject(
                    new { provider = "phish.in", show_id = show.id }
                )
            );
            await performances.AssignSetlistAsync(
                artist,
                existingSetlistShows[show.id.ToString(CultureInfo.InvariantCulture)]!.id,
                performance.uuid,
                JsonConvert.SerializeObject(new { provider = "phish.in", show_id = show.id })
            );

            scope.Complete();
        }
    }
}
