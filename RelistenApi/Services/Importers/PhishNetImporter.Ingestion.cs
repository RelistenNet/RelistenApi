using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Import.PhishNet;
using Relisten.Ingestion;

namespace Relisten.Import;

// Ratings, redirects, and reviews form one retryable item operation. Their bodies
// remain in memory; only the discovery selection and committed result are retained.
public partial class PhishNetImporter
{
    /// <summary>A recording selected for ratings/review enrichment and its matching API show.</summary>
    public record SourceInput(long SourceId, PhishNetApiShow? Show);

    public override void UseIngestionHttpClient(HttpClient client)
    {
        base.UseIngestionHttpClient(client);
        phishNetApiClient = new PhishNetApiClient(client);
    }

    /// <inheritdoc />
    public override async Task<DiscoverySnapshot<SourceInput>> DiscoverAsync(
        ProviderContext context,
        string? cursor
    )
    {
        var shows = await phishNetApiClient.Shows(null);
        var sources = await _sourceService.AllForArtistFromPrimary(context.Artist);
        return new DiscoverySnapshot<SourceInput>(
            sources
                .Where(source =>
                    context.Request.Year == null
                    || source.display_date.StartsWith(context.Request.Year.ToString()!)
                )
                .OrderBy(source => source.display_date)
                .Select(source => new DiscoveredItem<SourceInput>(
                    source.id.ToString(),
                    new SourceInput(
                        source.id,
                        shows.FirstOrDefault(show => show.showdate == source.display_date)
                    )
                ))
                .ToArray()
        );
    }

    /// <inheritdoc />
    public override async Task<ItemImportResult> ImportItemAsync(
        ProviderContext context,
        DiscoveredItem<SourceInput> item
    )
    {
        var input = item.Metadata;
        var source = await _sourceService.ForArtistFromPrimary(context.Artist, input.SourceId);
        var ratings = await new PhishNetRatingsScraper(http, source.display_date).ScrapeRatings();
        var reviews = Array.Empty<PhishNetApiReview>();

        if (ratings.RatingMatched && source.num_reviews != ratings.NumberOfReviewsWritten)
        {
            reviews = (await phishNetApiClient.Reviews(source.display_date, null)).ToArray();
        }

        var shows = input.Show == null ? Array.Empty<PhishNetApiShow>() : new[] { input.Show };
        return await context.CommitAsync(async () =>
            new ItemImportResult(
                await ProcessSource(
                    context.Artist,
                    context.Source,
                    source,
                    shows,
                    null,
                    ratings,
                    reviews
                )
            )
        );
    }
}
