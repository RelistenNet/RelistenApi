using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Relisten.Ingestion;
using Relisten.Vendor.Phishin;

namespace Relisten.Import;

// The prerequisite catalogs are reused across show activities. The snapshot keeps
// era and song-ID mappings stable without refetching four catalogs for every show.
public partial class PhishinImporter
{
    private record Catalogs(
        IDictionary<string, IList<string>> Eras,
        PhishinSmallTour[] Tours,
        PhishinSmallSong[] Songs,
        PhishinSmallVenue[] Venues
    );

    /// <inheritdoc />
    public override async Task<ImportStats> PrepareAsync(ProviderContext context)
    {
        if (context.Request.Item != null)
        {
            return new ImportStats();
        }

        var catalogs = await context.Store.SnapshotAsync(
            context.Run,
            "phish.in/catalogs",
            async () =>
                new Catalogs(
                    (
                        await PhishinApiRequest<IDictionary<string, IList<string>>>("eras", null)
                    ).data,
                    (await PhishinApiRequest<PhishinSmallTour[]>("tours", null)).data,
                    (await PhishinApiRequest<PhishinSmallSong[]>("songs", null)).data,
                    (await PhishinApiRequest<PhishinSmallVenue[]>("venues", null)).data
                )
        );

        return await context.CommitAsync(async () =>
        {
            await PreloadData(context.Artist);
            var stats = await ProcessEras(context.Artist, null, catalogs.Eras);
            stats += await ProcessTours(context.Artist, null, catalogs.Tours);
            stats += await ProcessSongs(context.Artist, null, catalogs.Songs);
            stats += await ProcessVenues(context.Artist, null, catalogs.Venues);
            return stats;
        });
    }

    /// <inheritdoc />
    public override async Task<DiscoverySnapshot<PhishinShow>> DiscoverAsync(
        ProviderContext context,
        string? cursor
    )
    {
        var page = cursor == null ? 1 : int.Parse(cursor);
        var listing = await PhishinApiRequest<IEnumerable<PhishinShow>>(
            "shows",
            null,
            "date",
            20,
            page
        );
        if (cursor == null && context.Request.Year.HasValue && listing.total_pages > 1)
        {
            return new DiscoverySnapshot<PhishinShow>(
                Array.Empty<DiscoveredItem<PhishinShow>>(),
                listing.total_pages.ToString()
            );
        }

        var shows = listing
            .data.Where(show =>
                context.Request.Year == null
                || string.Compare(
                    show.date,
                    context.Request.Year + "-01-01",
                    StringComparison.Ordinal
                ) >= 0
            )
            .ToArray();
        string? next = null;
        if (context.Request.Year.HasValue)
        {
            if (page > 1 && shows.Length > 0)
            {
                next = (page - 1).ToString();
            }
        }
        else if (page < listing.total_pages)
        {
            next = (page + 1).ToString();
        }

        return new DiscoverySnapshot<PhishinShow>(
            shows
                .Select(show => new DiscoveredItem<PhishinShow>(show.id.ToString(), show))
                .ToArray(),
            next
        );
    }

    /// <inheritdoc />
    public override async Task<ItemImportResult> ImportItemAsync(
        ProviderContext context,
        DiscoveredItem<PhishinShow> item
    )
    {
        var catalogs =
            await context.Store.ReadSnapshotAsync<Catalogs>(context.Run, "phish.in/catalogs")
            ?? throw new InvalidOperationException("Phish.in catalogs have not been prepared.");
        var show = item.Metadata;

        return await context.CommitAsync(async () =>
        {
            await PreloadData(context.Artist);
            RestoreCatalogMappings(catalogs);
            var stats = new ImportStats();
            await ProcessOneShow(stats, context.Artist, context.Source, show, null);
            return new ItemImportResult(stats);
        });
    }

    private void RestoreCatalogMappings(Catalogs catalogs)
    {
        foreach (var era in catalogs.Eras)
        {
            foreach (var year in era.Value)
            {
                yearToEraMapping[year] = existingEras[era.Key];
            }
        }

        var songsBySlug = existingSetlistSongs
            .Values.Where(song => song != null)
            .GroupBy(song => song!.slug)
            .ToDictionary(group => group.Key, group => group.First());
        foreach (var song in catalogs.Songs)
        {
            if (!song.alias_for.HasValue && songsBySlug.TryGetValue(song.slug, out var known))
            {
                existingSetlistSongs[song.id.ToString()] = known;
            }
        }
    }
}
