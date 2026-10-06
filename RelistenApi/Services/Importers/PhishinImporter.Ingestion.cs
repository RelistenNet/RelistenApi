using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Relisten.Ingestion;
using Relisten.Vendor;
using Relisten.Vendor.Phishin;

namespace Relisten.Import;

public partial class PhishinImporter : IProviderIngestion
{
    /// <inheritdoc />
    public async Task<ProviderPage> DiscoverAsync(ProviderContext context, string? cursor)
    {
        if (context.Request.Item != null)
            return new ProviderPage(Array.Empty<ProviderItem>());
        var stage = cursor ?? "eras";
        var catalogs = new[] { "eras", "tours", "songs", "venues" };
        var index = Array.IndexOf(catalogs, stage);
        if (index >= 0)
        {
            using var response = await http.GetAsync(
                PhishinApiUrl(stage),
                context.CancellationToken
            );
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(context.CancellationToken);
            var root = Newtonsoft.Json.Linq.JObject.Parse(body);
            var data =
                root["data"]
                ?? throw new System.IO.InvalidDataException("Missing Phish.in catalog data.");
            var item = new ProviderItem("catalog/" + stage, data.ToString());
            return new ProviderPage(
                new[] { item },
                index == 3 ? "shows:initial" : catalogs[index + 1]
            );
        }
        var initial = stage == "shows:initial";
        var page = initial ? 1 : int.Parse(stage.Split(':')[1]);
        var rootShows = await PhishinApiRequest<IEnumerable<PhishinShow>>(
            "shows",
            null,
            "date",
            20,
            page
        );
        if (initial && context.Request.Year.HasValue && rootShows.total_pages > 1)
            return new ProviderPage(Array.Empty<ProviderItem>(), "shows:" + rootShows.total_pages);
        var shows = rootShows
            .data.Where(s =>
                context.Request.Year == null
                || string.Compare(s.date, context.Request.Year + "-01-01", StringComparison.Ordinal)
                    >= 0
            )
            .ToArray();
        var next = context.Request.Year.HasValue
            ? page > 1 && shows.Length > 0
                ? "shows:" + (page - 1)
                : null
            : page < rootShows.total_pages
                ? "shows:" + (page + 1)
                : null;
        return new ProviderPage(
            shows
                .Select(s => new ProviderItem(s.id.ToString(), JsonConvert.SerializeObject(s)))
                .ToArray(),
            next
        );
    }

    /// <inheritdoc />
    public Task<ProviderFetch> FetchAsync(ProviderContext context, ProviderItem item, int part) =>
        Task.FromResult(new ProviderFetch("{}"));

    /// <inheritdoc />
    public async Task<ImportStats> ApplyAsync(
        ProviderContext context,
        ProviderItem item,
        ProviderFetch[] fetched
    )
    {
        await PreloadData(context.Artist);
        switch (item.Key)
        {
            case "catalog/eras":
                return await ProcessEras(
                    context.Artist,
                    null,
                    JsonConvert.DeserializeObject<IDictionary<string, IList<string>>>(item.Body)!
                );
            case "catalog/tours":
                return await ProcessTours(
                    context.Artist,
                    null,
                    JsonConvert.DeserializeObject<PhishinSmallTour[]>(item.Body)!
                );
            case "catalog/songs":
                return await ProcessSongs(
                    context.Artist,
                    null,
                    JsonConvert.DeserializeObject<PhishinSmallSong[]>(item.Body)!
                );
            case "catalog/venues":
                return await ProcessVenues(
                    context.Artist,
                    null,
                    JsonConvert.DeserializeObject<PhishinSmallVenue[]>(item.Body)!
                );
        }
        // Restore lookup state previously held across the old whole-artist method.
        var eras = await context.Store.ReadAsync<ProviderItem>(
            context.Request.RequestId,
            "phish.in/page/first/item/0"
        );
        foreach (
            var era in JsonConvert.DeserializeObject<IDictionary<string, IList<string>>>(
                eras!.Body
            )!
        )
        foreach (var year in era.Value)
            yearToEraMapping[year] = existingEras[era.Key];
        var songs = await context.Store.ReadAsync<ProviderItem>(
            context.Request.RequestId,
            "phish.in/page/songs/item/0"
        );
        var songsBySlug = existingSetlistSongs
            .Values.Where(s => s != null)
            .GroupBy(s => s!.slug)
            .ToDictionary(g => g.Key, g => g.First());
        foreach (var song in JsonConvert.DeserializeObject<PhishinSmallSong[]>(songs!.Body)!)
            if (!song.alias_for.HasValue && songsBySlug.TryGetValue(song.slug, out var known))
                existingSetlistSongs[song.id.ToString()] = known;
        var stats = new ImportStats();
        await ProcessOneShow(
            stats,
            context.Artist,
            context.Source,
            JsonConvert.DeserializeObject<PhishinShow>(item.Body)!,
            null
        );
        return stats;
    }
}
