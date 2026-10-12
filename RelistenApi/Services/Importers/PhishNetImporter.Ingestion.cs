using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Relisten.Import.PhishNet;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.PhishNet;

namespace Relisten.Import;

// Network reads happen before the lock. The transaction reloads its source so an
// overlapping attempt cannot overwrite recording fields from an earlier snapshot.
public partial class PhishNetImporter
{
    public override void UseIngestionHttpClient(HttpClient client)
    {
        base.UseIngestionHttpClient(client);
        phishNetApiClient = new PhishNetApiClient(client);
    }

    /// <summary>Selects current recordings and their matching Phish.net show metadata.</summary>
    public async Task<DiscoveredItem<CatalogSourceId, PhishNetSourceInput>[]> SelectSourcesAsync(
        ProviderContext context,
        int? year
    )
    {
        var shows = await phishNetApiClient.Shows(null);
        var sources = await _sourceService.AllForArtistFromPrimary(context.Artist);

        return sources
            .Where(source =>
                !year.HasValue
                || source.display_date.StartsWith($"{year.Value:D4}-", StringComparison.Ordinal)
            )
            .OrderBy(source => source.display_date)
            .Select(source =>
            {
                var show = shows.FirstOrDefault(show => show.showdate == source.display_date);
                return new DiscoveredItem<CatalogSourceId, PhishNetSourceInput>(
                    new(source.id),
                    new(
                        show == null
                            ? null
                            : new PhishNetShowInput(show.showdate, show.setlist_notes)
                    )
                );
            })
            .ToArray();
    }

    /// <summary>Enriches current source data; refetches outside the transaction if review requirements changed.</summary>
    public async Task<PhishNetItemResult> EnrichSourceAsync(
        ProviderContext context,
        DiscoveredItem<CatalogSourceId, PhishNetSourceInput> item
    )
    {
        var before = await _sourceService.ForArtistFromPrimary(context.Artist, item.Id.Value);
        var ratings = await new PhishNetRatingsScraper(http, before.display_date).ScrapeRatings();
        PhishNetApiReview[]? reviews = null;
        if (ratings.RatingMatched && before.num_reviews != ratings.NumberOfReviewsWritten)
        {
            reviews = (await phishNetApiClient.Reviews(before.display_date, null)).ToArray();
        }

        return await context.CommitAsync(async () =>
        {
            var current = await _sourceService.ForArtistFromPrimary(context.Artist, item.Id.Value);

            // HTTP inputs were fetched before locking. If they no longer describe
            // the current recording, retry the whole activity to fetch fresh data.
            if (
                current.display_date != before.display_date
                || (
                    ratings.RatingMatched
                    && current.num_reviews != ratings.NumberOfReviewsWritten
                    && reviews == null
                )
            )
            {
                throw new EnrichmentInputsChangedException();
            }

            await ProcessSource(
                context.Artist,
                context.Source,
                current,
                SelectedShows(item.Input.Show),
                null,
                ratings,
                reviews ?? Array.Empty<PhishNetApiReview>()
            );

            return new PhishNetItemResult(item.Id, ItemDisposition.Applied);
        });
    }

    private static PhishNetApiShow[] SelectedShows(PhishNetShowInput? show)
    {
        if (show == null)
        {
            return [];
        }

        return [new PhishNetApiShow { showdate = show.Date, setlist_notes = show.SetlistNotes! }];
    }
}

/// <summary>A concurrent write changed enrichment prerequisites; retry must fetch fresh inputs before locking.</summary>
public sealed class EnrichmentInputsChangedException()
    : Exception("The recording changed while fetching enrichment inputs.");
