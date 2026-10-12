using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Relisten.Ingestion;
using Relisten.Vendor;

namespace Relisten.Import;

public partial class JerryGarciaComImporter
{
    /// <inheritdoc />
    public override async Task<DiscoverySnapshot<FileMetaObject>> DiscoverAsync(
        ProviderContext context,
        string? cursor
    )
    {
        using var response = await http.GetAsync(
            ShowPagesListingUrl(context.Source),
            context.CancellationToken
        );
        response.EnsureSuccessStatusCode();
        var files =
            JsonConvert.DeserializeObject<string[]>(
                await response.Content.ReadAsStringAsync(context.CancellationToken)
            )
            ?? throw new System.IO.InvalidDataException(
                "Jerry mirror returned an invalid page index."
            );
        await PreloadData(context.Artist);
        return new DiscoverySnapshot<FileMetaObject>(
            BuildFileMetaObjects(files)
                .Where(f =>
                    context.Request.BackfillJerryVenues
                        ? existingSetlistShows.ContainsKey(f.Identifier)
                        : !existingSetlistShows.ContainsKey(f.Identifier)
                )
                .Select(f => new DiscoveredItem<FileMetaObject>(f.Identifier, f))
                .ToArray()
        );
    }

    /// <inheritdoc />
    public override async Task<ItemImportResult> ImportItemAsync(
        ProviderContext context,
        DiscoveredItem<FileMetaObject> item
    )
    {
        var file = item.Metadata;
        using var response = await http.GetAsync(
            ShowPageUrl(context.Source, file.FilePath),
            context.CancellationToken
        );
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(context.CancellationToken);
        var modifiedAt = response.Content.Headers.LastModified?.UtcDateTime ?? DateTime.UtcNow;

        return await context.CommitAsync(async () =>
        {
            await PreloadData(context.Artist);
            foreach (var tour in existingTours.Values.Where(t => t != null))
            {
                if (tour!.start_date.HasValue)
                {
                    tourToStartDate[tour.upstream_identifier] = tour.start_date.Value;
                }

                if (tour.end_date.HasValue)
                {
                    tourToEndDate[tour.upstream_identifier] = tour.end_date.Value;
                }
            }
            var stats = new ImportStats();
            if (context.Request.BackfillJerryVenues)
            {
                await ApplyVenueBackfill(stats, context.Artist, item.Metadata, html, modifiedAt);
                return new ItemImportResult(stats);
            }
            await ProcessPage(stats, context.Artist, item.Metadata, html, modifiedAt, null);
            if (context.Artist.features.tours && tourToStartDate.Count > 0)
            {
                await UpdateTourStartEndDates(context.Artist);
            }

            return new ItemImportResult(stats);
        });
    }

    /// <inheritdoc />
    public override async Task<ProviderResult> ReconcileAsync(
        ProviderContext context,
        string[] ineligibleItems,
        bool hasFailures
    )
    {
        if (!context.Request.BackfillJerryVenues || hasFailures)
        {
            return new ProviderResult(new ImportStats());
        }

        return await context.CommitAsync(async () =>
            new ProviderResult(
                new ImportStats
                {
                    Removed = await _venueService.RemoveOrphansForArtist(context.Artist),
                }
            )
        );
    }
}
