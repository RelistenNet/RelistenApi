using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Dapper;
using Relisten.Vendor;
using Relisten.Data;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Hangfire.Server;
using Hangfire.Console;
using Hangfire.Console.Progress;

namespace Relisten.Import
{
    public class SetlistFmImporter : ImporterBase
    {
        public const string DataSourceName = "setlist.fm";

        protected SetlistShowService _setlistShowService { get; set; }
        protected SetlistSongService _setlistSongService { get; set; }
        protected VenueService _venueService { get; set; }
        protected TourService _tourService { get; set; }
        protected ILogger<SetlistFmImporter> _log { get; set; }

        public SetlistFmImporter(
            DbService db,
            SetlistShowService setlistShowService,
            VenueService venueService,
            TourService tourService,
            SetlistSongService setlistSongService,
            ILogger<SetlistFmImporter> log
        ) : base(db)
        {
            this._setlistShowService = setlistShowService;
            this._venueService = venueService;
            this._tourService = tourService;
            this._setlistSongService = setlistSongService;
            this._log = log;
        }

		public override string ImporterName => "setlist.fm";

        public override ImportableData ImportableDataForArtist(Artist artist)
        {
            return ImportableData.Eras
             | ImportableData.SetlistShowsAndSongs
             | ImportableData.Tours
             | ImportableData.Venues;
        }

        public override async Task<ImportStats> ImportDataForArtist(Artist artist, ArtistUpstreamSource src, PerformContext ctx)
        {
            int page = 1;
            Tuple<bool, ImportStats> result = null;
            var stats = ImportStats.None;

            await PreloadData(artist);

			var prog = ctx?.WriteProgressBar();

            do
            {
                result = await ProcessSetlistPage(artist, await this.http.GetAsync(SetlistUrlForArtist(artist, page)), ctx, prog);

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

        private static string SetlistUrlForArtist(Artist artist, int page = 1)
        {
            return $"http://api.setlist.fm/rest/0.1/artist/{artist.musicbrainz_id}/setlists.json?p={page}";
        }

        private IDictionary<string, DateTime> tourToStartDate = new Dictionary<string, DateTime>();
        private IDictionary<string, DateTime> tourToEndDate = new Dictionary<string, DateTime>();

        private IDictionary<string, Venue> existingVenues = new Dictionary<string, Venue>();
        private IDictionary<string, Tour> existingTours = new Dictionary<string, Tour>();
        private IDictionary<string, SetlistShow> existingSetlistShows = new Dictionary<string, SetlistShow>();
        private IDictionary<string, SetlistSong> existingSetlistSongs = new Dictionary<string, SetlistSong>();

        async Task PreloadData(Artist artist)
        {
            existingVenues = (await _venueService.AllIncludingUnusedForArtist(artist)).
                GroupBy(venue => venue.upstream_identifier).
                ToDictionary(grp => grp.Key, grp => grp.First());

            existingTours = (await _tourService.AllForArtist(artist)).
                GroupBy(tour => tour.upstream_identifier).
                ToDictionary(grp => grp.Key, grp => grp.First());

            existingSetlistShows = (await _setlistShowService.AllForArtist(artist)).
                GroupBy(show => show.upstream_identifier).
                ToDictionary(grp => grp.Key, grp => grp.First());

            existingSetlistSongs = (await _setlistSongService.AllForArtist(artist)).
                GroupBy(song => song.upstream_identifier).
                ToDictionary(grp => grp.Key, grp => grp.First());
        }

        async Task<ImportStats> ProcessSetlist(Artist artist, Relisten.Vendor.SetlistFm.Setlist setlist)
        {
            var stats = new ImportStats();
            var now = DateTime.UtcNow;

            // venue
            var dbVenue = existingVenues.GetValue(setlist.venue._iguanaUpstreamId);
            if (dbVenue == null)
            {
                dbVenue = await _venueService.Save(new Venue()
                {
                    updated_at = now,
                    artist_id = artist.id,
                    name = setlist.venue.name,
                    latitude = setlist.venue.city.coords?.latitude,
                    longitude = setlist.venue.city.coords?.longitude,
                    location = $"{setlist.venue.city.name}, {setlist.venue.city.state}",
                    upstream_identifier = setlist.venue._iguanaUpstreamId,
                    slug = Slugify(setlist.venue.name)
                });

                existingVenues[dbVenue.upstream_identifier] = dbVenue;

                stats.Created++;
            }

            // tour
            Tour dbTour = null;
            if (artist.features.tours)
            {
                var tour_upstream = setlist.tour ?? "Not Part of a Tour";
                dbTour = existingTours.GetValue(tour_upstream);
                if (dbTour == null)
                {
                    dbTour = await _tourService.Save(new Tour()
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
            var setlistLastUpdated = DateTime.Parse(setlist.lastUpdated);

            var shouldAddSongs = false;

            if (dbShow == null)
            {
                dbShow = await _setlistShowService.Save(new SetlistShow()
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
                stats.Removed += await _setlistShowService.RemoveSongPlays(dbShow);

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
                var songs = setlist.sets.set.
                    SelectMany(set => set.song).
                    Select(song => new { Name = song.name, Slug = Slugify(song.name) }).
                    GroupBy(song => song.Slug).
                    Select(grp => grp.First()).
                    ToList();

                var dbSongs = existingSetlistSongs.
                    Where(kvp => songs.Select(song => song.Slug).Contains(kvp.Key)).
                    Select(kvp => kvp.Value).
                    ToList();

                if (songs.Count != dbSongs.Count)
                {
                    var newSongs = songs.
                        Where(song => dbSongs.Find(dbSong => dbSong.slug == song.Slug) == null).
                        Select(song => new SetlistSong()
                        {
                            artist_id = artist.id,
                            name = song.Name,
                            slug = song.Slug,
                            updated_at = now,
                            upstream_identifier = song.Slug
                        }).
                        ToList();

                    var justAdded = await _setlistSongService.InsertAll(artist, newSongs);
                    dbSongs.AddRange(justAdded);
                    stats.Created += newSongs.Count;

                    foreach (var justAddedSong in justAdded)
                    {
                        existingSetlistSongs[justAddedSong.upstream_identifier] = justAddedSong;
                    }
                }

                stats.Created += await _setlistShowService.AddSongPlays(dbShow, dbSongs);
            }

            return stats;
        }

		async Task<Tuple<bool, ImportStats>> ProcessSetlistPage(Artist artist, HttpResponseMessage res, PerformContext ctx, IProgressBar prog)
        {
            var body = await res.Content.ReadAsStringAsync();
            Relisten.Vendor.SetlistFm.SetlistsRootObject root = null;
            try
            {
                root = JsonConvert.DeserializeObject<Relisten.Vendor.SetlistFm.SetlistsRootObject>(
                    body,
                    new Vendor.SetlistFm.TolerantListConverter<Vendor.SetlistFm.Song>(),
                    new Vendor.SetlistFm.TolerantListConverter<Vendor.SetlistFm.Set>(),
                    new Vendor.SetlistFm.TolerantListConverter<Vendor.SetlistFm.Setlist>(),
                    new Vendor.SetlistFm.TolerantSetsConverter()
                );
            }
            catch (JsonReaderException e)
            {
				ctx?.WriteLine("Failed to parse {0}:\n{1}", res.RequestMessage.RequestUri.ToString(), body);
                throw e;
            }

            var stats = new ImportStats();

			var count = 1;
            foreach (var setlist in root.setlists.setlist)
            {
                if (setlist.sets.set.Count > 0)
                {
                    Stopwatch s = new Stopwatch();
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
						ctx?.WriteLine("{0}/{1}...failed in {2}! Stats: {3}", artist.name, setlist.eventDate, s.Elapsed, e.Message);

                        throw e;
                    }

					prog?.SetValue(((100.0 * (root.setlists.page - 1) * root.setlists.itemsPerPage) + count * 1.0) / root.setlists.total);

					count++;
                }
            }

            var hasMorePages = root.setlists.page < Math.Ceiling(1.0 * root.setlists.total / root.setlists.itemsPerPage);

            return new Tuple<bool, ImportStats>(hasMorePages, stats);
        }

        async Task UpdateTourStartEndDates(Artist artist)
        {
            await db.WithConnection(con => con.ExecuteAsync(@"
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