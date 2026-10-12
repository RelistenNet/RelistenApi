using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.SetlistFm;
using Relisten.Vendor;
using Relisten.Vendor.SetlistFm;

namespace Relisten.Import;

public partial class SetlistFmImporter
{
    /// <summary>Reads one upstream page while preserving the tolerant setlist parser.</summary>
    public async Task<
        Page<DiscoveredItem<SetlistFmId, Setlist>, SetlistFmPage>
    > ReadSetlistPageAsync(ProviderContext context, SetlistFmPage? cursor)
    {
        var page = cursor?.Number ?? 1;
        using var response = await http.GetAsync(
            SetlistUrlForArtist(context.Artist, page),
            context.CancellationToken
        );
        response.EnsureSuccessStatusCode();
        var root =
            JsonConvert.DeserializeObject<SetlistsRootObject>(
                await response.Content.ReadAsStringAsync(context.CancellationToken),
                new TolerantListConverter<Song>(),
                new TolerantListConverter<Set>(),
                new TolerantListConverter<Setlist>(),
                new TolerantSetsConverter()
            ) ?? throw new System.IO.InvalidDataException("Setlist.fm returned an empty page.");
        if (root.setlist == null || root.itemsPerPage <= 0 || root.page != page || root.total < 0)
            throw new System.IO.InvalidDataException(
                "Setlist.fm returned invalid pagination metadata."
            );
        var next =
            root.page < Math.Ceiling((double)root.total / root.itemsPerPage)
                ? new SetlistFmPage(page + 1)
                : null;
        return new Page<DiscoveredItem<SetlistFmId, Setlist>, SetlistFmPage>(
            root.setlist.Where(s => s.sets.set.Count > 0)
                .Select(s => new DiscoveredItem<SetlistFmId, Setlist>(new(s.id), s))
                .ToArray(),
            next
        );
    }

    /// <summary>Applies a setlist under the artist lock and returns its current catalog identity.</summary>
    public async Task<SetlistFmItemResult> ImportSetlistAsync(
        ProviderContext context,
        DiscoveredItem<SetlistFmId, Setlist> item
    )
    {
        return await context.CommitAsync(async () =>
        {
            await PreloadData(context.Artist);
            // Item activities no longer share dictionaries. Carry forward committed tour
            // bounds so applying the next item cannot shrink a previously imported tour.
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
            var unchanged =
                existingSetlistShows.TryGetValue(item.Id.Value, out var previous)
                && previous != null
                && previous.updated_at >= item.Input.lastUpdated;
            await ProcessSetlist(context.Artist, item.Input);
            if (context.Artist.features.tours && tourToStartDate.Count > 0)
            {
                await UpdateTourStartEndDates(context.Artist);
            }

            return new SetlistFmItemResult(
                item.Id,
                new(existingSetlistShows[item.Id.Value]!.id),
                unchanged ? ItemDisposition.Unchanged : ItemDisposition.Applied
            );
        });
    }
}
