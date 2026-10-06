using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Relisten.Ingestion;
using Relisten.Vendor;
using Relisten.Vendor.PhantasyTour;

namespace Relisten.Import;

public partial class PhantasyTourImporter : IProviderIngestion
{
    /// <inheritdoc />
    public async Task<ProviderPage> DiscoverAsync(ProviderContext context, string? cursor)
    {
        if (context.Request.Item != null)
            return new ProviderPage(Array.Empty<ProviderItem>());
        var page = cursor == null ? 1u : uint.Parse(cursor);
        using var response = await http.GetAsync(
            UrlForArtist(context.Source, page),
            context.CancellationToken
        );
        response.EnsureSuccessStatusCode();
        var shows =
            JsonConvert.DeserializeObject<PhantasyTourShowListing[]>(
                await response.Content.ReadAsStringAsync(context.CancellationToken)
            )
            ?? throw new System.IO.InvalidDataException(
                "Phantasy Tour returned an invalid listing."
            );
        await PreloadData(context.Artist);
        var now = DateTime.UtcNow;
        var selected = shows.Where(s =>
            s.dateTime.ToUniversalTime() <= now
            && !existingSetlistShows.ContainsKey(UpstreamIdentifierForPhantasyTourId(s.id))
        );
        return new ProviderPage(
            selected
                .Select(s => new ProviderItem(s.id.ToString(), JsonConvert.SerializeObject(s)))
                .ToArray(),
            shows.Length == ITEMS_PER_PAGE ? (page + 1).ToString() : null
        );
    }

    /// <inheritdoc />
    public async Task<ProviderFetch> FetchAsync(
        ProviderContext context,
        ProviderItem item,
        int part
    )
    {
        using var response = await http.GetAsync(
            UrlForShow(int.Parse(item.Key)),
            context.CancellationToken
        );
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(context.CancellationToken);
        if (JsonConvert.DeserializeObject<PhantasyTourEnvelope>(body)?.data == null)
            throw new System.IO.InvalidDataException(
                "Phantasy Tour returned invalid show metadata."
            );
        return new ProviderFetch(body);
    }

    /// <inheritdoc />
    public async Task<ImportStats> ApplyAsync(
        ProviderContext context,
        ProviderItem item,
        ProviderFetch[] fetched
    )
    {
        await PreloadData(context.Artist);
        var stats = new ImportStats();
        await ApplyShowBody(context.Artist, stats, null, int.Parse(item.Key), fetched[0].Body);
        if (context.Artist.features.tours && tourToStartDate.Count > 0)
            await UpdateTourStartEndDates(context.Artist);
        return stats;
    }
}
