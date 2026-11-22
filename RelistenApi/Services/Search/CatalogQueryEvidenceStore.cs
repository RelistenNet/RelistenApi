using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Relisten.Search;

internal sealed class CatalogQueryEvidenceStore
{
    private CatalogQueryEvidence _current = CatalogQueryEvidence.Empty;

    public CatalogQueryEvidence Current => Volatile.Read(ref _current);

    public void Publish(CatalogQueryEvidence snapshot) => Volatile.Write(ref _current, snapshot);
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
            logger.LogInformation("Loaded search query evidence for {Collection}", collection);
        }
    }
}
