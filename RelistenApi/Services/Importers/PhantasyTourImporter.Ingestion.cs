using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Relisten.Ingestion;
using Relisten.Vendor.PhantasyTour;

namespace Relisten.Import;

// Known shows need no detail fetch. Their dates are reconciled from the complete
// listing at provider finalization, preserving the upstream's local calendar date.
public partial class PhantasyTourImporter
{
    /// <inheritdoc />
    public override async Task<PhantasyTourDiscovery> DiscoverAsync(
        ProviderContext context,
        string? cursor
    )
    {
        var page = cursor == null ? 1u : uint.Parse(cursor);
        using var response = await http.GetAsync(
            UrlForArtist(context.Source, page),
            context.CancellationToken
        );
        response.EnsureSuccessStatusCode();
        var shows =
            JsonConvert.DeserializeObject<PhantasyTourShowListing[]>(
                await response.Content.ReadAsStringAsync(context.CancellationToken)
            ) ?? throw new InvalidDataException("Phantasy Tour returned an invalid listing.");
        var dates = new Dictionary<string, DateTime>();
        var selected = new List<DiscoveredItem<PhantasyTourShowListing>>();
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
            AddListedDate(dates, identifier, show.dateTime.Date);
            if (!existingSetlistShows.ContainsKey(identifier))
            {
                selected.Add(new DiscoveredItem<PhantasyTourShowListing>(show.id.ToString(), show));
            }
        }

        var next = shows.Length == ITEMS_PER_PAGE ? (page + 1).ToString() : null;
        return new PhantasyTourDiscovery(selected.ToArray(), next, SetlistDates: dates);
    }

    /// <inheritdoc />
    public override async Task<ItemImportResult> ImportItemAsync(
        ProviderContext context,
        DiscoveredItem<PhantasyTourShowListing> item
    )
    {
        var showId = item.Metadata.id;
        using var response = await http.GetAsync(UrlForShow(showId), context.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(context.CancellationToken);

        return await context.CommitAsync(async () =>
        {
            await PreloadData(context.Artist);
            var stats = new ImportStats();
            await ApplyShowBody(context.Artist, stats, null, showId, body);
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
        var dates = new Dictionary<string, DateTime>();
        string? cursor = null;
        do
        {
            var snapshot =
                await context.Store.ReadSnapshotAsync<PhantasyTourDiscovery>(
                    context.Run,
                    DiscoveryName(cursor)
                )
                ?? throw new InvalidOperationException(
                    "Phantasy Tour discovery has not completed."
                );
            foreach (var (identifier, date) in snapshot.SetlistDates)
            {
                AddListedDate(dates, identifier, date);
            }

            cursor = snapshot.NextCursor;
        } while (cursor != null);

        return await context.CommitAsync(async () =>
        {
            var corrected = await _setlistShowService.UpdateDates(context.Artist, dates);
            await _tourService.RefreshDatesFromSetlists(context.Artist);
            return new ProviderResult(new ImportStats { Updated = corrected });
        });
    }

    private static void AddListedDate(
        Dictionary<string, DateTime> dates,
        string identifier,
        DateTime date
    )
    {
        if (dates.TryGetValue(identifier, out var previous) && previous != date)
        {
            throw new InvalidDataException(
                $"Phantasy Tour returned conflicting dates for {identifier}."
            );
        }

        dates[identifier] = date;
    }
}

/// <summary>Local calendar dates from a complete Phantasy Tour listing page.</summary>
public record PhantasyTourDiscovery(
    DiscoveredItem<PhantasyTourShowListing>[] Items,
    string? NextCursor,
    IReadOnlyDictionary<string, DateTime> SetlistDates
) : DiscoverySnapshot<PhantasyTourShowListing>(Items, NextCursor);
