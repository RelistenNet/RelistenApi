using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Import;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace Relisten.Ingestion;

/// <summary>Bridges Temporal activities to provider operations; only database commits use retry receipts.</summary>
public sealed class IngestionActivities(
    ArtistService artists,
    ImporterService importers,
    IngestionStore store,
    IngestionHttpClientFactory httpClients,
    ILogger<IngestionActivities> log
) : IIngestionActivities
{
    [Activity("Ingestion.PrepareArtist")]
    public async Task<ArtistImportPlan> PrepareArtist(ArtistImportRequest request)
    {
        using var heartbeat = new ActivityHeartbeat();
        using var logging = BeginLogScope(request);
        IngestionStore.RequireActive(request.Run!);
        var artist =
            await artists.FindArtistWithIdOrSlug(request.Artist)
            ?? throw new ApplicationFailureException("Artist not found.", nonRetryable: true);
        var providers = artist
            .upstream_sources.Select(source => source.upstream_source.Provider)
            .Where(provider => provider.HasValue)
            .Select(provider => provider!.Value)
            .Where(IngestionQueues.Providers.ContainsKey)
            .Where(provider => request.Provider == null || provider == request.Provider)
            .ToArray();

        if (request.Provider != null && providers.Length == 0)
        {
            throw new ApplicationFailureException(
                "Artist has no enabled mapping for the requested provider.",
                nonRetryable: true
            );
        }

        log.LogInformation(
            "Prepared artist with {ProviderCount} enabled providers",
            providers.Length
        );
        return new ArtistImportPlan(artist.uuid.ToString(), providers);
    }

    [Activity("Ingestion.PrepareProvider")]
    public async Task<ImportStats> PrepareProvider(
        ArtistImportRequest request,
        UpstreamProvider provider
    )
    {
        using var heartbeat = new ActivityHeartbeat();
        using var logging = BeginLogScope(request, provider);
        var saved = await store.ReadReceiptAsync<ImportStats>(
            request.Run!,
            provider.ToValue() + "/prepare"
        );
        if (saved != null)
        {
            log.LogInformation("Returning previously committed result");
            return saved;
        }

        var (context, importer) = await Provider(request, provider, "prepare");
        log.LogInformation("Preparing provider catalogs");
        var result = await importer.PrepareAsync(context);
        log.LogInformation("Provider preparation completed: {Stats}", result);
        return result;
    }

    [Activity("Ingestion.DiscoverItems")]
    public async Task<ImportPage> DiscoverItems(
        ArtistImportRequest request,
        UpstreamProvider provider,
        DiscoveryCursor? cursor
    )
    {
        using var heartbeat = new ActivityHeartbeat();
        using var logging = BeginLogScope(request, provider);
        var (context, importer) = await Provider(request, provider, "discover");
        var page = await importer.DiscoverPageAsync(context, cursor);
        log.LogInformation(
            "Discovered {ItemCount} items; more pages: {HasNextPage}",
            page.Items.Length,
            page.NextCursor != null
        );
        return page;
    }

    [Activity("Ingestion.ImportItem")]
    public async Task<ItemImportResult> ImportItem(
        ArtistImportRequest request,
        UpstreamProvider provider,
        ImportItem? item
    )
    {
        using var heartbeat = new ActivityHeartbeat();
        using var logging = BeginLogScope(request, provider, item?.Identifier ?? request.Item);
        var identifier =
            item?.Identifier
            ?? request.Item
            ?? throw new ArgumentException("An item identifier is required.");
        var operation = "item/" + identifier;
        var saved = await store.ReadReceiptAsync<ItemImportResult>(
            request.Run!,
            provider.ToValue() + "/" + operation
        );
        if (saved != null)
        {
            log.LogInformation("Returning previously committed result");
            return saved;
        }

        var (context, importer) = await Provider(request, provider, operation);
        log.LogInformation("Importing item");
        var result = await importer.ImportAsync(context, item);
        log.LogInformation(
            ImportEvents.ItemCompleted,
            "Item completed: {Created} created, {Updated} updated, {Removed} removed; ineligible: {Ineligible}",
            result.Stats.Created,
            result.Stats.Updated,
            result.Stats.Removed,
            result.Ineligible
        );
        return result;
    }

    [Activity("Ingestion.ReconcileProvider")]
    public async Task<ProviderResult> ReconcileProvider(
        ArtistImportRequest request,
        UpstreamProvider provider,
        string[] ineligibleItems,
        bool hasFailures
    )
    {
        using var heartbeat = new ActivityHeartbeat();
        using var logging = BeginLogScope(request, provider);
        var saved = await store.ReadReceiptAsync<ProviderResult>(
            request.Run!,
            provider.ToValue() + "/reconcile"
        );
        if (saved != null)
        {
            log.LogInformation("Returning previously committed result");
            return saved;
        }

        var (context, importer) = await Provider(request, provider, "reconcile");
        var result = await importer.ReconcileAsync(context, ineligibleItems, hasFailures);
        log.LogInformation("Provider reconciliation completed: {Stats}", result.Stats);
        return result;
    }

    [Activity("Ingestion.FinishArtist")]
    public async Task<ImportResult> FinishArtist(ArtistImportRequest request, ImportResult result)
    {
        using var heartbeat = new ActivityHeartbeat();
        using var logging = BeginLogScope(request);
        var artist =
            await artists.FindArtistWithIdOrSlug(request.Artist)
            ?? throw new ApplicationFailureException("Artist not found.", nonRetryable: true);

        IngestionStore.RequireActive(request.Run!);
        // Rebuild SQL owns its transactions and is safe to repeat. Wrapping it in
        // an item receipt transaction would let its COMMIT close that transaction.
        // Always rebuild: a cancelled earlier import may have committed items only.
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        log.LogInformation("Rebuilding artist aggregates");
        await importers.Rebuild(artist, null);
        cancellationToken.ThrowIfCancellationRequested();
        log.LogInformation(
            "Artist import finished: {Stats}; {FailedItems} failed items",
            result.Stats,
            result.FailedItems
        );
        return result;
    }

    private IDisposable? BeginLogScope(
        ArtistImportRequest request,
        UpstreamProvider? provider = null,
        string? item = null
    )
    {
        var activity = ActivityExecutionContext.Current.Info;
        return log.BeginScope(
            new Dictionary<string, object?>
            {
                ["RequestId"] = request.RequestId,
                ["Artist"] = request.Artist,
                ["Provider"] = provider?.ToValue(),
                ["ItemIdentifier"] = item,
                ["WorkflowId"] = activity.WorkflowId,
                ["ActivityId"] = activity.ActivityId,
                ["Attempt"] = activity.Attempt,
            }
        );
    }

    private async Task<(ProviderContext Context, IProviderIngestion Importer)> Provider(
        ArtistImportRequest request,
        UpstreamProvider provider,
        string operation
    )
    {
        IngestionStore.RequireActive(request.Run!);
        var artist =
            await artists.FindArtistWithIdOrSlug(request.Artist)
            ?? throw new ApplicationFailureException("Artist not found.", nonRetryable: true);
        var source = artist.upstream_sources.Single(source =>
            source.upstream_source.Provider == provider
        );
        var importer = source.upstream_source.importer!;
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        importer.UseIngestionHttpClient(httpClients.Create(cancellationToken));
        importer.CurrentImportOptions = new ImportOptions { OnlyYear = request.Year };
        var context = new ProviderContext(
            artist,
            source,
            request,
            store,
            operation,
            cancellationToken
        );
        return (context, (IProviderIngestion)importer);
    }
}
