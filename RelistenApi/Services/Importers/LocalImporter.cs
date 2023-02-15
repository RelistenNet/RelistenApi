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

            ctx?.WriteLine("Processing Venues");
            stats += await ProcessVenues(artist, src, ctx);

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

        public async Task<ImportStats> ProcessVenues(Artist artist, ArtistUpstreamSource src, PerformContext ctx)
        {
            var stats = new ImportStats();

            foreach (var day in (await LocalApiRequest<LocalShow>($"{src.upstream_identifier}/metadata", ctx)).data.Values)
            {
                foreach (LocalShow show in day)
                {
                    var upstreamId = $"{show.venue} {show.city}, {show.state}";
                    var dbVenue = existingVenues.GetValue(upstreamId);

                    if (dbVenue == null)
                    {
                        var sc = new VenueWithShowCount
                        {
                            artist_id = artist.id,
                            name = show.venue,
                            location = string.IsNullOrEmpty(show.city) ? "Unknown Location" : $"{show.city}, {show.state}",
                            upstream_identifier = upstreamId,
                            slug = Slugify(show.venue),
                            updated_at = DateTime.Now,
                        };

                        var createdDb = await _venueService.Save(sc);

                        sc.id = createdDb.id;

                        existingVenues[sc.upstream_identifier] = sc;

                        stats.Created++;

                        dbVenue = sc;
                    }
                    else if (show.updated_at > dbVenue.updated_at)
                    {
                        dbVenue.name = show.venue;
                        dbVenue.updated_at = show.updated_at;

                        await _venueService.Save(dbVenue);

                        existingVenues[dbVenue.upstream_identifier] = dbVenue;

                        stats.Updated++;
                    }
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
            var venueUpstreamId = $"{show.venue} {show.city}, {show.state}";

            var addSongs = false;

            if (dbShow == null)
            {
                dbShow = await _setlistShowService.Save(new SetlistShow
                {
                    artist_id = artist.id,
                    upstream_identifier = show.date,
                    date = DateTime.Parse(show.date),
                    venue_id = existingVenues[venueUpstreamId].id,
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
                dbShow.venue_id = existingVenues[venueUpstreamId].id;
                // dbShow.tour_id = existingTours[show.tour_id.ToString()].id;
                // dbShow.era_id = yearToEraMapping
                //     .GetValue(show.date.Substring(0, 4), yearToEraMapping["1983-1987"]).id;
                dbShow.updated_at = dbSource.updated_at;

                dbShow = await _setlistShowService.Save(dbShow);

                stats.Updated++;

                addSongs = true;
            }

            // if (addSongs)
            // {
            //     var dbSongs = show.tracks
            //             .SelectMany(localTrack =>
            //                 localTrack.song_ids.Select(song_id =>
            //                     existingSetlistSongs.GetValue(song_id.ToString()))).Where(t => t != null)
            //             .GroupBy(t => t.upstream_identifier).Select(g => g.First()).ToList()
            //         ;

            //     stats += await _setlistShowService.UpdateSongPlays(dbShow, dbSongs);
            // }
        }

        private async Task<Source> ProcessShow(ImportStats stats, Artist artist, string date, IList<LocalShow> fullShows,
            ArtistUpstreamSource src, Source dbSource, PerformContext ctx)
        {

            var sets = new Dictionary<string, SourceSet>();

            // dbSource.has_jamcharts = fullShow.tags.Count(t => t.name == "Jamcharts") > 0;
            dbSource = await _sourceService.Save(dbSource);

            var set = new SourceSet
            {
                source_id = dbSource.id,
                index = 0,
                is_encore = false,
                name = "Set",
                updated_at = dbSource.updated_at
            };

            sets.Add("0", set);

            var taperNotes = new List<string>();

            var setMaps = (await _sourceSetService.UpdateAll(dbSource, sets.Values))
                .GroupBy(s => s.index)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Single());

            var localSet = setMaps.Values.First();
            localSet.tracks = new List<SourceTrack>();

            var tracksAdded = 0;

            foreach (var (idx, fullShow) in fullShows.Select((value, i) => (i, value))) {
                var apiShow =
                    await LocalShowRequest($"{src.upstream_identifier}/{fullShow.dir}/metadata", ctx);

                var tracks = apiShow.tracks.ToList();

                taperNotes.AddRange(apiShow.txts);
        
                foreach (var track in tracks)
                {
                    var localTrackPosition = track.tags.track.no;

                    // we add one to tracksAdded because track positions are not zero-indexed
                    // they start at 1, but tracksAdded is zero indexed 
                    if (localTrackPosition < tracksAdded + 1) localTrackPosition += tracksAdded;

                    var st = new SourceTrack
                    {
                        source_set_id = localSet.id,
                        source_id = dbSource.id,
                        title = track.tags.title,
                        duration = Convert.ToInt16(track.tags.duration),
                        track_position = localTrackPosition,
                        slug = SlugifyTrack(track.tags.title),
                        mp3_url = $"https://audio.relisten.net/{src.upstream_identifier}/{fullShow.dir}/{track.mp3}",
                        updated_at = dbSource.updated_at,
                        artist_id = artist.id
                    };

                    tracksAdded++;

                    localSet.tracks.Add(st);
                }
            }

            dbSource.taper_notes = String.Join("\n", taperNotes.ToArray());

            dbSource = await _sourceService.Save(dbSource);

            stats.Created +=
                (await _sourceTrackService.InsertAll(dbSource, setMaps.SelectMany(kvp => kvp.Value.tracks)))
                .Count();

            await ProcessSetlistShow(stats, fullShows.First(), artist, src, dbSource, sets);

            ResetTrackSlugCounts();

            return dbSource;
        }

        public async Task<ImportStats> ProcessShows(Artist artist, ArtistUpstreamSource src, PerformContext ctx)
        {
            var stats = new ImportStats();

            var prog = ctx?.WriteProgressBar();

            var apiShows =
                await LocalApiRequest<LocalShow>($"{src.upstream_identifier}/metadata", ctx);

            var shows = apiShows.data;

            foreach (var (date, day) in shows)
            {
                try
                {
                    await processDay(date, day);
                }
                catch (Exception e)
                {
                    ctx?.WriteLine($"Error processing show (but continuing): {date}");
                    ctx?.LogException(e);
                }

                // prog?.SetValue(100.0 * (idx / apiShows.total_entries));
            }

            async Task processDay(string date, IList<LocalShow> shows)
            {
                using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
                {
                    var firstShow = shows.First();
                    var dbSource = existingSources.GetValue(date);
                    var venueUpstreamId = $"{firstShow.venue} {firstShow.city}, {firstShow.state}";

                    if (dbSource == null)
                    {
                        dbSource = await ProcessShow(stats, artist, date, shows, src,
                            new Source
                            {
                                updated_at = DateTime.Now,
                                artist_id = artist.id,
                                venue_id = existingVenues[venueUpstreamId].id,
                                display_date = $"{firstShow.year}-{firstShow.month}-{firstShow.day}",
                                upstream_identifier = date,
                                is_soundboard = false,
                                is_remaster = false,
                                description = "",
                                taper_notes = ""
                            }, ctx);

                        existingSources[dbSource.upstream_identifier] = dbSource;

                        stats.Created++;

                        await linkService.AddLinksForSource(dbSource,
                            new[]
                            {
                                new Link
                                {
                                    source_id = dbSource.id,
                                    for_ratings = true,
                                    for_source = false,
                                    for_reviews = true,
                                    upstream_source_id = src.upstream_source_id,
                                    url = "http://phish.net/setlists/?d=" + dbSource.display_date,
                                    label = "View on phish.net"
                                }
                            });
                    }
                    // else if (show.updated_at > dbSource.updated_at)
                    // {
                    //     dbSource.updated_at = show.updated_at;
                    //     dbSource.venue_id = existingVenues[venueUpstreamId].id;
                    //     dbSource.display_date = show.date;
                    //     dbSource.upstream_identifier = show.id.ToString();
                    //     dbSource.is_soundboard = show.sbd;
                    //     dbSource.is_remaster = show.remastered;
                    //     dbSource.description = "";
                    //     dbSource.taper_notes = show.taper_notes;

                    //     dbSource = await ProcessShow(stats, artist, show, src, dbSource, ctx);

                    //     existingSources[dbSource.upstream_identifier] = dbSource;

                    //     stats.Updated++;
                    // }

                    scope.Complete();
                }
            }

            return stats;
        }
    }
}
