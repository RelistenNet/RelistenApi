using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Relisten.Import.PhishNet;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.PhishNet;

namespace Relisten.Import;

// Fetch HTTP before taking the artist performance lock. The lock keeps reviewed
// assignments stable while an item replaces its recording metadata and reviews.
public partial class PhishNetImporter
{
    public override void UseIngestionHttpClient(HttpClient client)
    {
        base.UseIngestionHttpClient(client);
        phishNetApiClient = new PhishNetApiClient(client, normalizationInputs);
    }

    /// <summary>Selects current recordings and their matching Phish.net show metadata.</summary>
    public async Task<DiscoveredItem<CatalogSourceId, PhishNetSourceInput>[]> SelectSourcesAsync(
        ProviderContext context,
        int? year
    )
    {
        var shows = await phishNetApiClient.Shows(null, context.CancellationToken);
        var sources = (await _sourceService.AllForArtistFromPrimary(context.Artist)).ToArray();
        var showDates = shows.ToLookup(show => show.showdate);
        var catalogDates = sources.ToLookup(source => source.display_date);

        return sources
            .Where(source =>
                !year.HasValue
                || source.display_date.StartsWith($"{year.Value:D4}-", StringComparison.Ordinal)
            )
            .OrderBy(source => source.display_date)
            .Select(source =>
            {
                var candidates = showDates[source.display_date].ToArray();
                var ambiguous =
                    candidates.Length != 1
                    || catalogDates[source.display_date]
                        .Select(recording => recording.show_id)
                        .Distinct()
                        .Skip(1)
                        .Any();
                var show = candidates.Length == 1 ? candidates[0] : null;
                return new DiscoveredItem<CatalogSourceId, PhishNetSourceInput>(
                    new(source.id),
                    new(
                        show == null
                            ? null
                            : new PhishNetShowInput(
                                show.showdate,
                                show.setlist_notes,
                                show.showid,
                                show.permalink
                            ),
                        candidates.Length == 0
                                ? "Phish.net has no matching performance for this date."
                            : ambiguous
                                ? "Several performances share this date; Phish.net requires an explicit match."
                            : null,
                        source.display_date
                    )
                );
            })
            .ToArray();
    }

    /// <summary>Enriches current source data; refetches outside the transaction if review requirements changed.</summary>
    public async Task<PhishNetItemResult> EnrichSourceAsync(
        ProviderContext context,
        DiscoveredItem<CatalogSourceId, PhishNetSourceInput> item
    ) => (await EnrichRecordingAsync(context, item)).Result;

    private async Task<(PhishNetItemResult Result, ImportStats Stats)> EnrichRecordingAsync(
        ProviderContext context,
        DiscoveredItem<CatalogSourceId, PhishNetSourceInput> item
    )
    {
        if (item.Input.SkipReason != null)
        {
            return (new(item.Id, ItemDisposition.Skipped, item.Input.SkipReason), new());
        }

        var before = await _sourceService.ForArtistFromPrimary(context.Artist, item.Id.Value);
        var selectedDate = item.Input.SourceDate ?? item.Input.Show?.Date;
        if (selectedDate != null && selectedDate != before.display_date)
        {
            return (
                new(
                    item.Id,
                    ItemDisposition.Skipped,
                    "The recording date changed after selection; select fresh Phish.net metadata."
                ),
                new()
            );
        }

        if (!await performances.CanEnrichSourceAsync(context.Artist, before.id))
        {
            return (
                new(
                    item.Id,
                    ItemDisposition.Skipped,
                    "The recording has no unambiguous performance assignment for Phish.net enrichment."
                ),
                new()
            );
        }

        var ratings = await new PhishNetRatingsScraper(
            http,
            before.display_date,
            normalizationInputs
        ).ScrapeRatings(context.CancellationToken);
        PhishNetApiReview[]? reviews = null;
        if (ratings.RatingMatched && before.num_reviews != ratings.NumberOfReviewsWritten)
        {
            reviews = (
                await phishNetApiClient.Reviews(
                    before.display_date,
                    null,
                    context.CancellationToken
                )
            ).ToArray();
        }

        return await context.CommitAsync(async () =>
        {
            await performances.LockArtistAsync(context.Artist);
            // ProcessSource replaces reviews before saving the source. Lock this
            // recording so overlapping attempts cannot mix those compound writes.
            var current = await _sourceService.ForArtistAndIdForUpdate(
                context.Artist,
                item.Id.Value
            );

            // HTTP inputs were fetched before this transaction. If they no longer describe
            // the current recording, retry the whole activity to fetch fresh data.
            if (
                current.display_date != before.display_date
                || current.show_id != before.show_id
                || (
                    ratings.RatingMatched
                    && current.num_reviews != ratings.NumberOfReviewsWritten
                    && reviews == null
                )
            )
            {
                throw new EnrichmentInputsChangedException();
            }

            if (!await performances.CanEnrichSourceAsync(context.Artist, current.id))
            {
                return (
                    new PhishNetItemResult(
                        item.Id,
                        ItemDisposition.Skipped,
                        "The performance assignment changed while fetching Phish.net inputs."
                    ),
                    new ImportStats()
                );
            }

            var stats = await ProcessSource(
                context.Source,
                current,
                SelectedShow(
                    item.Input.Show?.Date == current.display_date ? item.Input.Show : null
                ),
                null,
                ratings,
                reviews ?? Array.Empty<PhishNetApiReview>()
            );

            if (item.Input.Show is { ShowId: > 0 } show && show.Date == current.display_date)
            {
                await performances.RecordSourceEvidenceAsync(
                    context.Artist,
                    current.id,
                    "phish.net",
                    System.Text.Json.JsonSerializer.Serialize(
                        new { show_id = show.ShowId, permalink = show.Permalink }
                    )
                );
            }

            return (new PhishNetItemResult(item.Id, ItemDisposition.Applied), stats);
        });
    }

    private static PhishNetApiShow? SelectedShow(PhishNetShowInput? show) =>
        show == null
            ? null
            : new PhishNetApiShow
            {
                showdate = show.Date,
                setlist_notes = show.SetlistNotes!,
                showid = show.ShowId ?? 0,
                permalink = show.Permalink!,
            };
}

/// <summary>A concurrent write changed enrichment prerequisites; retry must fetch fresh inputs before applying them.</summary>
public sealed class EnrichmentInputsChangedException()
    : Exception("The recording changed while fetching enrichment inputs.");
