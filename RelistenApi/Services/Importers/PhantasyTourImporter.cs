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
using Hangfire.Server;
using Hangfire.Console;

namespace Relisten.Import
{
	public class PhantasyTourImporter : ImporterBase
	{
		public const string DataSourceName = "phantasytour.com";

		protected SourceService _sourceService { get; set; }
		protected SourceSetService _sourceSetService { get; set; }
		protected SourceReviewService _sourceReviewService { get; set; }
		protected SourceTrackService _sourceTrackService { get; set; }
		protected VenueService _venueService { get; set; }
		protected TourService _tourService { get; set; }

		public PhantasyTourImporter(
			DbService db,
			VenueService venueService,
			TourService tourService,
			SourceService sourceService,
			SourceSetService sourceSetService,
			SourceReviewService sourceReviewService,
			SourceTrackService sourceTrackService
		) : base(db)
        {
			this._sourceService = sourceService;
			this._venueService = venueService;
			this._tourService = tourService;

			_sourceReviewService = sourceReviewService;
			_sourceTrackService = sourceTrackService;
			_sourceSetService = sourceSetService;
		}

		public override ImportableData ImportableDataForArtist(Artist artist)
		{
			if (!artist.data_source.Contains(DataSourceName)) return ImportableData.Nothing;

			var data = ImportableData.SetlistShowsAndSongs | ImportableData.Tours;

			if (artist.features.per_show_venues)
			{
				data |= ImportableData.Venues;
			}

			return data;
		}

		public override async Task<ImportStats> ImportDataForArtist(Artist artist, PerformContext ctx)
		{
			
		}
	}
}
