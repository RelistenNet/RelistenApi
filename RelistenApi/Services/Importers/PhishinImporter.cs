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
using Relisten.Vendor;
using Relisten.Vendor.Phishin;
using Sentry;

namespace Relisten.Import
{
    public class PhishinImporter : ImporterBase
    {
        public const string DataSourceName = "phish.in";

        private readonly LinkService linkService;

        private readonly IDictionary<string, Era> yearToEraMapping = new Dictionary<string, Era>();
        private IDictionary<string, Era> existingEras = new Dictionary<string, Era>();
        private IDictionary<string, SetlistShow> existingSetlistShows = new Dictionary<string, SetlistShow>();
        private IDictionary<string, SetlistSong> existingSetlistSongs = new Dictionary<string, SetlistSong>();

        private IDictionary<string, Source> existingSources = new Dictionary<string, Source>();
        private IDictionary<string, Tour> existingTours = new Dictionary<string, Tour>();
        private IDictionary<string, VenueWithShowCount> existingVenues = new Dictionary<string, VenueWithShowCount>();

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

            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", configuration["PHISHIN_KEY"]);
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
            PerformContext ctx)
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

            ctx?.WriteLine("Rebuilding");
            await RebuildShows(artist);
            await RebuildYears(artist);

            return stats;
            //return await ProcessIdentifiers(artist, await this.http.GetAsync(SearchUrlForArtist(artist)));
        }

        public override Task<ImportStats> ImportSpecificShowDataForArtist(Artist artist, ArtistUpstreamSource src,
            string showIdentifier, PerformContext ctx)
        {
            return Task.FromResult(new ImportStats());
        }

        private async Task PreloadData(Artist artist)
        {
            existingSources = (await _sourceService.AllForArtist(artist))
                .GroupBy(venue => venue.upstream_identifier).ToDictionary(grp => grp.Key, grp => grp.First());

            existingEras = (await _eraService.AllForArtist(artist)).GroupBy(era => era.name)
                .ToDictionary(grp => grp.Key, grp => grp.First());

            existingVenues = (await _venueService.AllIncludingUnusedForArtist(artist))
                .GroupBy(venue => venue.upstream_identifier).ToDictionary(grp => grp.Key, grp => grp.First());

            existingTours = (await _tourService.AllForArtist(artist))
                .GroupBy(tour => tour.upstream_identifier).ToDictionary(grp => grp.Key, grp => grp.First());

            existingSetlistShows = (await _setlistShowService.AllForArtist(artist))
                .GroupBy(show => show.upstream_identifier).ToDictionary(grp => grp.Key, grp => grp.First());

            existingSetlistSongs = (await _setlistSongService.AllForArtist(artist))
                .GroupBy(song => song.upstream_identifier).ToDictionary(grp => grp.Key, grp => grp.First());
        }

        private string PhishinApiUrl(string api, string sort_attr = null, int per_page = 99999, int? page = null)
        {
            return
                $"https://phish.in/api/v1/{api}.json?per_page={per_page}{(sort_attr != null ? "&sort_attr=" + sort_attr : "")}{(page != null ? "&page=" + page.Value : "")}";
        }

        private async Task<PhishinRootObject<T>> PhishinApiRequest<T>(string apiRoute, PerformContext ctx,
            string sort_attr = null, int per_page = 99999, int? page = null)
        {
            var url = PhishinApiUrl(apiRoute, sort_attr, per_page, page);
            ctx?.WriteLine($"Requesting {url}");
            var resp = await http.GetAsync(url);
            return JsonConvert.DeserializeObject<PhishinRootObject<T>>(await resp.Content.ReadAsStringAsync());
        }

        public async Task<ImportStats> ProcessTours(Artist artist, PerformContext ctx)
        {
            var stats = new ImportStats();

            foreach (var tour in (await PhishinApiRequest<IEnumerable<PhishinSmallTour>>("tours", ctx)).data)
            {
                var dbTour = existingTours.GetValue(tour.id.ToString());

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
                        upstream_identifier = tour.id.ToString()
                    });

                    existingTours[dbTour.upstream_identifier] = dbTour;

                    stats.Created++;
                }
                else if (tour.updated_at > dbTour.updated_at)
                {
                    dbTour.start_date = DateTime.Parse(tour.starts_on);
                    dbTour.end_date = DateTime.Parse(tour.ends_on);
                    dbTour.name = tour.name;

                    dbTour = await _tourService.Save(dbTour);

                    existingTours[dbTour.upstream_identifier] = dbTour;

                    stats.Updated++;
                }
            }

            return stats;
        }

        public async Task<ImportStats> ProcessEras(Artist artist, PerformContext ctx)
        {
            var stats = new ImportStats();

            var order = 0;

            foreach (var era in (await PhishinApiRequest<IDictionary<string, IList<string>>>("eras", ctx)).data)
            {
                var dbEra = existingEras.GetValue(era.Key);

                if (dbEra == null)
                {
                    dbEra = await _eraService.Save(new Era
                    {
                        artist_id = artist.id, name = era.Key, order = order, updated_at = DateTime.Now
                    });

                    existingEras[dbEra.name] = dbEra;

                    stats.Created++;
                }

                foreach (var year in era.Value)
                {
                    yearToEraMapping[year] = dbEra;
                }

                order++;
            }

            return stats;
        }

        public async Task<ImportStats> ProcessSongs(Artist artist, PerformContext ctx)
        {
            var stats = new ImportStats();

            var songsToSave = new List<SetlistSong>();
            var songs = (await PhishinApiRequest<IEnumerable<PhishinSmallSong>>("songs", ctx)).data;

            foreach (var song in songs)
            {
                var dbSong = existingSetlistSongs.GetValue(song.id.ToString());

                // skip aliases for now
                if (dbSong == null && song.alias_for.HasValue == false)
                {
                    songsToSave.Add(new SetlistSong
                    {
                        updated_at = song.updated_at,
                        artist_id = artist.id,
                        name = song.title,
                        slug = song.slug,
                        upstream_identifier = song.id.ToString()
                    });
                }
            }

            var newSongs = await _setlistSongService.InsertAll(artist, songsToSave);

            foreach (var s in newSongs)
            {
                existingSetlistSongs[s.upstream_identifier] = s;
            }

            stats.Created += newSongs.Count();

            return stats;
        }

        public async Task<ImportStats> ProcessVenues(Artist artist, PerformContext ctx)
        {
            var stats = new ImportStats();

            foreach (var venue in (await PhishinApiRequest<IEnumerable<PhishinSmallVenue>>("venues", ctx)).data)
            {
                var dbVenue = existingVenues.GetValue(venue.id.ToString());

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
                        upstream_identifier = venue.id.ToString()
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

                    existingVenues[dbVenue.upstream_identifier] = dbVenue;

                    stats.Updated++;
                }
            }

            return stats;
        }

        private int SetIndexForIdentifier(string ident)
        {
            if (ident == "S") { return 0; }

            if (ident == "1") { return 1; }

            if (ident == "2") { return 2; }

            if (ident == "3") { return 3; }

            if (ident == "4") { return 4; }

            if (ident == "E") { return 5; }

            if (ident == "E2") { return 6; }

            if (ident == "E3") { return 7; }

            return 8;
        }

        private async Task ProcessSetlistShow(ImportStats stats, PhishinShow show, Artist artist,
            ArtistUpstreamSource src, Source dbSource, IDictionary<string, SourceSet> sets)
        {
            var dbShow = existingSetlistShows.GetValue(show.date);

            var addSongs = false;

            if (dbShow == null)
            {
                dbShow = await _setlistShowService.Save(new SetlistShow
                {
                    artist_id = artist.id,
                    upstream_identifier = show.date,
                    date = DateTime.Parse(show.date),
                    venue_id = existingVenues[show.venue.id.ToString()].id,
                    tour_id = existingTours[show.tour_id.ToString()].id,
                    era_id = yearToEraMapping
                        .GetValue(show.date.Substring(0, 4), yearToEraMapping["1983-1987"]).id,
                    updated_at = dbSource.updated_at
                });

                stats.Created++;

                addSongs = true;
            }
            else if (show.updated_at > dbShow.updated_at)
            {
                dbShow.date = DateTime.Parse(show.date);
                dbShow.venue_id = existingVenues[show.venue.id.ToString()].id;
                dbShow.tour_id = existingTours[show.tour_id.ToString()].id;
                dbShow.era_id = yearToEraMapping
                    .GetValue(show.date.Substring(0, 4), yearToEraMapping["1983-1987"]).id;
                dbShow.updated_at = dbSource.updated_at;

                dbShow = await _setlistShowService.Save(dbShow);

                stats.Updated++;

                addSongs = true;
            }

            if (addSongs)
            {
                var dbSongs = show.tracks
                        .SelectMany(phishinTrack =>
                            phishinTrack.song_ids.Select(song_id =>
                                existingSetlistSongs.GetValue(song_id.ToString()))).Where(t => t != null)
                        .GroupBy(t => t.upstream_identifier).Select(g => g.First()).ToList()
                    ;

                stats += await _setlistShowService.UpdateSongPlays(dbShow, dbSongs);
            }
        }

        private async Task<Source> ProcessShow(ImportStats stats, Artist artist, PhishinShow fullShow,
            ArtistUpstreamSource src, Source dbSource, PerformContext ctx)
        {
            dbSource.has_jamcharts = fullShow.tags.Count(t => t.name == "Jamcharts") > 0;
            dbSource = await _sourceService.Save(dbSource);

            var sets = new Dictionary<string, SourceSet>();

            foreach (var track in fullShow.tracks)
            {
                var set = sets.GetValue(track.set);

                if (set == null)
                {
                    set = new SourceSet
                    {
                        source_id = dbSource.id,
                        index = SetIndexForIdentifier(track.set),
                        name = track.set_name,
                        is_encore = track.set[0] == 'E',
                        updated_at = dbSource.updated_at
                    };

                    // this needs to be set after loading from the db
                    set.tracks = new List<SourceTrack>();

                    sets[track.set] = set;
                }
            }

            var setMaps = (await _sourceSetService.UpdateAll(dbSource, sets.Values))
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
                var set = setMaps[SetIndexForIdentifier(track.set)];
                set.tracks.Add(new SourceTrack
                {
                    source_set_id = set.id,
                    source_id = dbSource.id,
                    title = track.title,
                    duration = track.duration / 1000,
                    track_position = track.position,
                    // Phish.in slugs could stay the same when the track changes
                    slug = SlugifyTrack(track.title) + "-" + track.id.ToString(CultureInfo.InvariantCulture),
                    mp3_url = track.mp3.Replace("http:", "https:"),
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

        public async Task<ImportStats> ProcessShows(Artist artist, ArtistUpstreamSource src, PerformContext ctx)
        {
            var stats = new ImportStats();

            var pages = 80;

            var prog = ctx?.WriteProgressBar();
            var pageSize = 20;

            for (var currentPage = 1; currentPage <= pages; currentPage++)
            {
                var apiShows =
                    await PhishinApiRequest<IEnumerable<PhishinShow>>("shows", ctx, "date", pageSize, currentPage);
                pages = apiShows.total_pages;

                var shows = apiShows.data.ToList();

                foreach (var (idx, show) in shows.Select((s, i) => (i, s)))
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

                    prog?.SetValue(100.0 * (((currentPage - 1) * pageSize) + idx + 1) / apiShows.total_entries);
                }
            }

            async Task processShow(PhishinShow show)
            {
                using var scope = new TransactionScope(TransactionScopeOption.Required,
                    new TransactionOptions() { IsolationLevel = IsolationLevel.RepeatableRead },
                    TransactionScopeAsyncFlowOption.Enabled);

                var dbSource = existingSources.GetValue(show.id.ToString());

                if (dbSource == null)
                {
                    dbSource = await ProcessShow(stats, artist, show, src,
                        new Source
                        {
                            updated_at = show.updated_at,
                            artist_id = artist.id,
                            venue_id = existingVenues[show.venue.id.ToString()].id,
                            display_date = show.date,
                            upstream_identifier = show.id.ToString(),
                            is_soundboard = show.sbd,
                            is_remaster = show.remastered,
                            description = "",
                            taper_notes = show.taper_notes
                        }, ctx);

                    existingSources[dbSource.upstream_identifier] = dbSource;

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
                    dbSource.updated_at = show.updated_at;
                    dbSource.venue_id = existingVenues[show.venue.id.ToString()].id;
                    dbSource.display_date = show.date;
                    dbSource.upstream_identifier = show.id.ToString();
                    dbSource.is_soundboard = show.sbd;
                    dbSource.is_remaster = show.remastered;
                    dbSource.description = "";
                    dbSource.taper_notes = show.taper_notes;

                    dbSource = await ProcessShow(stats, artist, show, src, dbSource, ctx);

                    existingSources[dbSource.upstream_identifier] = dbSource;

                    stats.Updated++;
                }

                scope.Complete();
            }

            return stats;
        }
    }
}
