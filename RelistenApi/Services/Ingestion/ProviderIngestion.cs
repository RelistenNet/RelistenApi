using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Import;

namespace Relisten.Ingestion;

/// <summary>Provider-specific listing metadata kept only in an expiring discovery snapshot.</summary>
public record DiscoveredItem(string Identifier, string Metadata);

/// <summary>An immutable upstream listing page. Observed identifiers support Archive reconciliation.</summary>
public record DiscoverySnapshot(
    DiscoveredItem[] Items,
    string? NextCursor = null,
    string[]? ObservedIdentifiers = null,
    IReadOnlyDictionary<string, DateTime>? SetlistDates = null
);

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
            Source.upstream_source.name + "/" + Operation,
            apply,
            CancellationToken
        );
    }
}

/// <summary>Domain operations shared by artist refreshes and targeted imports. GETs may repeat on retry.</summary>
public interface IProviderIngestion
{
    /// <summary>Fetches and applies prerequisite catalogs before any items are imported.</summary>
    Task<ImportStats> PrepareAsync(ProviderContext context) => Task.FromResult(new ImportStats());

    /// <summary>Fetches one listing page; the caller retains its selection for this run.</summary>
    Task<DiscoverySnapshot> DiscoverAsync(ProviderContext context, string? cursor);

    /// <summary>Fetches related inputs, then commits one item's catalog changes through the context.</summary>
    Task<ItemImportResult> ImportItemAsync(ProviderContext context, DiscoveredItem item);

    /// <summary>Finalizes a completely enumerated provider. Failed items may suppress provider-specific cleanup.</summary>
    Task<ProviderResult> ReconcileAsync(
        ProviderContext context,
        string[] ineligibleItems,
        bool hasFailures
    ) => Task.FromResult(new ProviderResult(new ImportStats()));
}
