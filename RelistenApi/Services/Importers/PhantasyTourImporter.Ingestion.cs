using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.PhantasyTour;
using Relisten.Vendor;
using Relisten.Vendor.PhantasyTour;

namespace Relisten.Import;

// Known shows need no detail fetch. Their dates are reconciled from the complete
// listing at provider finalization, preserving the upstream's local calendar date.
public partial class PhantasyTourImporter
{
    /// <summary>Selects missing shows and retains dates for every listed past show.</summary>
    public async Task<PhantasyTourDiscovery> ReadShowPageAsync(
        ProviderContext context,
        PhantasyTourPage? cursor
    )
    {
        var page = cursor?.Number ?? 1u;
        var shows = await FetchShowPageAsync(context.Source, page, context.CancellationToken);

        var dates = new Dictionary<PhantasyTourShowId, DateTime>();
        var selected = new List<DiscoveredItem<PhantasyTourShowId, PhantasyTourShowListing>>();
        await PreloadData(context.Artist);

        foreach (var show in shows)
        {
            if (show.dateTime > DateTimeOffset.UtcNow)
            {
                continue;
            }

            // A repeated show may have changed while the listing was paged.
            dates[new(show.id)] = show.dateTime.Date;

            var identifier = UpstreamIdentifierForPhantasyTourId(show.id);
            if (!existingSetlistShows.ContainsKey(identifier))
            {
                selected.Add(
                    new DiscoveredItem<PhantasyTourShowId, PhantasyTourShowListing>(
                        new(show.id),
                        show
                    )
                );
            }
        }

        var next = shows.Length == ITEMS_PER_PAGE ? new PhantasyTourPage(page + 1) : null;
        return new PhantasyTourDiscovery(
            selected.ToArray(),
            next,
            dates.Select(pair => new PhantasyTourListedDate(pair.Key, pair.Value)).ToArray()
        );
    }

    /// <summary>Fetches details only for a missing setlist; retries recover an existing catalog identity.</summary>
    public async Task<PhantasyTourItemResult> ImportShowAsync(
        ProviderContext context,
        DiscoveredItem<PhantasyTourShowId, PhantasyTourShowListing> item
    )
    {
        var identifier = UpstreamIdentifierForPhantasyTourId(item.Id.Value);
        var existing = await context.CommitAsync(async () =>
        {
            await PreloadData(context.Artist);
            return existingSetlistShows.GetValue(identifier);
        });

        if (existing != null)
        {
            return new(item.Id, new(existing.id), ItemDisposition.Unchanged);
        }

        var show = await FetchShowAsync(item.Id.Value, context.CancellationToken);

        return await context.CommitAsync(async () =>
        {
            // A previous attempt may have committed while this attempt fetched
            // the detail page. Reuse that row instead of inserting another one.
            await PreloadData(context.Artist);
            if (existingSetlistShows.GetValue(identifier) is { } current)
            {
                return new PhantasyTourItemResult(
                    item.Id,
                    new(current.id),
                    ItemDisposition.Unchanged
                );
            }

            await ApplyShow(context.Artist, new ImportStats(), null, show);

            return new PhantasyTourItemResult(
                item.Id,
                new(existingSetlistShows[identifier]!.id),
                ItemDisposition.Applied
            );
        });
    }

    /// <summary>Repairs dates only from a complete, validated listing retained by the workflow.</summary>
    public Task<int> RepairListedDatesAsync(
        ProviderContext context,
        PhantasyTourListedDate[] listedDates
    )
    {
        var dates = new Dictionary<PhantasyTourShowId, DateTime>();
        foreach (var entry in listedDates)
        {
            dates[entry.Id] = entry.Date;
        }

        return context.CommitAsync(async () =>
        {
            var corrected = await _setlistShowService.UpdateDates(
                context.Artist,
                dates.ToDictionary(
                    pair => UpstreamIdentifierForPhantasyTourId(pair.Key.Value),
                    pair => pair.Value
                )
            );
            await _tourService.RefreshDatesFromSetlists(context.Artist);
            return corrected;
        });
    }
}
