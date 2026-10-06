using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Relisten.Ingestion;
using Relisten.Vendor;
using Relisten.Vendor.Local;

namespace Relisten.Import;

public partial class LocalImporter : IProviderIngestion
{
    /// <inheritdoc />
    public async Task<ProviderPage> DiscoverAsync(ProviderContext context, string? cursor)
    {
        if (context.Request.Item != null)
            return new ProviderPage(Array.Empty<ProviderItem>());
        var root = await LocalApiRequest<LocalShow>(
            $"{context.Source.upstream_identifier}/metadata",
            null
        );
        if (root?.data == null)
            throw new System.IO.InvalidDataException("Local metadata catalog is missing.");
        var items = new List<ProviderItem>
        {
            new("catalog/venues", JsonConvert.SerializeObject(root)),
        };
        await PreloadData(context.Artist);
        items.AddRange(
            root.data.Where(day =>
                    day.Value.Count > 0
                    && (
                        !existingSources.TryGetValue(day.Key, out var source)
                        || source == null
                        || day.Value[0].updated_at > source.updated_at
                    )
                )
                .Select(day => new ProviderItem(day.Key, JsonConvert.SerializeObject(day.Value)))
        );
        return new ProviderPage(items.ToArray());
    }

    /// <inheritdoc />
    public async Task<ProviderFetch> FetchAsync(
        ProviderContext context,
        ProviderItem item,
        int part
    )
    {
        if (item.Key == "catalog/venues")
            return new ProviderFetch("{}");
        var shows = JsonConvert.DeserializeObject<LocalShow[]>(item.Body)!;
        var show = shows[part];
        var data = await LocalShowRequest(
            $"{context.Source.upstream_identifier}/{show.dir}/metadata",
            null
        );
        if (data == null)
            throw new System.IO.InvalidDataException("Local directory metadata is missing.");
        return new ProviderFetch(JsonConvert.SerializeObject(data), part == shows.Length - 1);
    }

    /// <inheritdoc />
    public async Task<ImportStats> ApplyAsync(
        ProviderContext context,
        ProviderItem item,
        ProviderFetch[] fetched
    )
    {
        await PreloadData(context.Artist);
        if (item.Key == "catalog/venues")
            return await ProcessVenues(
                context.Artist,
                context.Source,
                null,
                JsonConvert.DeserializeObject<LocalRootObject<LocalShow>>(item.Body)!
            );
        var shows = JsonConvert.DeserializeObject<LocalShow[]>(item.Body)!;
        var metadata = shows
            .Select(
                (s, i) =>
                    (s.dir, Data: JsonConvert.DeserializeObject<LocalShowObject>(fetched[i].Body)!)
            )
            .GroupBy(x => x.dir)
            .ToDictionary(g => g.Key, g => g.First().Data);
        var stats = new ImportStats();
        await ProcessOneDay(stats, context.Artist, context.Source, item.Key, shows, null, metadata);
        return stats;
    }
}
