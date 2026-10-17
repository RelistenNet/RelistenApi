using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Import.PhishNet;
using Relisten.Ingestion;

namespace Relisten.Import
{
    public partial class PhishNetImporter : ImporterBase
    {
        readonly LinkService linkService;
        readonly ShowPerformanceService performances;
        readonly CatalogImportTransaction transactions;
        PhishNetApiClient phishNetApiClient;

        public PhishNetImporter(
            CatalogRebuildService catalog,
            SourceService sourceService,
            SourceReviewService sourceReviewService,
            LinkService linkService,
            ILogger<PhishNetImporter> log,
            ShowPerformanceService performances,
            CatalogImportTransaction transactions
        )
            : base(catalog, log)
        {
            this.linkService = linkService;
            this.performances = performances;
            this.transactions = transactions;
            _sourceService = sourceService;
            _log = log;
            _sourceReviewService = sourceReviewService;
            http = new PhishNetHttpClientFactory().HttpClient;
            phishNetApiClient = new PhishNetApiClient(http);
        }

        protected SourceService _sourceService { get; set; }
        protected SourceReviewService _sourceReviewService { get; set; }
        protected ILogger<PhishNetImporter> _log { get; set; }

        public override UpstreamProvider Provider => UpstreamProvider.PhishNet;

        public override ImportableData ImportableDataForArtist(Artist artist)
        {
            return ImportableData.SourceRatings
                | ImportableData.SourceReviews
                | ImportableData.Sources;
        }

        public override async Task<ImportStats> ImportDataForArtist(
            Artist artist,
            ArtistUpstreamSource src,
            PerformContext? ctx
        )
        {
            var context = new ProviderContext(
                artist,
                src,
                new IngestionExecution(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddDays(7)),
                transactions,
                CancellationToken.None
            );
            var items = await SelectSourcesAsync(
                context,
                CurrentImportOptions.IsThinScrape ? CurrentImportOptions.OnlyYear : null
            );
            var stats = new ImportStats();
            foreach (var item in items)
            {
                var outcome = await EnrichRecordingAsync(context, item);
                stats += outcome.Stats;
                if (outcome.Result.SkipReason != null)
                {
                    ImportProgress.WriteLine(
                        log,
                        ctx,
                        $"Source {item.Id.Value}: {outcome.Result.SkipReason}"
                    );
                }
            }

            await RebuildShows(artist);
            await RebuildYears(artist);
            return stats;
        }

        public override Task<ImportStats> ImportSpecificShowDataForArtist(
            Artist artist,
            ArtistUpstreamSource src,
            string? showIdentifier,
            PerformContext? ctx
        )
        {
            return Task.FromResult(new ImportStats());
        }

        private async Task<ImportStats> ProcessSource(
            ArtistUpstreamSource src,
            Source dbSource,
            PhishNetApiShow? phishNetApiShow,
            PerformContext? ctx,
            PhishNetScrapeResults ratings,
            IEnumerable<PhishNetApiReview> reviews
        )
        {
            var stats = new ImportStats();
            var dirty = false;

            if (!ratings.RatingMatched)
            {
                ImportProgress.WriteLine(
                    log,
                    ctx,
                    $"{dbSource.display_date} skipping — failed to scrape ratings from phish.net"
                );
            }
            else
            {
                var newAvgRating = decimal.ToDouble(ratings.RatingAverage * 2.0m);

                if (
                    dbSource.num_ratings != ratings.RatingVotesCast
                    || Math.Abs(dbSource.avg_rating - newAvgRating) > 0.001
                )
                {
                    ImportProgress.WriteLine(
                        log,
                        ctx,
                        $"{dbSource.display_date} ratings changed: num_ratings {dbSource.num_ratings} → {ratings.RatingVotesCast}, avg_rating {dbSource.avg_rating} → {newAvgRating}"
                    );
                    dbSource.num_ratings = ratings.RatingVotesCast;
                    dbSource.avg_rating = newAvgRating;

                    dirty = true;
                }

                if (dbSource.num_reviews != ratings.NumberOfReviewsWritten)
                {
                    ImportProgress.WriteLine(
                        log,
                        ctx,
                        $"{dbSource.display_date} reviews changed: {dbSource.num_reviews} → {ratings.NumberOfReviewsWritten}"
                    );
                    var dbReviews = reviews
                        .Where(review =>
                            phishNetApiShow?.showid is not > 0
                            || review.showid == phishNetApiShow.showid
                        )
                        .Select(rev => new SourceReview
                        {
                            rating = null,
                            title = null,
                            review = rev.review_text,
                            author = rev.username,
                            updated_at = rev.posted_at,
                        })
                        .ToList();

                    dbSource.num_reviews = dbReviews.Count;

                    dirty = true;

                    await ReplaceSourceReviews(stats, dbSource, dbReviews);
                }
            }

            if (
                phishNetApiShow?.setlist_notes is { } notes
                && !dbSource.description.Contains(notes)
            )
            {
                ImportProgress.WriteLine(
                    log,
                    ctx,
                    $"{dbSource.display_date} setlist_notes changed: [{dbSource.description.Length} chars] → [{phishNetApiShow.setlist_notes.Length} chars]"
                );
                dbSource.description = phishNetApiShow.setlist_notes;

                dirty = true;
            }

            if (dirty)
            {
                ImportProgress.WriteLine(log, ctx, $"{dbSource.display_date} changed!");
                await _sourceService.Save(dbSource);
                stats.Updated++;
            }

            stats.Created += (
                await linkService.AddLinksForSource(
                    dbSource,
                    [
                        new Link
                        {
                            source_id = dbSource.id,
                            for_ratings = true,
                            for_source = false,
                            for_reviews = true,
                            upstream_source_id = src.upstream_source_id,
                            url = OfficialShowUrl(phishNetApiShow, dbSource.display_date),
                            label = "View on phish.net",
                        },
                    ]
                )
            ).Count();

            return stats;
        }

        // Retain the provider's individual-show permalink when supplied. Date URLs
        // remain compatible with old workflow payloads and older provider fixtures.
        private static string OfficialShowUrl(PhishNetApiShow? show, string date)
        {
            if (
                Uri.TryCreate(show?.permalink, UriKind.Absolute, out var url)
                && url.Scheme == Uri.UriSchemeHttps
                && (url.Host == "phish.net" || url.Host == "www.phish.net")
            )
            {
                return url.AbsoluteUri;
            }

            return PhishNetRatingsScraper.PhishNetUrlForSource(date);
        }

        private async Task<IEnumerable<SourceReview>> ReplaceSourceReviews(
            ImportStats stats,
            Source source,
            IList<SourceReview> reviews
        )
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
