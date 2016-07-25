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
using System.Globalization;
using Relisten.Vendor.Phishin;

namespace Relisten.Import
{
    public class PhishinImporter : ImporterBase
    {
        public const string DataSourceName = "phish.in";

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

        public PhishinImporter(
            DbService db,
            VenueService venueService,
            TourService tourService,
            SourceService sourceService,
            SourceSetService sourceSetService,
            SourceReviewService sourceReviewService,
            SourceTrackService sourceTrackService,
            SetlistSongService setlistSongService,
            SetlistShowService setlistShowService,
            EraService eraService,
            ILogger<PhishinImporter> log
        ) : base(db)
        {
            this._setlistSongService = setlistSongService;
            this._setlistShowService = setlistShowService;
            this._sourceService = sourceService;
            this._venueService = venueService;
            this._tourService = tourService;
            this._log = log;
            _sourceReviewService = sourceReviewService;
            _sourceTrackService = sourceTrackService;
            _sourceSetService = sourceSetService;
            _eraService = eraService;
        }

        public override ImportableData ImportableDataForArtist(Artist artist)
        {
            if (!artist.data_source.Contains(DataSourceName)) return ImportableData.Nothing;

            return ImportableData.Sources
             | ImportableData.Venues
             | ImportableData.Tours
             | ImportableData.Eras
             | ImportableData.SetlistShowsAndSongs
             ;
        }

        public override async Task<ImportStats> ImportDataForArtist(Artist artist)
        {
            await PreloadData(artist);

            var stats = new ImportStats();

            stats += await ProcessTours(artist);
            stats += await ProcessSongs(artist);
            stats += await ProcessVenues(artist);
            stats += await ProcessShows(artist);
            stats += await ProcessEras(artist);

            return stats;
            //return await ProcessIdentifiers(artist, await this.http.GetAsync(SearchUrlForArtist(artist)));
        }

        private IDictionary<string, Source> existingSources = new Dictionary<string, Source>();
        private IDictionary<string, Era> existingEras = new Dictionary<string, Era>();
        private IDictionary<string, Venue> existingVenues = new Dictionary<string, Venue>();
        private IDictionary<string, Tour> existingTours = new Dictionary<string, Tour>();
        private IDictionary<string, SetlistShow> existingSetlistShows = new Dictionary<string, SetlistShow>();
        private IDictionary<string, SetlistSong> existingSetlistSongs = new Dictionary<string, SetlistSong>();

        private IDictionary<string, Era> yearToEraMapping = new Dictionary<string, Era>();

        async Task PreloadData(Artist artist)
        {
            existingSources = (await _sourceService.AllForArtist(artist)).
                GroupBy(venue => venue.upstream_identifier).
                ToDictionary(grp => grp.Key, grp => grp.First());

            existingVenues = (await _venueService.AllForArtist(artist)).
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

        private string PhishinApiUrl(string api)
        {
            return $"http://phish.in/api/v1/{api}.json?per_page=99999";
        }

        private async Task<T> PhishinApiRequest<T>(string apiRoute)
        {
            var resp = await http.GetAsync(apiRoute);
            return JsonConvert.DeserializeObject<PhishinRootObject<T>>(await resp.Content.ReadAsStringAsync()).data;
        }

        public async Task<ImportStats> ProcessTours(Artist artist)
        {
            var stats = new ImportStats();

            foreach (var tour in await PhishinApiRequest<IEnumerable<PhishinSmallTour>>("tours"))
            {
                var dbTour = existingTours.GetValue(tour.id.ToString());

                if (dbTour == null)
                {
                    dbTour = await _tourService.Save(new Tour()
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
                else
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

        public async Task<ImportStats> ProcessEras(Artist artist)
        {
            var stats = new ImportStats();

            var order = 0;

            foreach (var era in await PhishinApiRequest<IDictionary<string, IList<string>>>("eras"))
            {
                var dbEra = existingEras.GetValue(era.Key);

                if (dbEra == null)
                {
                    dbEra = await _eraService.Save(new Era()
                    {
                        artist_id = artist.id,
                        name = era.Key,
                        order = order,
                        updated_at = DateTime.Now
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

        public async Task<ImportStats> ProcessSongs(Artist artist)
        {
            var stats = new ImportStats();

            var songsToSave = new List<SetlistSong>();

            foreach (var song in await PhishinApiRequest<IEnumerable<PhishinSmallSong>>("songs"))
            {
                var dbSong = existingSetlistSongs.GetValue(song.id.ToString());

                // skip aliases for now
                if (dbSong == null && song.alias_for.HasValue == false)
                {
                    dbSong = new SetlistSong()
                    {
                        updated_at = song.updated_at,
                        artist_id = artist.id,
                        name = song.title,
                        slug = Slugify(song.title),
                        upstream_identifier = song.id.ToString()
                    };

                    existingSetlistSongs[dbSong.upstream_identifier] = dbSong;

                    stats.Created++;
                }
            }

            var newSongs = await _setlistSongService.InsertAll(artist, songsToSave);

            return stats;
        }

        public async Task<ImportStats> ProcessVenues(Artist artist)
        {
            var stats = new ImportStats();

            foreach (var venue in await PhishinApiRequest<IEnumerable<PhishinSmallVenue>>("venues"))
            {
                var dbVenue = existingVenues.GetValue(venue.id.ToString());

                if (dbVenue == null)
                {
                    dbVenue = await _venueService.Save(new Venue()
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
                    });

                    existingVenues[dbVenue.upstream_identifier] = dbVenue;

                    stats.Created++;
                }
                else
                {
                    dbVenue.name = venue.name;
                    dbVenue.location = venue.location;
                    dbVenue.longitude = venue.longitude;
                    dbVenue.latitude = venue.latitude;
                    dbVenue.past_names = venue.past_names;
                    dbVenue.updated_at = venue.updated_at;

                    dbVenue = await _venueService.Save(dbVenue);

                    existingVenues[dbVenue.upstream_identifier] = dbVenue;

                    stats.Updated++;
                }
            }

            return stats;
        }

        public async Task<ImportStats> ProcessShows(Artist artist)
        {
            var stats = new ImportStats();

            foreach (var show in await PhishinApiRequest<IEnumerable<PhishinSmallShow>>("shows"))
            {
                var dbSource = existingSources.GetValue(show.id.ToString());

                if (dbSource == null)
                {
                    dbSource = await _venueService.Save(new Venue()
                    {
                        updated_at = show.updated_at,
                        artist_id = artist.id,
                        name = show.name,
                        location = show.location,
                        slug = Slugify(show.name),
                        latitude = show.latitude,
                        longitude = show.longitude,
                        past_names = show.past_names,
                        upstream_identifier = show.id.ToString()
                    });

                    existingVenues[dbSource.upstream_identifier] = dbSource;

                    stats.Created++;
                }
                else
                {
                    dbSource.name = show.name;
                    dbSource.location = show.location;
                    dbSource.longitude = show.longitude;
                    dbSource.latitude = show.latitude;
                    dbSource.past_names = show.past_names;
                    dbSource.updated_at = show.updated_at;

                    dbSource = await _venueService.Save(dbSource);

                    existingVenues[dbSource.upstream_identifier] = dbSource;

                    stats.Updated++;
                }
            }

            return stats;
        }
    }
}