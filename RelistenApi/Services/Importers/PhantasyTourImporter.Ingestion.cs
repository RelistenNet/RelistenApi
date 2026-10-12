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
public partial class PhantasyTourImporter : IProviderIngestion
{
    /// <inheritdoc />
    public async Task<DiscoverySnapshot> DiscoverAsync(ProviderContext context, string? cursor)
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
        var selected = new List<DiscoveredItem>();
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
                selected.Add(new DiscoveredItem(show.id.ToString(), ""));
            }
        }

        var next = shows.Length == ITEMS_PER_PAGE ? (page + 1).ToString() : null;
        return new DiscoverySnapshot(selected.ToArray(), next, SetlistDates: dates);
    }

    /// <inheritdoc />
    public async Task<ItemImportResult> ImportItemAsync(
        ProviderContext context,
        DiscoveredItem item
    )
    {
        if (context.Request.Item != null)
        {
            return new ItemImportResult(new ImportStats());
        }

        var showId = int.Parse(item.Identifier);
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
    public async Task<ProviderResult> ReconcileAsync(
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
                await context.Store.ReadSnapshotAsync<DiscoverySnapshot>(
                    context.Run,
                    IngestionActivities.DiscoveryName(Provider, cursor)
                )
                ?? throw new InvalidOperationException(
                    "Phantasy Tour discovery has not completed."
                );
            foreach (var (identifier, date) in snapshot.SetlistDates!)
            {
                AddListedDate(dates, identifier, date);
            }

            cursor = snapshot.NextCursor;
        } while (cursor != null);

        return await context.CommitAsync(async () =>
        {
            var corrected = await _setlistShowService.UpdateDates(context.Artist, dates);
            await UpdateTourStartEndDates(context.Artist);
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
