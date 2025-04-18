using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Dapper;
using Hangfire.Console;
using Hangfire.Console.Progress;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Polly;
using Polly.Retry;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Vendor;
using Relisten.Vendor.SetlistFm;
using Artist = Relisten.Api.Models.Artist;
using Tour = Relisten.Api.Models.Tour;
using Venue = Relisten.Api.Models.Venue;

namespace Relisten.Import
{
    public class SetlistFmImporter : ImporterBase
    {
        public const string DataSourceName = "setlist.fm";

        private readonly LinkService _linkService;

        private readonly AsyncRetryPolicy<Tuple<bool, ImportStats>> retryPolicy = Policy
            .Handle<JsonReaderException>()
            .OrResult<Tuple<bool, ImportStats>>(r => false /* never error on the result */)
            //.HandleResult<HttpResponseMessage>(r => r.StatusCode == HttpStatusCode.ServiceUnavailable)
            .WaitAndRetryAsync(7, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));

        private IDictionary<string, SetlistShow> existingSetlistShows = new Dictionary<string, SetlistShow>();
        private IDictionary<string, SetlistSong> existingSetlistSongs = new Dictionary<string, SetlistSong>();
        private IDictionary<string, Tour> existingTours = new Dictionary<string, Tour>();

        private IDictionary<string, VenueWithShowCount> existingVenues = new Dictionary<string, VenueWithShowCount>();

        private IDictionary<string, DateTime> tourToEndDate = new Dictionary<string, DateTime>();

        private IDictionary<string, DateTime> tourToStartDate = new Dictionary<string, DateTime>();

        public SetlistFmImporter(
            DbService db,
            SetlistShowService setlistShowService,
            VenueService venueService,
            TourService tourService,
            SetlistSongService setlistSongService,
            ILogger<SetlistFmImporter> log,
            LinkService linkService,
            RedisService redisService
        ) : base(db, redisService)
        {
            _linkService = linkService;
            _setlistShowService = setlistShowService;
            _venueService = venueService;
            _tourService = tourService;
            _setlistSongService = setlistSongService;
            _log = log;

            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            http.DefaultRequestHeaders.Add("x-api-key", "a8e279f5-3209-4657-97dc-ad26f10d0109");
        }

        protected SetlistShowService _setlistShowService { get; set; }
        protected SetlistSongService _setlistSongService { get; set; }
        protected VenueService _venueService { get; set; }
        protected TourService _tourService { get; set; }
        protected ILogger<SetlistFmImporter> _log { get; set; }

        public override string ImporterName => "setlist.fm";

        public override ImportableData ImportableDataForArtist(Artist artist)
        {
            return ImportableData.Eras
                   | ImportableData.SetlistShowsAndSongs
                   | ImportableData.Tours
                   | ImportableData.Venues;
        }

        public override async Task<ImportStats> ImportDataForArtist(Artist artist, ArtistUpstreamSource src,
            PerformContext ctx)
        {
            var page = 1;
            Tuple<bool, ImportStats> result = null;
            var stats = ImportStats.None;

            await PreloadData(artist);

            var prog = ctx?.WriteProgressBar();

            do
            {
                result = await retryPolicy.ExecuteAsync(async () =>
                {
                    var httpRes = await http.GetAsync(SetlistUrlForArtist(artist, page));
                    return await ProcessSetlistPage(artist, httpRes, ctx, prog);
                });

                // max 10 per second
                await Task.Delay(100);

                page++;

                stats += result.Item2;
            } while (result != null && result.Item1);

            if (artist.features.tours)
            {
                await UpdateTourStartEndDates(artist);
            }

            // update shows
            await RebuildShows(artist);

            // update years
            await RebuildYears(artist);

            return stats;
        }

        public override Task<ImportStats> ImportSpecificShowDataForArtist(Artist artist, ArtistUpstreamSource src,
            string showIdentifier, PerformContext ctx)
        {
            return Task.FromResult(new ImportStats());
        }

        private static string SetlistUrlForArtist(Artist artist, int page = 1)
        {
            return $"https://api.setlist.fm/rest/1.0/artist/{artist.musicbrainz_id}/setlists?p={page}";
        }

        private async Task PreloadData(Artist artist)
        {
            existingVenues = (await _venueService.AllIncludingUnusedForArtist(artist))
                .GroupBy(venue => venue.upstream_identifier).ToDictionary(grp => grp.Key, grp => grp.First());

            existingTours = (await _tourService.AllForArtist(artist))
                .GroupBy(tour => tour.upstream_identifier).ToDictionary(grp => grp.Key, grp => grp.First());

            existingSetlistShows = (await _setlistShowService.AllForArtist(artist))
                .GroupBy(show => show.upstream_identifier).ToDictionary(grp => grp.Key, grp => grp.First());

            existingSetlistSongs = (await _setlistSongService.AllForArtist(artist))
                .GroupBy(song => song.upstream_identifier).ToDictionary(grp => grp.Key, grp => grp.First());
        }

        private async Task<ImportStats> ProcessSetlist(Artist artist, Setlist setlist)
        {
            var stats = new ImportStats();
            var now = DateTime.UtcNow;

            // venue
            Venue dbVenue = existingVenues.GetValue(setlist.venue._iguanaUpstreamId);
            if (dbVenue == null)
            {
                var sc = new VenueWithShowCount
                {
                    updated_at = now,
                    artist_id = artist.id,
                    name = setlist.venue.name,
                    latitude = setlist.venue.city.coords?.lat,
                    longitude = setlist.venue.city.coords?.@long,
                    location = $"{setlist.venue.city.name}, {setlist.venue.city.state}",
                    upstream_identifier = setlist.venue._iguanaUpstreamId,
                    slug = Slugify(setlist.venue.name)
                };

                dbVenue = await _venueService.Save(sc);

                sc.id = dbVenue.id;

                existingVenues[dbVenue.upstream_identifier] = sc;

                stats.Created++;
            }

            // tour
            Tour dbTour = null;
            if (artist.features.tours)
            {
                var tour_upstream = setlist.tour?.name ?? "Not Part of a Tour";
                dbTour = existingTours.GetValue(tour_upstream);
                if (dbTour == null)
                {
                    dbTour = await _tourService.Save(new Tour
                    {
                        updated_at = now,
                        artist_id = artist.id,
                        start_date = null,
                        end_date = null,
                        name = tour_upstream,
                        slug = Slugify(tour_upstream),
                        upstream_identifier = tour_upstream
                    });

                    existingTours[dbTour.upstream_identifier] = dbTour;

                    stats.Created++;
                }
            }

            // show
            var dbShow = existingSetlistShows.GetValue(setlist.id);
            var date = DateTime.ParseExact(setlist.eventDate, "dd-MM-yyyy", null);
            var setlistLastUpdated = setlist.lastUpdated;

            var shouldAddSongs = false;

            if (dbShow == null)
            {
                dbShow = await _setlistShowService.Save(new SetlistShow
                {
                    artist_id = artist.id,
                    updated_at = setlistLastUpdated,
                    date = date,
                    upstream_identifier = setlist.id,
                    venue_id = dbVenue.id,
                    tour_id = artist.features.tours ? dbTour?.id : null
                });

                existingSetlistShows[dbShow.upstream_identifier] = dbShow;

                stats.Created++;

                shouldAddSongs = true;
            }
            else if (setlistLastUpdated > dbShow.updated_at)
            {
                dbShow.artist_id = artist.id;
                dbShow.updated_at = setlistLastUpdated;
                dbShow.date = date;
                dbShow.venue_id = dbVenue.id;
                dbShow.tour_id = dbTour?.id;

                dbShow = await _setlistShowService.Save(dbShow);

                existingSetlistShows[dbShow.upstream_identifier] = dbShow;

                stats.Updated++;

                shouldAddSongs = true;
            }

            // setlist.fm doesn't provide much info about tours so we need to find the start
            // and end date ourselves.
            if (artist.features.tours &&
                (dbTour.start_date == null
                 || dbTour.end_date == null
                 || dbShow.date < dbTour.start_date
                 || dbShow.date > dbTour.end_date))
            {
                if (!tourToStartDate.ContainsKey(dbTour.upstream_identifier)
                    || dbShow.date < tourToStartDate[dbTour.upstream_identifier])
                {
                    tourToStartDate[dbTour.upstream_identifier] = dbShow.date;
                }

                if (!tourToEndDate.ContainsKey(dbTour.upstream_identifier)
                    || dbShow.date > tourToEndDate[dbTour.upstream_identifier])
                {
                    tourToEndDate[dbTour.upstream_identifier] = dbShow.date;
                }
            }

            if (shouldAddSongs)
            {
                var songs = setlist.sets.set.SelectMany(set => set.song)
                    .Select(song => new {Name = song.name, Slug = Slugify(song.name)}).GroupBy(song => song.Slug)
                    .Select(grp => grp.First()).ToList();

                var dbSongs = existingSetlistSongs.Where(kvp => songs.Select(song => song.Slug).Contains(kvp.Key))
                    .Select(kvp => kvp.Value).ToList();

                if (songs.Count != dbSongs.Count)
                {
                    var newSongs = songs.Where(song => dbSongs.Find(dbSong => dbSong.slug == song.Slug) == null)
                        .Select(song => new SetlistSong
                        {
                            artist_id = artist.id,
                            name = song.Name,
                            slug = song.Slug,
                            updated_at = now,
                            upstream_identifier = song.Slug
                        }).ToList();

                    var justAdded = await _setlistSongService.InsertAll(artist, newSongs);
                    dbSongs.AddRange(justAdded);
                    stats.Created += newSongs.Count;

                    foreach (var justAddedSong in justAdded)
                    {
                        existingSetlistSongs[justAddedSong.upstream_identifier] = justAddedSong;
                    }
                }

                stats += await _setlistShowService.UpdateSongPlays(dbShow, dbSongs);
            }

            return stats;
        }

        private async Task<Tuple<bool, ImportStats>> ProcessSetlistPage(Artist artist, HttpResponseMessage res,
            PerformContext ctx, IProgressBar prog)
        {
            var body = await res.Content.ReadAsStringAsync();
            SetlistsRootObject root = null;
            try
            {
                root = JsonConvert.DeserializeObject<SetlistsRootObject>(
                    body,
                    new TolerantListConverter<Song>(),
                    new TolerantListConverter<Set>(),
                    new TolerantListConverter<Setlist>(),
                    new TolerantSetsConverter()
                );
            }
            catch (JsonReaderException)
            {
                ctx?.WriteLine("Failed to parse {0}:\n{1}", res.RequestMessage.RequestUri.ToString(), body);
                throw;
            }

            var stats = new ImportStats();

            var count = 1;
            foreach (var setlist in root.setlist)
            {
                if (setlist.sets.set.Count > 0)
                {
                    var s = new Stopwatch();
                    s.Start();

                    // ctx?.WriteLine("Indexing setlist: {0}/{1}...", artist.name, setlist.eventDate);

                    try
                    {
                        var thisStats = await ProcessSetlist(artist, setlist);

                        s.Stop();
                        // ctx?.WriteLine("...success in {0}! Stats: {1}", s.Elapsed, thisStats);

                        stats += thisStats;
                    }
                    catch (Exception e)
                    {
                        s.Stop();
                        ctx?.WriteLine("{0}/{1}...failed in {2}! Stats: {3}", artist.name, setlist.eventDate, s.Elapsed,
                            e.Message);

                        throw;
                    }

                    prog?.SetValue(((100.0 * (root.page - 1) * root.itemsPerPage) + (count * 1.0)) / root.total);

                    count++;
                }
            }

            var hasMorePages = root.page < Math.Ceiling(1.0 * root.total / root.itemsPerPage);

            return new Tuple<bool, ImportStats>(hasMorePages, stats);
        }

        private async Task UpdateTourStartEndDates(Artist artist)
        {
            await db.WithWriteConnection(con => con.ExecuteAsync(@"
                UPDATE
                    tours
                SET
                    start_date = @startDate,
                    end_date = @endDate
                WHERE
                    artist_id = @artistId
                    AND upstream_identifier = @upstream_identifier
            ", tourToStartDate.Keys.Select(tourUpstreamId =>
            {
                return new
                {
                    startDate = tourToStartDate[tourUpstreamId],
                    endDate = tourToEndDate[tourUpstreamId],
                    artistId = artist.id,
                    upstream_identifier = tourUpstreamId
                };
            })));

            tourToStartDate = new Dictionary<string, DateTime>();
            tourToEndDate = new Dictionary<string, DateTime>();
        }
    }
}
