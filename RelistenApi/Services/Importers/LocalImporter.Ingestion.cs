using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Relisten.Ingestion;
using Relisten.Vendor.Local;

namespace Relisten.Import;

// The root catalog is reused for venue preparation and stable date selection.
// Directory metadata is fetched and applied within each date's item activity.
public partial class LocalImporter
{
    private Task<LocalRootObject<LocalShow>> RootCatalog(ProviderContext context) =>
        context.Store.SnapshotAsync(
            context.Run,
            "local/root",
            async () =>
            {
                var root = await LocalApiRequest<LocalShow>(
                    $"{context.Source.upstream_identifier}/metadata",
                    null
                );
                if (root?.data == null)
                {
                    throw new System.IO.InvalidDataException("Local metadata catalog is missing.");
                }

                return root;
            }
        );

    /// <inheritdoc />
    public override async Task<ImportStats> PrepareAsync(ProviderContext context)
    {
        if (context.Request.Item != null)
        {
            return new ImportStats();
        }

        var root = await RootCatalog(context);
        return await context.CommitAsync(async () =>
        {
            await PreloadData(context.Artist);
            return await ProcessVenues(context.Artist, context.Source, null, root);
        });
    }

    /// <inheritdoc />
    public override async Task<DiscoverySnapshot<LocalShow[]>> DiscoverAsync(
        ProviderContext context,
        string? cursor
    )
    {
        var root = await RootCatalog(context);
        await PreloadData(context.Artist);
        return new DiscoverySnapshot<LocalShow[]>(
            root.data.Where(day =>
                    day.Value.Count > 0
                    && (
                        !existingSources.TryGetValue(day.Key, out var source)
                        || source == null
                        || day.Value[0].updated_at > source.updated_at
                    )
                )
                .Select(day => new DiscoveredItem<LocalShow[]>(day.Key, day.Value.ToArray()))
                .ToArray()
        );
    }

    /// <inheritdoc />
    public override async Task<ItemImportResult> ImportItemAsync(
        ProviderContext context,
        DiscoveredItem<LocalShow[]> item
    )
    {
        var shows = item.Metadata;
        var metadata = new Dictionary<string, LocalShowObject>();
        foreach (var directory in shows.Select(show => show.dir).Distinct())
        {
            metadata[directory] =
                await LocalShowRequest(
                    $"{context.Source.upstream_identifier}/{directory}/metadata",
                    null
                )
                ?? throw new System.IO.InvalidDataException("Local directory metadata is missing.");
        }

        return await context.CommitAsync(async () =>
        {
            await PreloadData(context.Artist);
            var stats = new ImportStats();
            await ProcessOneDay(
                stats,
                context.Artist,
                context.Source,
                item.Identifier,
                shows,
                null,
                metadata
            );
            return new ItemImportResult(stats);
        });
    }
}
