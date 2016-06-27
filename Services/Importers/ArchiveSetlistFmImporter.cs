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

namespace Relisten.Import
{
    [Flags]
    public enum ImportableData
    {
        Nothing = 0,
        Eras = 1 << 0,
        Tours = 1 << 1,
        Venues = 1 << 2,
        SetlistShowsAndSongs = 1 << 3,
        SourceReviews = 1 << 4,
        SourceRatings = 1 << 5,
        Sources = 1 << 6
    }

    public class ImportStats
    {
        public static readonly ImportStats None = new ImportStats();

        public int Updated { get; set; } = 0;
        public int Created { get; set; } = 0;
        public int Removed { get; set; } = 0;

        public static ImportStats operator +(ImportStats c1, ImportStats c2)
        {
            return new ImportStats()
            {
                Updated = c1.Updated + c2.Updated,
                Removed = c1.Removed + c2.Removed,
                Created = c1.Created + c2.Created
            };
        }

        public override string ToString()
        {
            return $"Created: {Created}; Updated: {Updated}; Removed: {Removed}";
        }
    }

    public abstract class ImporterBase : IDisposable
    {
        protected IDbConnection db { get; set; }
        protected HttpClient http { get; set; }

        public ImporterBase(DbService db)
        {
            this.db = db.connection;
            this.http = new HttpClient();
        }

        public abstract ImportableData ImportableDataForArtist(Artist artist);
        public abstract Task<ImportStats> ImportDataForArtist(Artist artist);

        public void Dispose()
        {
            this.http.Dispose();
        }

        public string Slugify(string full)
        {
            var slug = Regex.Replace(full.ToLower().Normalize(), @"['.]", "");
            slug = Regex.Replace(slug, @"[^a-z0-9\s-]", " ");

            return Regex.Replace(slug, @"\s+", " ").
                Trim().
                Replace(" ", "-");
        }

        public async Task<ImportStats> RebuildYears()
        {
            return ImportStats.None;
        }
        public async Task<ImportStats> RebuildShows()
        {
            return ImportStats.None;
        }
    }

    public class ArchiveOrgImporter
    {

    }

    public class SetlistFmImporter : ImporterBase
    {
        public const string DataSourceName = "archive.org + setlist.fm";

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

        public override ImportableData ImportableDataForArtist(Artist artist)
        {
            if (artist.data_source != DataSourceName) return ImportableData.Nothing;

            return ImportableData.Eras
             | ImportableData.SetlistShowsAndSongs
             | ImportableData.Tours
             | ImportableData.Venues;
        }

        public override async Task<ImportStats> ImportDataForArtist(Artist artist)
        {
            int page = 1;
            Tuple<bool, ImportStats> result = null;
            var stats = ImportStats.None;

            do
            {
                result = await processSetlistPage(artist, await this.http.GetAsync(setlistUrlForArtist(artist, page)));

                page++;

                stats += result.Item2;
            } while (result != null && result.Item1);

            if (artist.features.tours)
            {
                await updateTourStartEndDates(artist);
            }

            return stats;
        }

        protected SetlistShowService _setlistShowService { get; set; }
        protected SetlistSongService _setlistSongService { get; set; }
        protected VenueService _venueService { get; set; }
        protected TourService _tourService { get; set; }
        protected ILogger<SetlistFmImporter> _log { get; set; }

        string setlistUrlForArtist(Artist artist, int page = 1)
        {
            return $"http://api.setlist.fm/rest/0.1/artist/{artist.musicbrainz_id}/setlists.json?p={page}";
        }

        private IDictionary<string, DateTime> tourToStartDate = new Dictionary<string, DateTime>();
        private IDictionary<string, DateTime> tourToEndDate = new Dictionary<string, DateTime>();

        async Task<ImportStats> processSetlist(Artist artist, Relisten.Vendor.SetlistFm.Setlist setlist)
        {
            var stats = new ImportStats();
            var now = DateTime.UtcNow;

            // venue
            var dbVenue = await _venueService.ForGlobalUpstreamIdentifier("setlistfm:" + setlist.venue.id);
            if (dbVenue == null)
            {
                dbVenue = await _venueService.Save(new Venue()
                {
                    updated_at = now,
                    artist_id = null,
                    name = setlist.venue.name,
                    latitude = setlist.venue.city.coords?.latitude,
                    longitude = setlist.venue.city.coords?.longitude,
                    location = $"{setlist.venue.city.name}, {setlist.venue.city.state}",
                    upstream_identifier = "setlistfm:" + setlist.venue.id
                });

                stats.Created++;
            }

            // tour
            Tour dbTour = null;

            if (artist.features.tours)
            {
                var tour_upstream = setlist.tour ?? "Not Part of a Tour";
                dbTour = await _tourService.ForUpstreamIdentifier(artist, tour_upstream);
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

                    stats.Created++;
                }
            }

            // show
            var dbShow = await _setlistShowService.ForUpstreamIdentifier(artist, setlist.id);
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

                stats.Created++;

                shouldAddSongs = true;
            }
            else if (setlistLastUpdated > dbShow.updated_at)
            {
                dbShow.artist_id = artist.id;
                dbShow.updated_at = setlistLastUpdated;
                dbShow.date = date;
                dbShow.venue_id = dbVenue.id;
                dbShow.tour_id = dbTour.id;

                dbShow = await _setlistShowService.Save(dbShow);

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
                    Select(song => song.name).
                    ToList();

                var dbSongs = (await _setlistSongService.ForUpstreamIdentifiers(artist, songs)).ToList();

                if (songs.Count != dbSongs.Count)
                {
                    var newSongs = songs.
                        Where(songName => dbSongs.Find(dbSong => dbSong.upstream_identifier == songName) == null).
                        Select(songName => new SetlistSong()
                        {
                            artist_id = artist.id,
                            name = songName,
                            slug = Slugify(songName),
                            updated_at = now,
                            upstream_identifier = songName
                        });

                    foreach (var newSong in newSongs)
                    {
                        dbSongs.Add(await _setlistSongService.Save(newSong));

                        stats.Created++;
                    }
                }

                stats.Created += await _setlistShowService.AddSongPlays(dbShow, dbSongs);
            }

            return stats;
        }

        async Task<Tuple<bool, ImportStats>> processSetlistPage(Artist artist, HttpResponseMessage res)
        {
            var body = await res.Content.ReadAsStringAsync();
            var root = JsonConvert.DeserializeObject<Relisten.Vendor.SetlistFm.SetlistsRootObject>(
                body,
                new Vendor.SetlistFm.TolerantListConverter<Vendor.SetlistFm.Song>(),
                new Vendor.SetlistFm.TolerantListConverter<Vendor.SetlistFm.Set>(),
                new Vendor.SetlistFm.TolerantSetsConverter()
            );

            var stats = new ImportStats();

            foreach (var setlist in root.setlists.setlist)
            {
                if (setlist.sets.set.Count > 0)
                {
                    var trans = db.BeginTransaction();

                    Stopwatch s = new Stopwatch();
                    s.Start();

                    _log.LogDebug("Indexing setlist: {0}/{1}...", artist.name, setlist.eventDate);

                    try {
                        var thisStats = await processSetlist(artist, setlist);

                        trans.Commit();

                        s.Stop();
                        _log.LogDebug("...success in {0}! Stats: {1}", s.Elapsed, thisStats);

                        stats += thisStats;
                    }
                    catch(Exception e) {
                        trans.Rollback();

                        s.Stop();
                        _log.LogDebug("...failed in {0}! Stats: {1}", s.Elapsed, e.Message);

                        throw e;
                    }
                }
            }

            var hasMorePages = root.setlists.page < Math.Ceiling(1.0 * root.setlists.total / root.setlists.itemsPerPage);

            return new Tuple<bool, ImportStats>(hasMorePages, stats);
        }

        async Task updateTourStartEndDates(Artist artist)
        {
            await db.ExecuteAsync(@"
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
            }));

            tourToStartDate = new Dictionary<string, DateTime>();
            tourToEndDate = new Dictionary<string, DateTime>();
        }
    }
    public class ArchiveOrgSetlistFmImporter
    {

    }
}
