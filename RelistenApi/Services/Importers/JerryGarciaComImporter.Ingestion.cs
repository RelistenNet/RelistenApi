using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Relisten.Ingestion;
using Relisten.Vendor;

namespace Relisten.Import;

public partial class JerryGarciaComImporter : IProviderIngestion
{
    /// <inheritdoc />
    public async Task<ProviderPage> DiscoverAsync(ProviderContext context, string? cursor)
    {
        if (context.Request.Item != null)
            return new ProviderPage(Array.Empty<ProviderItem>());
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
        return new ProviderPage(
            BuildFileMetaObjects(files)
                .Where(f =>
                    context.Request.BackfillJerryVenues
                        ? existingSetlistShows.ContainsKey(f.Identifier)
                        : !existingSetlistShows.ContainsKey(f.Identifier)
                )
                .Select(f => new ProviderItem(f.Identifier, JsonConvert.SerializeObject(f)))
                .ToArray()
        );
    }

    /// <inheritdoc />
    public async Task<ProviderFetch> FetchAsync(
        ProviderContext context,
        ProviderItem item,
        int part
    )
    {
        var file = JsonConvert.DeserializeObject<FileMetaObject>(item.Body)!;
        using var response = await http.GetAsync(
            ShowPageUrl(context.Source, file.FilePath),
            context.CancellationToken
        );
        response.EnsureSuccessStatusCode();
        return new ProviderFetch(
            await response.Content.ReadAsStringAsync(context.CancellationToken),
            ModifiedAt: response.Content.Headers.LastModified?.UtcDateTime ?? DateTime.UtcNow
        );
    }

    /// <inheritdoc />
    public async Task<ImportStats> ApplyAsync(
        ProviderContext context,
        ProviderItem item,
        ProviderFetch[] fetched
    )
    {
        await PreloadData(context.Artist);
        foreach (var tour in existingTours.Values.Where(t => t != null))
        {
            if (tour!.start_date.HasValue)
                tourToStartDate[tour.upstream_identifier] = tour.start_date.Value;
            if (tour.end_date.HasValue)
                tourToEndDate[tour.upstream_identifier] = tour.end_date.Value;
        }
        var stats = new ImportStats();
        if (context.Request.BackfillJerryVenues)
        {
            await ApplyVenueBackfill(
                stats,
                context.Artist,
                JsonConvert.DeserializeObject<FileMetaObject>(item.Body)!,
                fetched[0].Body,
                fetched[0].ModifiedAt!.Value
            );
            return stats;
        }
        await ProcessPage(
            stats,
            context.Artist,
            JsonConvert.DeserializeObject<FileMetaObject>(item.Body)!,
            fetched[0].Body,
            fetched[0].ModifiedAt!.Value,
            null
        );
        if (context.Artist.features.tours && tourToStartDate.Count > 0)
            await UpdateTourStartEndDates(context.Artist);
        return stats;
    }
}
