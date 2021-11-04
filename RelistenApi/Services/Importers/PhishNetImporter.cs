using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Hangfire.Console;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Relisten.Data;

namespace Relisten.Import
{
    public class PhishNetImporter : ImporterBase
    {
        public const string DataSourceName = "phish.net";

        private static readonly Regex PhishNetRatingScraper =
            new(@"Overall: (?<AverageRating>[\d.]+)\/5 \((?<VotesCast>\d+) ratings\)");

        private static readonly Regex PhishNetReviewCountScraper = new(@"class='tpc-comment review'");

        private readonly LinkService linkService;

        public PhishNetImporter(
            DbService db,
            SourceService sourceService,
            SourceReviewService sourceReviewService,
            LinkService linkService,
            ILogger<PhishNetImporter> log,
            RedisService redisService
        ) : base(db, redisService)
        {
            this.linkService = linkService;
            _sourceService = sourceService;
            _log = log;
            _sourceReviewService = sourceReviewService;
        }

        protected SourceService _sourceService { get; set; }
        protected SourceReviewService _sourceReviewService { get; set; }
        protected ILogger<PhishNetImporter> _log { get; set; }

        public override string ImporterName => "phish.net";

        public override ImportableData ImportableDataForArtist(Artist artist)
        {
            return ImportableData.SourceRatings
                   | ImportableData.SourceReviews
                   | ImportableData.Sources;
        }

        public override async Task<ImportStats> ImportDataForArtist(Artist artist, ArtistUpstreamSource src,
            PerformContext ctx)
        {
            var stats = new ImportStats();

            var shows = (await _sourceService.AllForArtist(artist)).OrderBy(s => s.display_date).ToList();

            var prog = ctx?.WriteProgressBar();

            ctx?.WriteLine($"Processing {shows.Count} shows");

            await shows.ForEachAsync(async dbSource =>
            {
                stats += await ProcessSource(artist, src, dbSource, ctx);
            }, prog, 1);

            ctx?.WriteLine("Rebuilding...");

            await RebuildShows(artist);
            await RebuildYears(artist);

            return stats;
        }

        public override Task<ImportStats> ImportSpecificShowDataForArtist(Artist artist, ArtistUpstreamSource src,
            string showIdentifier, PerformContext ctx)
        {
            return Task.FromResult(new ImportStats());
        }

        private string PhishNetUrlForSource(Source dbSource)
        {
            return "http://phish.net/setlists/?d=" + dbSource.display_date;
        }

        private string PhishNetApiReviewsUrlForSource(Source dbSource)
        {
            return
                "https://api.phish.net/api.js?api=2.0&method=pnet.reviews.query&format=json&apikey=B6570BEDA805B616AB6C&showdate=" +
                dbSource.display_date;
        }

        private string PhishNetApiSetlistUrlForSource(Source dbSource)
        {
            return
                "https://api.phish.net/api.js?api=2.0&method=pnet.shows.setlists.get&format=json&apikey=C60F490D1358FBBE31DA&showdate=" +
                dbSource.display_date;
        }

        private int TryParseInt(string str)
        {
            var i = 0;
            int.TryParse(str, out i);
            return i;
        }

        private double TryParseDouble(string str)
        {
            double i = 0;
            double.TryParse(str, out i);
            return i;
        }

        private async Task<PhishNetScrapeResults> ScrapePhishNetForSource(Source dbSource, PerformContext ctx)
        {
            var url = PhishNetUrlForSource(dbSource);
            ctx?.WriteLine($"Requesting {url}");
            var resp = await http.GetAsync(url);
            var page = await resp.Content.ReadAsStringAsync();

            var ratingMatches = PhishNetRatingScraper.Match(page);

            return new PhishNetScrapeResults
            {
                RatingAverage = TryParseDouble(ratingMatches.Groups["AverageRating"].Value),
                RatingVotesCast = TryParseInt(ratingMatches.Groups["VotesCast"].Value),
                NumberOfReviewsWritten = PhishNetReviewCountScraper.Matches(page).Count
            };
        }

        private async Task<PhishNetApiSetlist> GetPhishNetApiSetlist(Source dbSource, PerformContext ctx)
        {
            var url = PhishNetApiSetlistUrlForSource(dbSource);
            ctx?.WriteLine($"Requesting {url}");
            var resp = await http.GetAsync(url);
            var page = await resp.Content.ReadAsStringAsync();

            if (page.Length == 0)
            {
                return null;
            }

            var setlists = JsonConvert.DeserializeObject<IEnumerable<PhishNetApiSetlist>>(page);

            return setlists.Where(setlist => setlist.artist_name == "Phish").FirstOrDefault();
        }

        private async Task<IEnumerable<PhishNetApiReview>> GetPhishNetApiReviews(Source dbSource, PerformContext ctx)
        {
            var url = PhishNetApiReviewsUrlForSource(dbSource);
            ctx?.WriteLine($"Requesting {url}");
            var resp = await http.GetAsync(url);
            var page = await resp.Content.ReadAsStringAsync();

            // some shows have no reviews
            if (page.Length == 0 || page[0] == '{')
            {
                return new List<PhishNetApiReview>();
            }

            return JsonConvert.DeserializeObject<IEnumerable<PhishNetApiReview>>(page);
        }

        private async Task<ImportStats> ProcessSource(Artist artist, ArtistUpstreamSource src, Source dbSource,
            PerformContext ctx)
        {
            var stats = new ImportStats();

            var ratings = await ScrapePhishNetForSource(dbSource, ctx);
            var dirty = false;

            if (dbSource.num_ratings != ratings.RatingVotesCast)
            {
                dbSource.num_ratings = ratings.RatingVotesCast;
                dbSource.avg_rating = ratings.RatingAverage * 2.0;

                dirty = true;
            }

            if (dbSource.num_reviews != ratings.NumberOfReviewsWritten)
            {
                var reviewsTask = GetPhishNetApiReviews(dbSource, ctx);
                var setlistTask = GetPhishNetApiSetlist(dbSource, ctx);

                await Task.WhenAll(reviewsTask, setlistTask);

                var dbReviews = reviewsTask.Result.Select(rev =>
                {
                    return new SourceReview
                    {
                        rating = null,
                        title = null,
                        review = rev.review,
                        author = rev.author,
                        updated_at = DateTimeOffset.FromUnixTimeSeconds(rev.tstamp).UtcDateTime
                    };
                }).ToList();

                dbSource.num_reviews = dbReviews.Count();
                dbSource.description = setlistTask.Result.setlistnotes + "\n\n\n" + setlistTask.Result.setlistdata;

                dirty = true;

                await ReplaceSourceReviews(stats, dbSource, dbReviews);
            }

            if (dirty)
            {
                await _sourceService.Save(dbSource);
                stats.Updated++;
            }

            stats.Created += (await linkService.AddLinksForSource(dbSource,
                new[]
                {
                    new Link
                    {
                        source_id = dbSource.id,
                        for_ratings = true,
                        for_source = false,
                        for_reviews = true,
                        upstream_source_id = src.upstream_source_id,
                        url = PhishNetUrlForSource(dbSource),
                        label = "View on phish.net"
                    }
                })).Count();


            return stats;
        }

        private async Task<IEnumerable<SourceReview>> ReplaceSourceReviews(ImportStats stats, Source source,
            IEnumerable<SourceReview> reviews)
        {
            foreach (var review in reviews)
            {
                review.source_id = source.id;
            }

            var res = await _sourceReviewService.UpdateAll(source, reviews);

            stats.Created += res.Count();

            return res;
        }

        private class PhishNetScrapeResults
        {
            public double RatingAverage { get; set; }
            public int RatingVotesCast { get; set; }
            public int NumberOfReviewsWritten { get; set; }
        }

        public class PhishNetApiReview
        {
            public string commentid { get; set; }
            public string showdate { get; set; }
            public string showyear { get; set; }
            public string review { get; set; }
            public string venue { get; set; }
            public string city { get; set; }
            public string state { get; set; }
            public string country { get; set; }
            public int tstamp { get; set; }
            public string author { get; set; }
        }

        public class PhishNetApiSetlist
        {
            public string artist { get; set; }
            public string showid { get; set; }
            public string showdate { get; set; }
            public string showyear { get; set; }
            public string meta { get; set; }
            public string city { get; set; }
            public string state { get; set; }
            public string country { get; set; }
            public string venue { get; set; }
            public string setlistnotes { get; set; }
            public string venuenotes { get; set; }
            public string venueid { get; set; }
            public string url { get; set; }

            [JsonProperty("artist-name")] public string artist_name { get; set; }

            public string mmddyy { get; set; }
            public string nicedate { get; set; }
            public string relativetime { get; set; }
            public string setlistdata { get; set; }
        }
    }
}
