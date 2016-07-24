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

            return stats;
            //return await ProcessIdentifiers(artist, await this.http.GetAsync(SearchUrlForArtist(artist)));
        }

        private IDictionary<string, Source> existingSources = new Dictionary<string, Source>();
        private IDictionary<string, Venue> existingVenues = new Dictionary<string, Venue>();
        private IDictionary<string, Tour> existingTours = new Dictionary<string, Tour>();
        private IDictionary<string, SetlistShow> existingSetlistShows = new Dictionary<string, SetlistShow>();
        private IDictionary<string, SetlistSong> existingSetlistSongs = new Dictionary<string, SetlistSong>();

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

        public async Task<ImportStats> ProcessTours(Artist artist)
        {
            
        }
    }
}