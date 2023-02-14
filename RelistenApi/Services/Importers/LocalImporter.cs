using System;
using System.Collections.Generic;
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
using Relisten.Vendor.Local;

namespace Relisten.Import
{
    public class LocalImporter : ImporterBase
    {
        public const string DataSourceName = "relisten";

        private readonly LinkService linkService;

        private readonly IDictionary<string, Era> yearToEraMapping = new Dictionary<string, Era>();
        private IDictionary<string, Era> existingEras = new Dictionary<string, Era>();
        private IDictionary<string, SetlistShow> existingSetlistShows = new Dictionary<string, SetlistShow>();
        private IDictionary<string, SetlistSong> existingSetlistSongs = new Dictionary<string, SetlistSong>();

        private IDictionary<string, Source> existingSources = new Dictionary<string, Source>();
        private IDictionary<string, Tour> existingTours = new Dictionary<string, Tour>();
        private IDictionary<string, VenueWithShowCount> existingVenues = new Dictionary<string, VenueWithShowCount>();

        public LocalImporter(
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
            ILogger<LocalImporter> log,
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
        protected ILogger<LocalImporter> _log { get; set; }
        public IConfiguration _configuration { get; }

        public override string ImporterName => "local";

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

        private string LocalApiUrl(string api)
        {
            return
                $"https://audio.relisten.net/{api}.json";
        }

        private async Task<LocalRootObject<T>> LocalApiRequest<T>(string apiRoute, PerformContext ctx)
        {
            var url = LocalApiUrl(apiRoute);
            ctx?.WriteLine($"Requesting {url}");
            var resp = await http.GetAsync(url);
            return JsonConvert.DeserializeObject<LocalRootObject<T>>(await resp.Content.ReadAsStringAsync());
        }

        private async Task<LocalShowObject> LocalShowRequest(string apiRoute, PerformContext ctx)
        {
            var url = LocalApiUrl(apiRoute);
            ctx?.WriteLine($"Requesting {url}");
            var resp = await http.GetAsync(url);
            return JsonConvert.DeserializeObject<LocalShowObject>(await resp.Content.ReadAsStringAsync());
        }

        public async Task<ImportStats> ProcessTours(Artist artist, PerformContext ctx)
        {
            var stats = new ImportStats();

            foreach (var tour in (await LocalApiRequest<IEnumerable<LocalSmallTour>>("tours", ctx)).data)
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

            foreach (var era in (await LocalApiRequest<IDictionary<string, IList<string>>>("eras", ctx)).data)
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

            foreach (var song in (await LocalApiRequest<IEnumerable<LocalSmallSong>>("songs", ctx)).data)
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

            foreach (var venue in (await LocalApiRequest<IEnumerable<LocalSmallVenue>>("venues", ctx)).data)
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

        private async Task ProcessSetlistShow(ImportStats stats, LocalShow show, Artist artist,
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
                    // venue_id = existingVenues[show.venue.id.ToString()].id,
                    // tour_id = existingTours[show.tour_id.ToString()].id,
                    // era_id = yearToEraMapping
                    //     .GetValue(show.date.Substring(0, 4), yearToEraMapping["1983-1987"]).id,
                    updated_at = dbSource.updated_at
                });

                stats.Created++;

                addSongs = true;
            }
            else if (show.updated_at > dbShow.updated_at)
            {
                dbShow.date = DateTime.Parse(show.date);
                // dbShow.venue_id = existingVenues[show.venue.id.ToString()].id;
                // dbShow.tour_id = existingTours[show.tour_id.ToString()].id;
                // dbShow.era_id = yearToEraMapping
                //     .GetValue(show.date.Substring(0, 4), yearToEraMapping["1983-1987"]).id;
                dbShow.updated_at = dbSource.updated_at;

                dbShow = await _setlistShowService.Save(dbShow);

                stats.Updated++;

                addSongs = true;
            }

            if (addSongs)
            {
                var dbSongs = show.tracks
                        .SelectMany(localTrack =>
                            localTrack.song_ids.Select(song_id =>
                                existingSetlistSongs.GetValue(song_id.ToString()))).Where(t => t != null)
                        .GroupBy(t => t.upstream_identifier).Select(g => g.First()).ToList()
                    ;

                stats += await _setlistShowService.UpdateSongPlays(dbShow, dbSongs);
            }
        }

        private async Task<Source> ProcessShow(ImportStats stats, Artist artist, LocalShow fullShow,
            ArtistUpstreamSource src, Source dbSource, PerformContext ctx)
        {

            var apiShow =
                await LocalShowRequest($"{src.upstream_identifier}/{fullShow.dir.ToString()}/metadata", ctx);

            var tracks = apiShow.tracks.ToList();

            // dbSource.has_jamcharts = fullShow.tags.Count(t => t.name == "Jamcharts") > 0;
            dbSource = await _sourceService.Save(dbSource);

            var sets = new Dictionary<string, SourceSet>();

            var set = new SourceSet
            {
                source_id = dbSource.id,
                index = 0,
                is_encore = false,
                name = "Set",
                updated_at = dbSource.updated_at
            };

            sets.Add("0", set);

            var setMaps = (await _sourceSetService.UpdateAll(dbSource, sets.Values))
                .GroupBy(s => s.index)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Single());

            foreach (var kvp in setMaps)
            {
                kvp.Value.tracks = new List<SourceTrack>();
           
                foreach (var track in tracks)
                {
                    kvp.Value.tracks.Add(new SourceTrack
                    {
                        source_set_id = kvp.Value.id,
                        source_id = dbSource.id,
                        title = track.tags.title,
                        duration = track.tags.duration,
                        track_position = track.tags.track.no,
                        slug = SlugifyTrack(track.tags.title),
                        mp3_url = $"https://audio.relisten.net/{dbSource.upstream_identifier}/{fullShow.dir.ToString()}/{track.mp3}",
                        updated_at = dbSource.updated_at,
                        artist_id = artist.id
                    });
                }
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

            var prog = ctx?.WriteProgressBar();

            var apiShows =
                await LocalApiRequest<IEnumerable<LocalShow>>($"{artist.slug}/metadata", ctx);

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
                }

                prog?.SetValue(100.0 * (idx / apiShows.total_entries));
            }

            async Task processShow(LocalShow show)
            {
                using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
                {
                    var dbSource = existingSources.GetValue(show.dir.ToString());

                    if (dbSource == null)
                    {
                        dbSource = await ProcessShow(stats, artist, show, src,
                            new Source
                            {
                                updated_at = DateTime.Now,
                                artist_id = artist.id,
                                // venue_id = existingVenues[show.venue.id.ToString()].id,
                                display_date = $"{show.year}-{show.month}-{show.day}",
                                upstream_identifier = show.dir.ToString(),
                                is_soundboard = false,
                                is_remaster = false,
                                description = "",
                                taper_notes = ""
                            }, ctx);

                        existingSources[dbSource.upstream_identifier] = dbSource;

                        stats.Created++;
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
            }

            return stats;
        }
    }
}
