using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Relisten.Search;

/// <summary>Holds the API's current search evidence and its startup grace period.</summary>
public sealed class CatalogQueryEvidenceStore
{
    private static readonly TimeSpan StartupGracePeriod = TimeSpan.FromSeconds(60);
    private readonly TimeProvider _clock;
    private readonly long _startedAt;
    private CatalogQueryEvidence _current = CatalogQueryEvidence.Empty;

    public CatalogQueryEvidenceStore()
        : this(TimeProvider.System) { }

    internal CatalogQueryEvidenceStore(TimeProvider clock)
    {
        _clock = clock;
        _startedAt = clock.GetTimestamp();
    }

    internal CatalogQueryEvidence Current => Volatile.Read(ref _current);

    // Give search a head start on replacement pods while keeping browse
    // available if loading evidence takes longer than the startup window.
    internal bool SearchStartupComplete =>
        Current.Collection != null || _clock.GetElapsedTime(_startedAt) >= StartupGracePeriod;

    internal void Publish(CatalogQueryEvidence snapshot) => Volatile.Write(ref _current, snapshot);
}

internal sealed class CatalogQueryEvidenceRefreshService(
    DbService db,
    TypesenseClient typesense,
    CatalogQueryEvidenceStore store,
    IConfiguration configuration,
    ILogger<CatalogQueryEvidenceRefreshService> logger
) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var alias = SearchIndexBuilder.AliasName(
            configuration["Search:AliasPrefix"] ?? "catalog",
            SearchIndexSchema.Entities
        );
        var exportPath = configuration["Search:PlaysExport"];
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Refresh(alias, exportPath, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning(
                    error,
                    "Could not refresh search query evidence for {Alias}",
                    alias
                );
            }

            await Task.Delay(RefreshInterval, stoppingToken);
        }
    }

    private async Task Refresh(
        string alias,
        string? exportPath,
        CancellationToken cancellationToken
    )
    {
        var collection = await typesense.ReadAlias(alias, cancellationToken);
        if (collection is null || collection == store.Current.Collection)
        {
            return;
        }

        if (store.Current.Collection != null)
        {
            // All pods see the same alias swap. Spread their full catalog reads
            // over a minute while the current evidence remains usable.
            await Task.Delay(
                TimeSpan.FromMilliseconds(Random.Shared.Next(60_001)),
                cancellationToken
            );
            collection = await typesense.ReadAlias(alias, cancellationToken);
            if (collection is null || collection == store.Current.Collection)
            {
                return;
            }
        }

        var loadTime = Stopwatch.StartNew();
        var snapshot = await CatalogQueryEvidenceLoader.Load(
            db,
            collection,
            exportPath,
            cancellationToken
        );

        // Keep serving the last complete snapshot if another build published
        // while this one was loading. The next refresh will use the new alias.
        if (await typesense.ReadAlias(alias, cancellationToken) == collection)
        {
            store.Publish(snapshot);
            logger.LogInformation(
                "Loaded search query evidence for {Collection} in {ElapsedMs} ms",
                collection,
                loadTime.ElapsedMilliseconds
            );
        }
    }
}
