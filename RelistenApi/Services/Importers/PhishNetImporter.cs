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
using Relisten.Import.PhishNet;

namespace Relisten.Import
{
    public class PhishNetImporter : ImporterBase
    {
        public const string DataSourceName = "phish.net";

        readonly LinkService linkService;
        readonly PhishNetApiClient phishNetApiClient;

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
            http = new PhishNetHttpClientFactory().HttpClient;
            phishNetApiClient = new PhishNetApiClient(http);
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

            var phishNetApiShows = await phishNetApiClient.Shows(ctx);

            await shows.ForEachAsync(async dbSource =>
            {
                stats += await ProcessSource(artist, src, dbSource, phishNetApiShows, ctx);
            }, prog, 10);

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

        private async Task<PhishNetScrapeResults> ScrapePhishNetForSource(Source dbSource, PerformContext ctx)
        {
            var ratingScraper = new PhishNetRatingsScraper(http, dbSource.display_date);
            ctx?.WriteLine($"Requesting {PhishNetRatingsScraper.PhishNetUrlForSource(dbSource.display_date)}");

            return await ratingScraper.ScrapeRatings();
        }

        private async Task<ImportStats> ProcessSource(Artist artist, ArtistUpstreamSource src, Source dbSource,
            IList<PhishNetApiShow> phishNetApiShows,
            PerformContext ctx)
        {
            var stats = new ImportStats();

            var ratings = await ScrapePhishNetForSource(dbSource, ctx);
            var dirty = false;

            if (dbSource.num_ratings != ratings.RatingVotesCast)
            {
                dbSource.num_ratings = ratings.RatingVotesCast;
                dbSource.avg_rating = decimal.ToDouble(ratings.RatingAverage * 2.0m);

                dirty = true;
            }

            var phishNetApiShow = phishNetApiShows.FirstOrDefault(pnetShow => pnetShow.showdate == dbSource.display_date);

            if (!dbSource.description.Contains(phishNetApiShow.setlist_notes))
            {
                dbSource.description = phishNetApiShow.setlist_notes;

                dirty = true;
            }

            if (dbSource.num_reviews != ratings.NumberOfReviewsWritten)
            {
                var reviews = await phishNetApiClient.Reviews(dbSource.display_date, ctx);

                var dbReviews = reviews.Select(rev => new SourceReview
                {
                    rating = null,
                    title = null,
                    review = rev.review_text,
                    author = rev.username,
                    updated_at = rev.posted_at
                }).ToList();

                dbSource.num_reviews = dbReviews.Count;

                dirty = true;

                await ReplaceSourceReviews(stats, dbSource, dbReviews);
            }

            if (dirty)
            {
                ctx?.WriteLine($"{dbSource.display_date} changed!");
                await _sourceService.Save(dbSource);
                stats.Updated++;
            }

            stats.Created += (await linkService.AddLinksForSource(dbSource,
            [
                new Link
                    {
                        source_id = dbSource.id,
                        for_ratings = true,
                        for_source = false,
                        for_reviews = true,
                        upstream_source_id = src.upstream_source_id,
                        url = PhishNetRatingsScraper.PhishNetUrlForSource(dbSource.display_date),
                        label = "View on phish.net"
                    }
            ])).Count();


            return stats;
        }

        private async Task<IEnumerable<SourceReview>> ReplaceSourceReviews(ImportStats stats, Source source,
            IList<SourceReview> reviews)
        {
            foreach (var review in reviews)
            {
                review.source_id = source.id;
            }

            var res = await _sourceReviewService.UpdateAll(source, reviews);

            stats.Created += res.Count();

            return res;
        }
    }
}
