using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.Local;
using Relisten.Vendor.Local;

namespace Relisten.Import;

// One root response supplies venues and date selection. Each date merges its directories into one source.
public partial class LocalImporter
{
    /// <summary>Fetches the metadata catalog once and commits its venue prerequisites.</summary>
    public async Task<LocalRootObject<LocalShow>> FetchRootAndPrepareVenuesAsync(
        ProviderContext context
    )
    {
        var root = await LocalApiRequest<LocalShow>(
            $"{context.Source.upstream_identifier}/metadata",
            null
        );
        if (root?.data == null)
        {
            throw new System.IO.InvalidDataException("Local metadata catalog is missing.");
        }

        await context.CommitAsync(async () =>
        {
            await PreloadData(context.Artist);
            return await ProcessVenues(context.Artist, context.Source, null, root);
        });

        return root;
    }

    /// <summary>Selects changed dates from the recorded root response using the existing timestamp rule.</summary>
    public async Task<LocalSelection> SelectChangedDatesAsync(
        ProviderContext context,
        LocalRootObject<LocalShow> root
    )
    {
        await PreloadData(context.Artist);

        return new(
            root.data.Where(day =>
                    day.Value.Count > 0
                    && (
                        !existingSources.TryGetValue(day.Key, out var source)
                        || source == null
                        || day.Value[0].updated_at > source.updated_at
                    )
                )
                .Select(day => new DiscoveredItem<LocalShowDate, LocalShow[]>(
                    new(day.Key),
                    day.Value.ToArray()
                ))
                .ToArray()
        );
    }

    /// <summary>Merges all selected directories for one date into its existing catalog source.</summary>
    public async Task<LocalItemResult> ImportDateAsync(
        ProviderContext context,
        DiscoveredItem<LocalShowDate, LocalShow[]> item
    )
    {
        // Fetch all directories before taking the artist lock. The transaction
        // merges them into one source for this date, retaining its catalog UUID.
        var metadata = await FetchDirectoryMetadataAsync(context, item.Input);

        return await context.CommitAsync(async () =>
        {
            await PreloadData(context.Artist);

            var unchanged =
                existingSources.TryGetValue(item.Id.Value, out var old)
                && old != null
                && item.Input[0].updated_at <= old.updated_at;

            await ProcessOneDay(
                new ImportStats(),
                context.Artist,
                context.Source,
                item.Id.Value,
                item.Input,
                null,
                metadata
            );

            var source =
                await _sourceService.ForUpstreamIdentifierFromPrimary(context.Artist, item.Id.Value)
                ?? throw new InvalidOperationException(
                    "Local date import did not produce a source."
                );

            return new LocalItemResult(
                item.Id,
                unchanged ? ItemDisposition.Unchanged : ItemDisposition.Applied,
                new(source.id)
            );
        });
    }

    private async Task<Dictionary<string, LocalShowObject>> FetchDirectoryMetadataAsync(
        ProviderContext context,
        LocalShow[] shows
    )
    {
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

        return metadata;
    }
}
