using System;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Import;

namespace Relisten.Ingestion;

/// <summary>One selected upstream item and its concrete listing data.</summary>
public record DiscoveredItem<TItem>(string Identifier, TItem Metadata)
    where TItem : class;

/// <summary>A typed immutable listing page. Providers can extend it with reconciliation evidence.</summary>
public record DiscoverySnapshot<TItem>(DiscoveredItem<TItem>[] Items, string? NextCursor = null)
    where TItem : class;

/// <summary>Services and identity for a single activity. Fetch inputs before entering CommitAsync.</summary>
public record ProviderContext(
    Artist Artist,
    ArtistUpstreamSource Source,
    ArtistImportRequest Request,
    IngestionStore Store,
    string Operation,
    CancellationToken CancellationToken
)
{
    public IngestionRun Run =>
        Request.Run
        ?? throw new InvalidOperationException("The workflow must assign an ingestion run.");

    /// <summary>Atomically commits catalog writes and their result; a repeated attempt returns the receipt.</summary>
    public Task<T> CommitAsync<T>(Func<Task<T>> apply)
        where T : class
    {
        CancellationToken.ThrowIfCancellationRequested();
        return Store.CommitAsync(
            Run,
            Source.upstream_source.Provider!.Value.ToValue() + "/" + Operation,
            apply,
            CancellationToken
        );
    }
}

/// <summary>Domain operations shared by artist refreshes and targeted imports. GETs may repeat on retry.</summary>
public interface IProviderIngestion
{
    /// <summary>Fetches and applies prerequisite catalogs before any items are imported.</summary>
    Task<ImportStats> PrepareAsync(ProviderContext context);

    /// <summary>Fetches one listing page; the caller retains its selection for this run.</summary>
    Task<ImportPage> DiscoverPageAsync(ProviderContext context, DiscoveryCursor? cursor);

    /// <summary>Fetches related inputs, then commits one item's catalog changes through the context.</summary>
    Task<ItemImportResult> ImportAsync(ProviderContext context, ImportItem? item);

    /// <summary>Finalizes a completely enumerated provider. Failed items may suppress provider-specific cleanup.</summary>
    Task<ProviderResult> ReconcileAsync(
        ProviderContext context,
        string[] ineligibleItems,
        bool hasFailures
    );
}
