using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Relisten.Ingestion;
using Relisten.Vendor;
using Relisten.Vendor.SetlistFm;

namespace Relisten.Import;

public partial class SetlistFmImporter : IProviderIngestion
{
    /// <inheritdoc />
    public async Task<ProviderPage> DiscoverAsync(ProviderContext context, string? cursor)
    {
        // The legacy targeted endpoint is unsupported for this provider.
        if (context.Request.Item != null)
            return new ProviderPage(Array.Empty<ProviderItem>());
        var page = cursor == null ? 1 : int.Parse(cursor);
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
        var next =
            root.page < Math.Ceiling((double)root.total / root.itemsPerPage)
                ? (page + 1).ToString()
                : null;
        return new ProviderPage(
            root.setlist.Where(s => s.sets.set.Count > 0)
                .Select(s => new ProviderItem(s.id, JsonConvert.SerializeObject(s)))
                .ToArray(),
            next
        );
    }

    /// <inheritdoc />
    public Task<ProviderFetch> FetchAsync(ProviderContext context, ProviderItem item, int part) =>
        Task.FromResult(new ProviderFetch("{}")); // The listing already contains the complete setlist.

    /// <inheritdoc />
    public async Task<ImportStats> ApplyAsync(
        ProviderContext context,
        ProviderItem item,
        ProviderFetch[] fetched
    )
    {
        await PreloadData(context.Artist);
        // Item activities no longer share dictionaries. Carry forward committed tour
        // bounds so applying the next item cannot shrink a previously imported tour.
        foreach (var tour in existingTours.Values.Where(t => t != null))
        {
            if (tour!.start_date.HasValue)
                tourToStartDate[tour.upstream_identifier] = tour.start_date.Value;
            if (tour.end_date.HasValue)
                tourToEndDate[tour.upstream_identifier] = tour.end_date.Value;
        }
        var stats = await ProcessSetlist(
            context.Artist,
            JsonConvert.DeserializeObject<Setlist>(item.Body)!
        );
        if (context.Artist.features.tours && tourToStartDate.Count > 0)
            await UpdateTourStartEndDates(context.Artist);
        return stats;
    }
}
