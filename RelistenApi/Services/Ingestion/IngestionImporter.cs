using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Import;
using Temporalio.Exceptions;

namespace Relisten.Ingestion;

/// <summary>Provider flow for ordinary listing pages with no additional reconciliation evidence.</summary>
public abstract class IngestionImporter<TItem>(CatalogRebuildService catalog, ILogger log)
    : IngestionImporter<TItem, DiscoverySnapshot<TItem>>(catalog, log)
    where TItem : class;

/// <summary>
/// Keeps discovery, snapshot persistence, and item loading typed through to the provider.
/// Temporal receives only bounded item references. JSON encoding belongs to IngestionStore.
/// </summary>
public abstract class IngestionImporter<TItem, TDiscovery>(
    CatalogRebuildService catalog,
    ILogger log
) : ImporterBase(catalog, log), IProviderIngestion
    where TItem : class
    where TDiscovery : DiscoverySnapshot<TItem>
{
    /// <inheritdoc />
    public virtual Task<ImportStats> PrepareAsync(ProviderContext context) =>
        Task.FromResult(new ImportStats());

    /// <summary>Fetches a provider's typed listing page, including any reconciliation evidence.</summary>
    public abstract Task<TDiscovery> DiscoverAsync(ProviderContext context, string? cursor);

    /// <summary>Fetches related inputs and commits one selected item using its concrete listing type.</summary>
    public abstract Task<ItemImportResult> ImportItemAsync(
        ProviderContext context,
        DiscoveredItem<TItem> item
    );

    /// <summary>Retains legacy no-op behavior for providers that do not support targeted imports.</summary>
    public virtual Task<ItemImportResult> ReimportItemAsync(
        ProviderContext context,
        string identifier
    ) => Task.FromResult(new ItemImportResult(new ImportStats()));

    /// <inheritdoc />
    public virtual Task<ProviderResult> ReconcileAsync(
        ProviderContext context,
        string[] ineligibleItems,
        bool hasFailures
    ) => Task.FromResult(new ProviderResult(new ImportStats()));

    protected string DiscoveryName(string? cursor) =>
        Provider.ToValue() + "/discovery/" + (cursor ?? "first");

    async Task<ImportPage> IProviderIngestion.DiscoverPageAsync(
        ProviderContext context,
        DiscoveryCursor? cursor
    )
    {
        var position = cursor ?? new DiscoveryCursor();
        var name = DiscoveryName(position.Upstream);
        var snapshot = await context.Store.SnapshotAsync(
            context.Run,
            name,
            () => DiscoverAsync(context, position.Upstream)
        );
        var items = snapshot
            .Items.Skip(position.Offset)
            .Take(100)
            .Select((item, index) => new ImportItem(item.Identifier, name, position.Offset + index))
            .ToArray();
        var nextOffset = position.Offset + items.Length;
        DiscoveryCursor? next = null;

        if (nextOffset < snapshot.Items.Length)
        {
            next = position with { Offset = nextOffset };
        }
        else if (snapshot.NextCursor != null)
        {
            if (snapshot.NextCursor == position.Upstream)
            {
                throw new ApplicationFailureException(
                    "Provider discovery cursor did not advance.",
                    nonRetryable: true
                );
            }

            next = new DiscoveryCursor(snapshot.NextCursor);
        }

        return new ImportPage(items, next);
    }

    async Task<ItemImportResult> IProviderIngestion.ImportAsync(
        ProviderContext context,
        ImportItem? item
    )
    {
        if (item == null)
        {
            return await ReimportItemAsync(
                context,
                context.Request.Item
                    ?? throw new ArgumentException("An item identifier is required.")
            );
        }

        var input = await context.Store.ReadItemAsync<TItem>(context.Run, item);
        return await ImportItemAsync(context, input);
    }
}
