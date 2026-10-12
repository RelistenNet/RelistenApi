using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.Jerry;
using Relisten.Vendor;

namespace Relisten.Import;

// The ordinary refresh and venue repair share page parsing, but select opposite
// catalog membership sets and have different cleanup policies.
public partial class JerryGarciaComImporter
{
    /// <summary>Freezes the mirror index for either missing-page import or existing-page repair.</summary>
    public async Task<JerrySelection> SelectPagesAsync(ProviderContext context, bool existing)
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
        return new JerrySelection(
            BuildFileMetaObjects(files)
                .Where(f => existingSetlistShows.ContainsKey(f.Identifier) == existing)
                .Select(f => new DiscoveredItem<JerryShowId, FileMetaObject>(new(f.Identifier), f))
                .ToArray()
        );
    }

    /// <summary>Imports one missing page and preserves tour bounds from earlier committed pages.</summary>
    public Task<JerryItemResult> ImportPageAsync(
        ProviderContext context,
        DiscoveredItem<JerryShowId, FileMetaObject> item
    ) => ApplySelectedPageAsync(context, item, repairVenue: false);

    /// <summary>Repairs one existing setlist's venue without changing its songs or identity.</summary>
    public Task<JerryItemResult> RepairVenueAsync(
        ProviderContext context,
        DiscoveredItem<JerryShowId, FileMetaObject> item
    ) => ApplySelectedPageAsync(context, item, repairVenue: true);

    private async Task<JerryItemResult> ApplySelectedPageAsync(
        ProviderContext context,
        DiscoveredItem<JerryShowId, FileMetaObject> item,
        bool repairVenue
    )
    {
        using var response = await http.GetAsync(
            ShowPageUrl(context.Source, item.Input.FilePath),
            context.CancellationToken
        );
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(context.CancellationToken);
        var modifiedAt = response.Content.Headers.LastModified?.UtcDateTime ?? DateTime.UtcNow;
        // The legacy parser deliberately ignores pages without show details. Make
        // that outcome visible instead of reporting an import with no identity.
        if (ParseShowPage(html) == null)
            return new(item.Id, null, ItemDisposition.Skipped, JerrySkipReason.MissingShowDetails);
        return await context.CommitAsync(async () =>
        {
            await PreloadData(context.Artist);
            var previous = existingSetlistShows.GetValue(item.Id.Value);
            if (repairVenue)
            {
                if (previous == null)
                    return new JerryItemResult(
                        item.Id,
                        null,
                        ItemDisposition.Skipped,
                        JerrySkipReason.MissingCatalogSetlist
                    );
                var stats = new ImportStats();
                await ApplyVenueBackfill(stats, context.Artist, item.Input, html, modifiedAt);
                return new JerryItemResult(
                    item.Id,
                    new(previous.id),
                    stats.Created + stats.Updated == 0
                        ? ItemDisposition.Unchanged
                        : ItemDisposition.Applied
                );
            }
            if (previous != null)
                return new JerryItemResult(item.Id, new(previous.id), ItemDisposition.Unchanged);
            foreach (var tour in existingTours.Values.Where(t => t != null))
            {
                if (tour!.start_date.HasValue)
                    tourToStartDate[tour.upstream_identifier] = tour.start_date.Value;
                if (tour.end_date.HasValue)
                    tourToEndDate[tour.upstream_identifier] = tour.end_date.Value;
            }
            await ProcessPage(
                new ImportStats(),
                context.Artist,
                item.Input,
                html,
                modifiedAt,
                null
            );
            if (context.Artist.features.tours && tourToStartDate.Count > 0)
                await UpdateTourStartEndDates(context.Artist);
            return new JerryItemResult(
                item.Id,
                new(existingSetlistShows[item.Id.Value]!.id),
                ItemDisposition.Applied
            );
        });
    }

    /// <summary>Refreshes tour bounds after repairs; orphan removal requires every selected repair to succeed.</summary>
    public Task<int> FinishVenueRepairAsync(ProviderContext context, bool removeOrphans) =>
        context.CommitAsync(async () =>
        {
            await _tourService.RefreshDatesFromSetlists(context.Artist);
            return removeOrphans ? await _venueService.RemoveOrphansForArtist(context.Artist) : 0;
        });
}
