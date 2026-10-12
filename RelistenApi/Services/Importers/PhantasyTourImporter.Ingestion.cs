using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
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
        using var response = await http.GetAsync(
            UrlForArtist(context.Source, page),
            context.CancellationToken
        );
        response.EnsureSuccessStatusCode();
        var shows =
            JsonConvert.DeserializeObject<PhantasyTourShowListing[]>(
                await response.Content.ReadAsStringAsync(context.CancellationToken)
            ) ?? throw new InvalidDataException("Phantasy Tour returned an invalid listing.");

        var dates = new Dictionary<PhantasyTourShowId, DateTime>();
        var selected = new List<DiscoveredItem<PhantasyTourShowId, PhantasyTourShowListing>>();
        await PreloadData(context.Artist);

        foreach (var show in shows)
        {
            if (show.id <= 0 || show.dateTime == default)
            {
                throw new InvalidDataException(
                    "Phantasy Tour returned a show without an ID or local date."
                );
            }

            if (show.dateTime > DateTimeOffset.UtcNow)
            {
                continue;
            }

            var identifier = UpstreamIdentifierForPhantasyTourId(show.id);
            AddListedDate(dates, new(show.id), show.dateTime.Date);
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

        using var response = await http.GetAsync(
            UrlForShow(item.Id.Value),
            context.CancellationToken
        );
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(context.CancellationToken);

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

            await ApplyShowBody(context.Artist, new ImportStats(), null, item.Id.Value, body);

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
            AddListedDate(dates, entry.Id, entry.Date);
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

    private static void AddListedDate(
        Dictionary<PhantasyTourShowId, DateTime> dates,
        PhantasyTourShowId identifier,
        DateTime date
    )
    {
        if (dates.TryGetValue(identifier, out var previous) && previous != date)
        {
            throw new InvalidDataException(
                $"Phantasy Tour returned conflicting dates for {identifier.Value}."
            );
        }

        dates[identifier] = date;
    }
}
