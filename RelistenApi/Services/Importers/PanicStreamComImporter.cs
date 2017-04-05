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
using System.IO;
using HtmlAgilityPack;
using System.Globalization;
using System.Net;
using Hangfire.Server;
using Hangfire.Console;

namespace Relisten.Import
{
    public class PanicStreamComImporter : ImporterBase
    {
        public const string DataSourceName = "panicstream.com";

        protected SourceService _sourceService { get; set; }
        protected SourceSetService _sourceSetService { get; set; }
        protected SourceReviewService _sourceReviewService { get; set; }
        protected SourceTrackService _sourceTrackService { get; set; }
        protected VenueService _venueService { get; set; }
        protected TourService _tourService { get; set; }
        protected ILogger<PanicStreamComImporter> _log { get; set; }

        public PanicStreamComImporter(
            DbService db,
            VenueService venueService,
            TourService tourService,
            SourceService sourceService,
            SourceSetService sourceSetService,
            SourceReviewService sourceReviewService,
            SourceTrackService sourceTrackService,
            ILogger<PanicStreamComImporter> log
        ) : base(db)
        {
            this._sourceService = sourceService;
            this._venueService = venueService;
            this._tourService = tourService;
            this._log = log;
            _sourceReviewService = sourceReviewService;
            _sourceTrackService = sourceTrackService;
            _sourceSetService = sourceSetService;
        }

		public override string ImporterName => "panicstream.com";

        public override ImportableData ImportableDataForArtist(Artist artist)
        {
            var r = ImportableData.Sources;

            if (artist.features.per_source_venues)
            {
                r |= ImportableData.Venues;
            }

            return r;
        }

        private static Regex ShowDirMatcher = new Regex(@"href=""([0-9_]+)([a-z]?)\/"">[0-9_]+[a-z]?\/<\/a>\s+([0-9A-Za-z: -]+)\s+-");
        private static Regex TrackPrefixFinder = new Regex(@"^((?:\d{2,3} )|(?:[Ww][sS]?[pP][0-9-]+(?:[.]|EQ)?(?:[dD]\d+)?[tT]\d+\.(?!mp3|m4a|MP3|M4A))|(?:(?:d\d+)?t\d+) )");
        private static Regex Mp3FileFinder = new Regex(@" href=""(.*\.(?:mp3|m4a|MP3|M4A))""");
        private static Regex TxtFileFinder = new Regex(@" href=""(.*\.txt)""");

		private async Task<string> FetchUrl(string url, PerformContext ctx)
        {
            url = url.Replace("&amp;", "&");
			ctx?.WriteLine("Fetching URL: " + url);
            var page = await http.GetAsync(url);

            if (page.StatusCode != HttpStatusCode.OK)
            {
                var e = new Exception("URL fetch failed: " + url);
                e.Data["url"] = url;
                e.Data["StatusCode"] = page.StatusCode;
                throw e;
            }

            return await page.Content.ReadAsStringAsync();
        }

        private static string PanicIndexUrl() { return "http://www.panicstream.com/streams/wsp/?C=M;O=D"; }
        private static string PanicShowUrl(string panicDate)
        {
            return "http://www.panicstream.com/streams/wsp/" + panicDate + "/";
        }
		private static string PanicShowFileUrl(string panicDate, string fileName)
        {
            return PanicShowUrl(panicDate) + fileName;
        }

        public override async Task<ImportStats> ImportDataForArtist(Artist artist, ArtistUpstreamSource src, PerformContext ctx)
        {
            var stats = new ImportStats();

            await PreloadData(artist);

            var contents = await FetchUrl(PanicIndexUrl(), ctx);

			var matches = ShowDirMatcher.Matches(contents);

			ctx?.WriteLine($"Check {matches.Count} subdirectories");
			var prog = ctx?.WriteProgressBar();

			var counter = 1;
            foreach (Match match in ShowDirMatcher.Matches(contents))
            {
                var panicDate = match.Groups[1].Value;
                var panicRecLetter = match.Groups[2].Value;

                // 27-Jul-2016 19:14
                var panicUpdatedAt = DateTime.ParseExact(match.Groups[3].Value.Trim(), "dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture);

                await ProcessShow(stats, artist, panicDate, panicRecLetter, panicUpdatedAt, ctx);

				prog.SetValue(100.0 * counter / matches.Count);

				counter++;
            }

            await RebuildShows(artist);
            await RebuildYears(artist);

            return stats;
        }

        private IDictionary<string, Source> existingSources = new Dictionary<string, Source>();

        async Task PreloadData(Artist artist)
        {
            existingSources = (await _sourceService.AllForArtist(artist)).
                GroupBy(venue => venue.upstream_identifier).
                ToDictionary(grp => grp.Key, grp => grp.First());
        }

		private async Task ProcessShow(ImportStats stats, Artist artist, string panicDate, string panicRecLetter, DateTime panicUpdatedAt, PerformContext ctx)
        {
            var upstreamId = panicDate + panicRecLetter;
            var dbSource = existingSources.GetValue(upstreamId);

            if (dbSource != null && dbSource.updated_at <= panicUpdatedAt)
            {
                return;
            }

			var isUpdate = dbSource != null;

            var showDir = await FetchUrl(PanicShowUrl(upstreamId), ctx);

            if(showDir.Contains("-   \n<hr></pre>") || upstreamId == "1995_06_02" || upstreamId == "2010_04_30")
            {
                return;
            }

            var txt = TxtFileFinder.Matches(showDir)
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Where(m => !m.Contains("ffp") && !m.Contains("FFP"))
                .FirstOrDefault()
                ;

            var mp3matches = Mp3FileFinder.Matches(showDir);

            if (mp3matches.Count == 0)
            {
                throw new Exception("No mp3's or m4a's for " + upstreamId);
            }

            var desc = txt != null ? await FetchUrl(PanicShowFileUrl(upstreamId, txt), ctx) : "";

            var src = new Source
            {
                artist_id = artist.id,
                display_date = panicDate.Replace('_', '-'),
                is_soundboard = false,
                is_remaster = false,
                has_jamcharts = false,
                avg_rating = 0,
                num_reviews = 0,
                avg_rating_weighted = 0,
                upstream_identifier = upstreamId,
                taper_notes = desc,
                updated_at = panicUpdatedAt
            };

			if (isUpdate)
			{
				src.id = dbSource.id;
			}

			dbSource = await _sourceService.Save(dbSource);

            existingSources[dbSource.upstream_identifier] = dbSource;

			if (isUpdate)
			{
				stats.Updated++;
				stats.Removed += await _sourceService.DropAllSetsAndTracksForSource(dbSource);
			}
			else
			{
				stats.Created++;
			}

            var dbSet = await _sourceSetService.Insert(new SourceSet
            {
                source_id = dbSource.id,
                index = 0,
                is_encore = false,
                name = "Default Set",
                updated_at = panicUpdatedAt
            });
            stats.Created++;

            var trackIndex = 0;
            var mp3s = mp3matches
                .Cast<Match>()
                .Select(m =>
                {
                    var fileName = m.Groups[1].Value;

                    var trackName = TrackPrefixFinder
                        .Replace(WebUtility.UrlDecode(fileName), "")
                        .Replace(".mp3", "")
                        .Replace(".MP3", "")
                        .Replace(".M4A", "")
                        .Replace(".m4a", "");

                    trackIndex++;

                    return new SourceTrack
                    {
                        source_id = dbSource.id,
                        source_set_id = dbSet.id,
                        track_position = trackIndex,
                        duration = null,
                        title = trackName,
                        slug = Slugify(trackName),
                        mp3_url = PanicShowFileUrl(upstreamId, fileName),
                        updated_at = panicUpdatedAt
                    };
                });

            await _sourceTrackService.InsertAll(mp3s);
            stats.Created += mp3s.Count();
        }
    }
}