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
    private readonly long _startedAt;
    private PublishedEvidence _current = new(CatalogQueryEvidence.Empty, 0);

    public CatalogQueryEvidenceStore()
        : this(TimeProvider.System) { }

    internal CatalogQueryEvidenceStore(TimeProvider clock)
    {
        Clock = clock;
        _startedAt = clock.GetTimestamp();
    }

    internal CatalogQueryEvidence Current => Volatile.Read(ref _current).Evidence;
    internal TimeProvider Clock { get; }

    // Give search a head start on replacement pods while keeping browse
    // available if loading evidence takes longer than the startup window.
    internal bool SearchStartupComplete =>
        Current.Collection != null || Clock.GetElapsedTime(_startedAt) >= StartupGracePeriod;

    internal bool IsFresh(string collection, TimeSpan maximumAge)
    {
        var current = Volatile.Read(ref _current);
        return current.Evidence.Collection == collection
            && Clock.GetElapsedTime(current.Timestamp) < maximumAge;
    }

    internal void Publish(CatalogQueryEvidence snapshot) =>
        Volatile.Write(ref _current, new PublishedEvidence(snapshot, Clock.GetTimestamp()));

    private sealed record PublishedEvidence(CatalogQueryEvidence Evidence, long Timestamp);
}

internal sealed class CatalogQueryEvidenceRefreshService(
    DbService db,
    TypesenseClient typesense,
    CatalogQueryEvidenceStore store,
    IConfiguration configuration,
    ILogger<CatalogQueryEvidenceRefreshService> logger
) : BackgroundService
{
    private static readonly TimeSpan AliasPollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumEvidenceAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan FailedInitialLoadDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FailedRefreshDelay = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private int _urgent;

    public void RefreshNow()
    {
        Interlocked.Exchange(ref _urgent, 1);
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already queued.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The dedicated Hangfire process shares the API image but serves no search traffic.
        if (!configuration.GetValue("Search:RefreshQueryEvidence", true))
        {
            return;
        }

        var prefix = configuration["Search:AliasPrefix"] ?? "catalog";
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var urgent = Interlocked.Exchange(ref _urgent, 0) != 0;
                await Refresh(prefix, urgent, stoppingToken);
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
                    prefix
                );
                // Missing-generation traffic may queue a wake-up while the load
                // is running. That signal must not bypass the retry delay.
                // Retry sooner during startup, when this pod has no search evidence to serve.
                var delay = store.Current.Collection is null
                    ? FailedInitialLoadDelay
                    : FailedRefreshDelay;
                await Task.Delay(delay, store.Clock, stoppingToken);
                continue;
            }

            await _wake.WaitAsync(AliasPollInterval, stoppingToken);
        }
    }

    private async Task Refresh(string prefix, bool urgent, CancellationToken cancellationToken)
    {
        // The index builder publishes entities last. Its alias is the commit
        // point for the generation shared by all catalog collections.
        var alias = SearchIndexBuilder.AliasName(prefix, SearchIndexSchema.Entities);
        var collection = await typesense.ReadAlias(alias, cancellationToken);
        // Artist reindexes change evidence in place without moving the alias.
        if (collection is null || store.IsFresh(collection, MaximumEvidenceAge))
        {
            return;
        }

        if (!urgent && store.Current.Collection != null)
        {
            // Pods can see the same alias swap or reach the refresh age together.
            // Spread their catalog reads while the current evidence remains usable.
            await _wake.WaitAsync(
                TimeSpan.FromMilliseconds(Random.Shared.Next(60_001)),
                cancellationToken
            );
            collection = await typesense.ReadAlias(alias, cancellationToken);
            if (collection is null || store.IsFresh(collection, MaximumEvidenceAge))
            {
                return;
            }
        }

        var loadTime = Stopwatch.StartNew();
        // Read the performance names from the same generation as the song
        // entities, even if the aliases change while this snapshot loads.
        var performances = SearchIndexBuilder.CollectionName(
            prefix,
            SearchIndexSchema.Performances,
            collection[(alias.Length + 1)..]
        );
        var snapshot = await CatalogQueryEvidenceLoader.Load(
            db,
            typesense,
            collection,
            performances,
            logger,
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
