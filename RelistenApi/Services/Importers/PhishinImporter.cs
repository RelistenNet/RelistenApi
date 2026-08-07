using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Transactions;
using Hangfire.Console;
using Hangfire.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Vendor;
using Relisten.Vendor.Phishin;
using Sentry;

namespace Relisten.Import
{
    public class PhishinImporter : ImporterBase
    {
        public const string DataSourceName = "phish.in";

        private readonly LinkService linkService;

        private readonly IDictionary<string, Era?> yearToEraMapping = new Dictionary<string, Era?>();
        private IDictionary<string, Era?> existingEras = new Dictionary<string, Era?>();
        private IDictionary<string, SetlistShow?> existingSetlistShows = new Dictionary<string, SetlistShow?>();
        private IDictionary<string, SetlistSong?> existingSetlistSongs = new Dictionary<string, SetlistSong?>();

        private IDictionary<string, Source?> existingSources = new Dictionary<string, Source?>();
        private IDictionary<string, Tour?> existingTours = new Dictionary<string, Tour?>();
        private IDictionary<string, Tour?> existingToursByName = new Dictionary<string, Tour?>();
        private IDictionary<string, VenueWithShowCount?> existingVenues = new Dictionary<string, VenueWithShowCount?>();
        private IDictionary<string, VenueWithShowCount?> existingVenuesByName = new Dictionary<string, VenueWithShowCount?>();
        private IDictionary<string, SetlistSong?> existingSetlistSongsBySlug = new Dictionary<string, SetlistSong?>();

        public PhishinImporter(
            DbService db,
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
            RedisService redisService
        ) : base(db, redisService)
        {
            this.linkService = linkService;
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

        public override string ImporterName => "phish.in";

        public override ImportableData ImportableDataForArtist(Artist artist)
        {
            return ImportableData.Sources
                   | ImportableData.Venues
                   | ImportableData.Tours
                   | ImportableData.Eras
                   | ImportableData.SetlistShowsAndSongs;
        }

        public override async Task<ImportStats> ImportDataForArtist(Artist artist, ArtistUpstreamSource src,
            PerformContext? ctx)
        {
            await PreloadData(artist);

            var stats = new ImportStats();

            ctx?.WriteLine("Processing Eras");
            stats += await ProcessEras(artist, ctx);

            ctx?.WriteLine("Processing Tours");
            stats += await ProcessTours(artist, ctx);

            ctx?.WriteLine("Processing Songs");
            stats += await ProcessSongs(artist, ctx);

            ctx?.WriteLine("Processing Venues");
            stats += await ProcessVenues(artist, ctx);

            ctx?.WriteLine("Processing Shows");
            stats += await ProcessShows(artist, src, ctx);

            ctx?.WriteLine($"Import stats: {stats}");

            if (stats.Created > 0 || stats.Updated > 0 || stats.Removed > 0)
            {
                ctx?.WriteLine("Rebuilding shows and years...");
                await RebuildShows(artist);
                await RebuildYears(artist);
                ctx?.WriteLine("--> rebuilt!");
            }
            else
            {
                ctx?.WriteLine("No changes detected, skipping show/year rebuild.");
            }

            return stats;
        }

        public override Task<ImportStats> ImportSpecificShowDataForArtist(Artist artist, ArtistUpstreamSource src,
            string? showIdentifier, PerformContext? ctx)
        {
            return Task.FromResult(new ImportStats());
        }

        private async Task PreloadData(Artist artist)
        {
            existingSources = (await _sourceService.AllForArtistFromPrimary(artist))
                .GroupBy(venue => venue.upstream_identifier)
                .ToDictionary(grp => grp.Key, grp => (Source?)grp.First());

            existingEras = (await _eraService.AllForArtist(artist)).GroupBy(era => era.name)
                .ToDictionary(grp => grp.Key, grp => (Era?)grp.First());

            existingVenues = (await _venueService.AllIncludingUnusedForArtist(artist))
                .GroupBy(venue => venue.upstream_identifier)
                .ToDictionary(grp => grp.Key, grp => (VenueWithShowCount?)grp.First());

            existingVenuesByName = existingVenues.Values
                .Where(v => v != null)
                .GroupBy(v => v!.name)
                .ToDictionary(grp => grp.Key, grp => (VenueWithShowCount?)grp.First());

            existingTours = (await _tourService.AllForArtist(artist))
                .GroupBy(tour => tour.upstream_identifier).ToDictionary(grp => grp.Key, grp => (Tour?)grp.First());

            existingToursByName = existingTours.Values
                .Where(t => t != null)
                .GroupBy(t => t!.name)
                .ToDictionary(grp => grp.Key, grp => (Tour?)grp.First());

            existingSetlistShows = (await _setlistShowService.AllForArtist(artist))
                .GroupBy(show => show.upstream_identifier)
                .ToDictionary(grp => grp.Key, grp => (SetlistShow?)grp.First());

            existingSetlistSongs = (await _setlistSongService.AllForArtist(artist))
                .GroupBy(song => song.upstream_identifier)
                .ToDictionary(grp => grp.Key, grp => (SetlistSong?)grp.First());

            existingSetlistSongsBySlug = existingSetlistSongs.Values
                .Where(s => s != null)
                .GroupBy(s => s!.slug)
                .ToDictionary(grp => grp.Key, grp => (SetlistSong?)grp.First());
        }

        private string PhishinApiUrl(string resource, string? sort = null, int per_page = 1000, int? page = null)
        {
            var url = $"https://phish.in/api/v2/{resource}?per_page={per_page}";
            if (sort != null) url += $"&sort={sort}";
            if (page != null) url += $"&page={page.Value}";
            return url;
        }

        private async Task<(List<T> data, int totalPages, int totalEntries)> PhishinApiPagedRequest<T>(
            string resource, string dataKey, PerformContext? ctx,
            string? sort = null, int per_page = 1000, int? page = null,
            string? extraParams = null)
        {
            var url = PhishinApiUrl(resource, sort, per_page, page);
            if (extraParams != null) url += extraParams;
            ctx?.WriteLine($"Requesting {url}");
            var resp = await http.GetAsync(url);
            var json = await resp.Content.ReadAsStringAsync();
            var obj = JObject.Parse(json);
            var data = obj[dataKey]!.ToObject<List<T>>()!;
            var totalPages = obj.Value<int>("total_pages");
            var totalEntries = obj.Value<int>("total_entries");
            return (data, totalPages, totalEntries);
        }

        private async Task<T> PhishinApiGet<T>(string resource, PerformContext? ctx)
        {
            var url = $"https://phish.in/api/v2/{resource}";
            ctx?.WriteLine($"Requesting {url}");
            var resp = await http.GetAsync(url);
            var json = await resp.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<T>(json)!;
        }

        public async Task<ImportStats> ProcessTours(Artist artist, PerformContext? ctx)
        {
            var stats = new ImportStats();

            var (tours, _, _) = await PhishinApiPagedRequest<PhishinSmallTour>("tours", "tours", ctx);

            foreach (var tour in tours)
            {
                Tour? dbTour = existingTours.GetValue(tour.slug)
                               ?? existingToursByName.GetValue(tour.name);

                if (dbTour == null)
                {
                    dbTour = await _tourService.Save(new Tour
                    {
                        updated_at = tour.updated_at,
                        artist_id = artist.id,
                        start_date = DateTime.Parse(tour.starts_on),
                        end_date = DateTime.Parse(tour.ends_on),
                        name = tour.name,
                        slug = Slugify(tour.name),
                        upstream_identifier = tour.slug
                    });

                    existingTours[dbTour.upstream_identifier] = dbTour!;
                    existingToursByName[dbTour.name] = dbTour!;

                    stats.Created++;
                }
                else if (tour.updated_at > dbTour.updated_at)
                {
                    if (dbTour.upstream_identifier != tour.slug)
                    {
                        existingTours.Remove(dbTour.upstream_identifier);
                        dbTour.upstream_identifier = tour.slug;
                    }

                    dbTour.start_date = DateTime.Parse(tour.starts_on);
                    dbTour.end_date = DateTime.Parse(tour.ends_on);
                    dbTour.name = tour.name;

                    dbTour = await _tourService.Save(dbTour);

                    existingTours[dbTour.upstream_identifier] = dbTour!;
                    existingToursByName[dbTour.name] = dbTour!;

                    stats.Updated++;
                }
                else
                {
                    if (dbTour.upstream_identifier != tour.slug)
                    {
                        existingTours.Remove(dbTour.upstream_identifier);
                        dbTour.upstream_identifier = tour.slug;
                        dbTour = await _tourService.Save(dbTour);
                        existingTours[dbTour.upstream_identifier] = dbTour!;
                    }

                    existingToursByName[dbTour.name] = dbTour!;
                }
            }

            return stats;
        }

        public async Task<ImportStats> ProcessEras(Artist artist, PerformContext? ctx)
        {
            var stats = new ImportStats();

            var years = await PhishinApiGet<List<PhishinYear>>("years", ctx);

            var eraGroups = years
                .GroupBy(y => y.era)
                .OrderBy(g => g.Key);

            var order = 0;

            foreach (var eraGroup in eraGroups)
            {
                var eraName = eraGroup.Key;

                Era? dbEra = existingEras.GetValue(eraName);

                if (dbEra == null)
                {
                    dbEra = await _eraService.Save(new Era
                    {
                        artist_id = artist.id, name = eraName, order = order, updated_at = DateTime.Now
                    });

                    existingEras[dbEra.name] = dbEra;

                    stats.Created++;
                }

                foreach (var year in eraGroup)
                {
                    yearToEraMapping[year.period] = dbEra!;
                }

                order++;
            }

            return stats;
        }

        public async Task<ImportStats> ProcessSongs(Artist artist, PerformContext? ctx)
        {
            var stats = new ImportStats();

            var songsToSave = new List<SetlistSong>();
            var (songs, _, _) = await PhishinApiPagedRequest<PhishinSmallSong>("songs", "songs", ctx);

            foreach (var song in songs)
            {
                SetlistSong? dbSong = existingSetlistSongs.GetValue(song.slug)
                                      ?? existingSetlistSongsBySlug.GetValue(song.slug);

                if (dbSong == null && song.alias == null)
                {
                    songsToSave.Add(new SetlistSong
                    {
                        updated_at = song.updated_at,
                        artist_id = artist.id,
                        name = song.title,
                        slug = song.slug,
                        upstream_identifier = song.slug
                    });
                }
                else if (dbSong != null && dbSong.upstream_identifier != song.slug)
                {
                    existingSetlistSongs.Remove(dbSong.upstream_identifier);
                    dbSong.upstream_identifier = song.slug;
                    existingSetlistSongs[song.slug] = dbSong;
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
                        existingSetlistSongsBySlug[song.slug] = dbSong;
                    }
                }
            }

            stats.Created += newSongs.Count();

            return stats;
        }

        public async Task<ImportStats> ProcessVenues(Artist artist, PerformContext? ctx)
        {
            var stats = new ImportStats();

            var (venues, _, _) = await PhishinApiPagedRequest<PhishinSmallVenue>("venues", "venues", ctx);

            foreach (var venue in venues)
            {
                var pastNames = venue.other_names != null ? string.Join(", ", venue.other_names) : null;

                VenueWithShowCount? dbVenue = existingVenues.GetValue(venue.slug)
                                              ?? existingVenuesByName.GetValue(venue.name);

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
                        past_names = pastNames,
                        upstream_identifier = venue.slug
                    };

                    var createdDb = await _venueService.Save(sc);

                    sc.id = createdDb.id;

                    existingVenues[sc.upstream_identifier] = sc;
                    existingVenuesByName[sc.name] = sc;

                    stats.Created++;

                    dbVenue = sc;
                }
                else if (venue.updated_at > dbVenue.updated_at)
                {
                    if (dbVenue.upstream_identifier != venue.slug)
                    {
                        existingVenues.Remove(dbVenue.upstream_identifier);
                        dbVenue.upstream_identifier = venue.slug;
                    }

                    dbVenue.name = venue.name;
                    dbVenue.location = venue.location;
                    dbVenue.longitude = venue.longitude;
                    dbVenue.latitude = venue.latitude;
                    dbVenue.past_names = pastNames;
                    dbVenue.updated_at = venue.updated_at;

                    await _venueService.Save(dbVenue);

                    existingVenues[dbVenue.upstream_identifier] = dbVenue!;
                    existingVenuesByName[dbVenue.name] = dbVenue!;

                    stats.Updated++;
                }
                else
                {
                    if (dbVenue.upstream_identifier != venue.slug)
                    {
                        existingVenues.Remove(dbVenue.upstream_identifier);
                        dbVenue.upstream_identifier = venue.slug;
                        await _venueService.Save(dbVenue);
                        existingVenues[dbVenue.upstream_identifier] = dbVenue!;
                    }

                    existingVenuesByName[dbVenue.name] = dbVenue!;
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
                if (!map.ContainsKey(track.set_name))
                {
                    map[track.set_name] = nextIndex++;
                }
            }

            return map;
        }

        private async Task ProcessSetlistShow(ImportStats stats, PhishinShow show, Artist artist,
            ArtistUpstreamSource src, Source dbSource, IDictionary<string, SourceSet?> sets)
        {
            SetlistShow? dbShow = existingSetlistShows.GetValue(show.date);

            var addSongs = false;

            var venueId = (existingVenues.GetValue(show.venue.slug)
                           ?? existingVenuesByName.GetValue(show.venue.name))!.id;

            var tourId = show.tour_name != null
                ? (existingTours.GetValue(show.tour_name)
                   ?? existingToursByName.GetValue(show.tour_name))?.id
                : null;

            var eraId = yearToEraMapping
                .GetValue(show.date.Substring(0, 4), yearToEraMapping.Values.FirstOrDefault())?.id;

            if (dbShow == null)
            {
                dbShow = await _setlistShowService.Save(new SetlistShow
                {
                    artist_id = artist.id,
                    upstream_identifier = show.date,
                    date = DateTime.Parse(show.date),
                    venue_id = venueId,
                    tour_id = tourId ?? 0,
                    era_id = eraId ?? 0,
                    updated_at = dbSource.updated_at
                });

                stats.Created++;

                addSongs = true;
            }
            else if (show.updated_at > dbShow.updated_at)
            {
                dbShow.date = DateTime.Parse(show.date);
                dbShow.venue_id = venueId;
                dbShow.tour_id = tourId ?? dbShow.tour_id;
                dbShow.era_id = eraId ?? dbShow.era_id;
                dbShow.updated_at = dbSource.updated_at;

                dbShow = await _setlistShowService.Save(dbShow);

                stats.Updated++;

                addSongs = true;
            }

            if (addSongs && show.tracks != null)
            {
                var dbSongs = show.tracks
                        .SelectMany(phishinTrack =>
                            phishinTrack.songs.Select(song =>
                                existingSetlistSongs.GetValue(song.slug)
                                ?? existingSetlistSongsBySlug.GetValue(song.slug)))
                        .Where(t => t != null)
                        .Select(t => t!)
                        .GroupBy(t => t.upstream_identifier)
                        .Select(g => g.First())
                        .ToList()
                    ;

                stats += await _setlistShowService.UpdateSongPlays(dbShow!, dbSongs);
            }
        }

        private async Task<Source> ProcessShow(ImportStats stats, Artist artist, PhishinShow fullShow,
            ArtistUpstreamSource src, Source dbSource, PerformContext? ctx)
        {
            dbSource.has_jamcharts = fullShow.tags.Count(t => t.name == "Jamcharts") > 0;
            dbSource = await _sourceService.Save(dbSource);

            if (fullShow.tracks == null || fullShow.tracks.Count == 0)
            {
                return dbSource;
            }

            var setIndexMap = BuildSetIndexMap(fullShow.tracks);
            var sets = new Dictionary<string, SourceSet?>();

            foreach (var track in fullShow.tracks)
            {
                SourceSet? set = sets.GetValue(track.set_name);

                if (set == null)
                {
                    set = new SourceSet
                    {
                        source_id = dbSource.id,
                        index = setIndexMap[track.set_name],
                        name = track.set_name,
                        is_encore = track.set_name.StartsWith("Encore", StringComparison.OrdinalIgnoreCase),
                        updated_at = dbSource.updated_at
                    };

                    set.tracks = new List<SourceTrack>();

                    sets[track.set_name] = set;
                }
            }

            var setMaps = (await _sourceSetService.UpdateAll(dbSource, sets.Values.Where(s => s != null).Select(s => s!)))
                .GroupBy(s => s.index)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Single());

            foreach (var kvp in setMaps)
            {
                kvp.Value.tracks = new List<SourceTrack>();
            }

            var tracksWithMp3s = fullShow.tracks.Where(t => t.mp3_url != null);

            foreach (var track in tracksWithMp3s)
            {
                var set = setMaps[setIndexMap[track.set_name]];
                set.tracks.Add(new SourceTrack
                {
                    source_set_id = set.id,
                    source_id = dbSource.id,
                    title = track.title,
                    duration = track.duration / 1000,
                    track_position = track.position,
                    slug = SlugifyTrack(track.title) + "-" + track.id.ToString(CultureInfo.InvariantCulture),
                    mp3_url = track.mp3_url.Replace("http:", "https:"),
                    updated_at = track.updated_at ?? dbSource.updated_at,
                    artist_id = artist.id
                });
            }

            stats.Created +=
                (await _sourceTrackService.InsertAll(dbSource, setMaps.SelectMany(kvp => kvp.Value.tracks)))
                .Count();

            await ProcessSetlistShow(stats, fullShow, artist, src, dbSource, sets);

            ResetTrackSlugCounts();

            return dbSource;
        }

        public async Task<ImportStats> ProcessShows(Artist artist, ArtistUpstreamSource src, PerformContext? ctx)
        {
            var stats = new ImportStats();

            var prog = ctx?.WriteProgressBar();
            var pageSize = 200;

            var isThinScrape = CurrentImportOptions.IsThinScrape;
            var sort = "date:asc";
            var yearFilter = isThinScrape ? $"&year={CurrentImportOptions.OnlyYear}" : null;

            var currentPage = 1;
            var totalPages = 1;
            var processedCount = 0;
            var totalEntries = 0;

            while (currentPage <= totalPages)
            {
                var (shows, pages, entries) = await PhishinApiPagedRequest<PhishinShow>(
                    "shows", "shows", ctx, sort, pageSize, currentPage, yearFilter);

                totalPages = pages;
                totalEntries = entries;

                foreach (var show in shows)
                {
                    try
                    {
                        await processShow(show);
                    }
                    catch (Exception e)
                    {
                        ctx?.WriteLine($"Error processing show (but continuing): {show.date} (id: {show.id})");
                        ctx?.LogException(e);

                        e.Data["phishin_show_date"] = show.date;
                        e.Data["phishin_show_id"] = show.id;

                        SentrySdk.CaptureException(e);
                    }

                    processedCount++;
                    prog?.SetValue(100.0 * processedCount / totalEntries);
                }

                currentPage++;
            }

            async Task processShow(PhishinShow show)
            {
                using var scope = new TransactionScope(TransactionScopeOption.Required,
                    new TransactionOptions() { IsolationLevel = IsolationLevel.RepeatableRead },
                    TransactionScopeAsyncFlowOption.Enabled);

                Source? dbSource = existingSources.GetValue(show.id.ToString());

                var venueId = (existingVenues.GetValue(show.venue.slug)
                               ?? existingVenuesByName.GetValue(show.venue.name))!.id;

                var isSbd = show.tags?.Any(t => t.name == "SBD") ?? false;
                var isRemaster = show.tags?.Any(t => t.name == "RMSTR") ?? false;

                if (dbSource == null)
                {
                    var fullShow = await PhishinApiGet<PhishinShow>($"shows/{show.date}", ctx);

                    dbSource = await ProcessShow(stats, artist, fullShow, src,
                        new Source
                        {
                            updated_at = show.updated_at,
                            artist_id = artist.id,
                            venue_id = venueId,
                            display_date = show.date,
                            upstream_identifier = show.id.ToString(),
                            is_soundboard = isSbd,
                            is_remaster = isRemaster,
                            description = "",
                            taper_notes = show.taper_notes
                        }, ctx);

                    existingSources[dbSource.upstream_identifier] = dbSource!;

                    stats.Created++;

                    stats.Created += (await linkService.AddLinksForSource(dbSource,
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
                                label = "View on phish.in"
                            }
                        })).Count();
                }
                else if (show.updated_at > dbSource.updated_at)
                {
                    var fullShow = await PhishinApiGet<PhishinShow>($"shows/{show.date}", ctx);

                    dbSource.updated_at = show.updated_at;
                    dbSource.venue_id = venueId;
                    dbSource.display_date = show.date;
                    dbSource.upstream_identifier = show.id.ToString();
                    dbSource.is_soundboard = isSbd;
                    dbSource.is_remaster = isRemaster;
                    dbSource.description = "";
                    dbSource.taper_notes = show.taper_notes;

                    dbSource = await ProcessShow(stats, artist, fullShow, src, dbSource, ctx);

                    existingSources[dbSource.upstream_identifier] = dbSource!;

                    stats.Updated++;
                }

                scope.Complete();
            }

            return stats;
        }
    }
}
