using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Dapper;
using Hangfire.Console;
using Hangfire.Server;
using Newtonsoft.Json;
using Polly;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Vendor;
using Relisten.Vendor.PhantasyTour;

namespace Relisten.Import
{
    public class PhantasyTourImporter : ImporterBase
    {
        public const string DataSourceName = "phantasytour.com";

        private const uint ITEMS_PER_PAGE = 100;
        private IDictionary<string, SetlistShow?> existingSetlistShows = new Dictionary<string, SetlistShow?>();
        private IDictionary<string, SetlistSong?> existingSetlistSongs = new Dictionary<string, SetlistSong?>();
        private IDictionary<string, Tour?> existingTours = new Dictionary<string, Tour?>();

        private IDictionary<string, VenueWithShowCount?> existingVenues = new Dictionary<string, VenueWithShowCount?>();
        private readonly Dictionary<string, DateTime> listedDates = new();

        public PhantasyTourImporter(
            DbService db,
            VenueService venueService,
            TourService tourService,
            SetlistShowService setlistShowService,
            SetlistSongService setlistSongService,
            RedisService redisService
        ) : base(db, redisService)
        {
            _setlistSongService = setlistSongService;
            _setlistShowService = setlistShowService;
            _venueService = venueService;
            _tourService = tourService;
        }

        protected VenueService _venueService { get; set; }
        protected TourService _tourService { get; set; }
        protected SetlistShowService _setlistShowService { get; set; }
        protected SetlistSongService _setlistSongService { get; set; }

        public override string ImporterName => DataSourceName;

        public override ImportableData ImportableDataForArtist(Artist artist)
        {
            var data = ImportableData.SetlistShowsAndSongs | ImportableData.Tours;

            if (artist.features.per_show_venues)
            {
                data |= ImportableData.Venues;
            }

            return data;
        }

        private string UrlForArtist(ArtistUpstreamSource src, uint page)
        {
            return
                $"https://www.phantasytour.com/api/bands/{uint.Parse(src.upstream_identifier!)}/shows?pageSize={ITEMS_PER_PAGE}&page={page}";
        }

        private string UrlForShow(int showId)
        {
            return $"https://www.phantasytour.com/api/shows/{showId}/blob";
        }

        private async Task PreloadData(Artist artist)
        {
            existingVenues = (await _venueService.AllIncludingUnusedForArtist(artist))
                .GroupBy(venue => venue.upstream_identifier)
                .ToDictionary(grp => grp.Key, grp => (VenueWithShowCount?)grp.First());

            existingTours = (await _tourService.AllForArtist(artist))
                .GroupBy(tour => tour.upstream_identifier).ToDictionary(grp => grp.Key, grp => (Tour?)grp.First());

            existingSetlistShows = (await _setlistShowService.AllForArtist(artist))
                .GroupBy(show => show.upstream_identifier)
                .ToDictionary(grp => grp.Key, grp => (SetlistShow?)grp.First());

            existingSetlistSongs = (await _setlistSongService.AllForArtist(artist))
                .GroupBy(song => song.upstream_identifier)
                .ToDictionary(grp => grp.Key, grp => (SetlistSong?)grp.First());

            listedDates.Clear();
        }

        private string UpstreamIdentifierForPhantasyTourId(int phantasyTour)
        {
            return "pt:" + phantasyTour;
        }

        public override async Task<ImportStats> ImportDataForArtist(Artist artist, ArtistUpstreamSource src,
            PerformContext? ctx)
        {
            uint page = 1;
            var stats = new ImportStats();

            await PreloadData(artist);

            ctx?.WriteLine($"Requesting page #{page}");

            while (await ImportPage(artist, stats, ctx, await http.GetAsync(UrlForArtist(src, page))))
            {
                page++;

                await Task.Delay(100);

                ctx?.WriteLine($"Requesting page #{page}");
            }

            // The listing carries local dates even for known shows. Repair historical UTC
            // truncation only after every page succeeds, before rebuilding date-based joins.
            var correctedDates = await _setlistShowService.UpdateDates(artist, listedDates);
            stats.Updated += correctedDates;
            ctx?.WriteLine($"Corrected {correctedDates} Phantasy Tour setlist dates");

            ctx?.WriteLine("Updating tour start/end dates");
            await UpdateTourStartEndDates(artist);

            // update shows
            await RebuildShows(artist);

            // update years
            await RebuildYears(artist);

            return stats;
        }

        public override Task<ImportStats> ImportSpecificShowDataForArtist(Artist artist, ArtistUpstreamSource src,
            string? showIdentifier, PerformContext? ctx)
        {
            return Task.FromResult(new ImportStats());
        }

        private async Task<bool> ImportPage(Artist artist, ImportStats stats, PerformContext? ctx,
            HttpResponseMessage res)
        {
            res.EnsureSuccessStatusCode();
            var body = await res.Content.ReadAsStringAsync();
            var json = JsonConvert.DeserializeObject<IList<PhantasyTourShowListing>>(body);

            if (json == null)
            {
                throw new InvalidDataException("Phantasy Tour returned an empty show listing.");
            }

            var prog = ctx?.WriteProgressBar();

            Func<PhantasyTourShowListing, Task> processShow = async show =>
            {
                if (show.id <= 0 || show.dateTime == default)
                    throw new InvalidDataException("Phantasy Tour returned a show without an ID or local date.");

                if (show.dateTime > DateTimeOffset.UtcNow)
                {
                    // future shows can't have recordings
                    return;
                }

                var showId = UpstreamIdentifierForPhantasyTourId(show.id);
                var localDate = show.dateTime.Date;
                if (listedDates.TryGetValue(showId, out var previousDate) && previousDate != localDate)
                    throw new InvalidDataException($"Phantasy Tour returned conflicting dates for {showId}.");
                listedDates[showId] = localDate;

                // Existing dates are reconciled from the listing; fetch full setlists only once.
                if (!existingSetlistShows.ContainsKey(showId))
                {
                    await ImportSingle(artist, stats, ctx, show.id);
                }
            };

            if (prog == null)
            {
                foreach (var show in json)
                {
                    await processShow(show);
                }
            }
            else
            {
                await json.AsyncForEachWithProgress(prog, processShow);
            }

            // once we aren't at the full items per page this is the last page and we should stop
            var shouldContinue = json.Count == ITEMS_PER_PAGE;

            return shouldContinue;
        }

        private async Task ImportSingle(Artist artist, ImportStats stats, PerformContext? ctx, int showId)
        {
            var policy = Policy
                .Handle<JsonReaderException>()
                .WaitAndRetryAsync(5, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt) / 2.0));

            await policy.ExecuteAsync(() => _ImportSingle(artist, stats, ctx, showId));
        }

        private async Task _ImportSingle(Artist artist, ImportStats stats, PerformContext? ctx, int showId)
        {
            ctx?.WriteLine($"Requesting page for show id {showId}");

            var res = await http.GetAsync(UrlForShow(showId));
            res.EnsureSuccessStatusCode();

            var body = await res.Content.ReadAsStringAsync();

            ctx?.WriteLine($"Result: [{res.StatusCode}]: {body.Length}");

            var json = JsonConvert.DeserializeObject<PhantasyTourEnvelope>(body)?.data;

            var now = DateTime.UtcNow;

            if (json == null || json.id != showId || json.dateTime == default)
            {
                throw new InvalidDataException($"Phantasy Tour returned invalid details for show {showId}.");
            }

            Venue? dbVenue = existingVenues.GetValue(UpstreamIdentifierForPhantasyTourId(json.venue.id));
            if (dbVenue == null)
            {
                var sc = new VenueWithShowCount
                {
                    updated_at = now,
                    artist_id = artist.id,
                    name = json.venue.name,
                    latitude = null,
                    longitude = null,
                    location = $"{json.venue.city}, {json.venue.state}, {json.venue.country}",
                    upstream_identifier = UpstreamIdentifierForPhantasyTourId(json.venue.id),
                    slug = Slugify($"{json.venue.name}, {json.venue.city}, {json.venue.state}")
                };

                dbVenue = await _venueService.Save(sc);

                sc.id = dbVenue.id;

                existingVenues[dbVenue.upstream_identifier] = sc;

                stats.Created++;
            }

            ctx?.WriteLine($"Importing show id {showId}");

            // tour
            Tour? dbTour = null;
            if (artist.features.tours)
            {
                var tour_name = json.tour?.name ?? "Not Part of a Tour";
                var tour_upstream = UpstreamIdentifierForPhantasyTourId(json.tour?.id ?? -1);

                dbTour = existingTours.GetValue(tour_upstream);
                if (dbTour == null)
                {
                    dbTour = await _tourService.Save(new Tour
                    {
                        updated_at = now,
                        artist_id = artist.id,
                        start_date = null,
                        end_date = null,
                        name = tour_name,
                        slug = Slugify(tour_name),
                        upstream_identifier = tour_upstream
                    });

                    existingTours[dbTour.upstream_identifier] = dbTour!;

                    stats.Created++;
                }
            }

            var dbShow = existingSetlistShows.GetValue(UpstreamIdentifierForPhantasyTourId(json.id));
            var date = json.dateTime.Date;

            if (dbShow == null)
            {
                dbShow = await _setlistShowService.Save(new SetlistShow
                {
                    artist_id = artist.id,
                    updated_at = now,
                    date = date,
                    upstream_identifier = UpstreamIdentifierForPhantasyTourId(json.id),
                    venue_id = dbVenue!.id,
                    tour_id = artist.features.tours ? dbTour?.id : null
                });

                existingSetlistShows[dbShow.upstream_identifier] = dbShow!;

                stats.Created++;
            }

            var songs = json.sets.SelectMany(set => set.songs)
                .Select(song => new {Name = song.name, Slug = Slugify(song.name)}).GroupBy(song => song.Slug)
                .Select(grp => grp.First()).ToList();

            var dbSongs = existingSetlistSongs.Where(kvp => songs.Select(song => song.Slug).Contains(kvp.Key))
                .Select(kvp => kvp.Value)
                .Where(song => song != null)
                .Select(song => song!)
                .ToList();

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

            stats += await _setlistShowService.UpdateSongPlays(dbShow!, dbSongs);
        }

        private async Task UpdateTourStartEndDates(Artist artist)
        {
            await db.WithWriteConnection(con => con.ExecuteAsync(@"
                UPDATE tours t
                SET start_date = dates.start_date, end_date = dates.end_date
                FROM (
                    SELECT tour_id, MIN(date) AS start_date, MAX(date) AS end_date
                    FROM setlist_shows
                    WHERE artist_id = @artistId AND tour_id IS NOT NULL
                    GROUP BY tour_id
                ) dates
                WHERE t.id = dates.tour_id AND t.artist_id = @artistId
                    AND (t.start_date, t.end_date) IS DISTINCT FROM (dates.start_date, dates.end_date)
            ", new { artistId = artist.id }));
        }
    }
}
