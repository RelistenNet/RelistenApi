using System;
using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Ingestion.Providers.Archive;
using Relisten.Ingestion.Providers.TapersSection;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace Relisten.Ingestion.Providers.ArchiveCollections;

/// <summary>
/// Fetches provider evidence and retains collection discovery progress. Exact-source resolution
/// can link canonical playback, but these activities never materialize artists or import recordings.
/// </summary>
public sealed class ArchiveCollectionDiscoveryActivities(
    CollectionDiscoveryService discovery,
    ArchiveIntakeService intake,
    ArchiveItemResolutionService resolutions,
    IngestionHttpClientFactory httpClients,
    NormalizationProviderInputService normalizationInputs
)
{
    [Activity("ArchiveCollections.BeginOrResume")]
    public async Task<ArchiveCollectionDiscoveryCheckpoint> BeginOrResume(
        IngestionExecution execution,
        Guid selectorUuid
    )
    {
        using var heartbeat = new ActivityHeartbeat();
        RequireActive(execution);
        return await discovery.BeginOrResumeAsync(
            selectorUuid,
            execution.Deadline,
            ActivityExecutionContext.Current.CancellationToken
        );
    }

    [Activity("ArchiveCollections.FetchPage")]
    public async Task<ArchiveCollectionSearchPage> FetchPage(
        IngestionExecution execution,
        ArchiveCollectionDiscoveryCheckpoint checkpoint
    )
    {
        using var heartbeat = new ActivityHeartbeat();
        RequireActive(execution);
        var token = ActivityExecutionContext.Current.CancellationToken;
        try
        {
            using var http = httpClients.Create(token);
            return await new ArchiveCollectionSearchClient(
                http,
                checkpoint.CollectionIdentifier
            ).FetchPageAsync(checkpoint.Cursor, token);
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            await discovery.RecordScanFailureAsync(checkpoint, error.Message, token);
            throw;
        }
    }

    [Activity("ArchiveCollections.ApplyPage")]
    public async Task<ArchiveCollectionDiscoveryCheckpoint> ApplyPage(
        IngestionExecution execution,
        ArchiveCollectionDiscoveryCheckpoint checkpoint,
        ArchiveCollectionSearchPage page
    )
    {
        using var heartbeat = new ActivityHeartbeat();
        RequireActive(execution);
        var token = ActivityExecutionContext.Current.CancellationToken;
        try
        {
            return await discovery.ApplyPageAsync(checkpoint, page, execution.Deadline, token);
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            await discovery.RecordScanFailureAsync(checkpoint, error.Message, token);
            throw;
        }
    }

    [Activity("ArchiveCollections.ObserveItem")]
    public async Task<ArchiveCollectionObservationResult> ObserveItem(
        IngestionExecution execution,
        ArchiveItemId item
    )
    {
        using var heartbeat = new ActivityHeartbeat();
        RequireActive(execution);
        var token = ActivityExecutionContext.Current.CancellationToken;
        try
        {
            using var http = httpClients.Create(token);
            var raw = await ArchiveNormalizationInput.FetchAsync(
                http,
                normalizationInputs,
                item,
                token
            );
            var projection = ArchiveObservationProjector.Project(item, raw);
            RequireActive(execution);
            var observationId = await intake.RetainObservationAsync(
                projection,
                token,
                execution.Deadline
            );
            RequireActive(execution);
            var resolution = await resolutions.ResolveExistingAsync(
                new ArchiveItemResolutionRequest(item.Value) { observation_id = observationId },
                token,
                execution.Deadline
            );
            return new(item, resolution.status == "resolved");
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            await resolutions.RecordFailureAsync(item.Value, error.Message);
            throw;
        }
    }

    [Activity("ArchiveCollections.AcknowledgeItems")]
    public async Task<ArchiveCollectionDiscoveryCheckpoint> AcknowledgeItems(
        IngestionExecution execution,
        ArchiveCollectionDiscoveryCheckpoint checkpoint,
        string[] settledItems
    )
    {
        using var heartbeat = new ActivityHeartbeat();
        RequireActive(execution);
        return await discovery.AcknowledgeItemsAsync(
            checkpoint,
            settledItems,
            execution.Deadline,
            ActivityExecutionContext.Current.CancellationToken
        );
    }

    [Activity("ArchiveCollections.Complete")]
    public async Task<ArchiveCollectionDiscoveryCheckpoint> Complete(
        IngestionExecution execution,
        ArchiveCollectionDiscoveryCheckpoint checkpoint
    )
    {
        using var heartbeat = new ActivityHeartbeat();
        RequireActive(execution);
        return await discovery.CompleteAsync(
            checkpoint,
            execution.Deadline,
            ActivityExecutionContext.Current.CancellationToken
        );
    }

    private static void RequireActive(IngestionExecution execution)
    {
        ActivityExecutionContext.Current.CancellationToken.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow >= execution.Deadline)
        {
            throw new ApplicationFailureException(
                "The collection discovery deadline has passed.",
                "ImportExpired",
                nonRetryable: true
            );
        }
    }
}
