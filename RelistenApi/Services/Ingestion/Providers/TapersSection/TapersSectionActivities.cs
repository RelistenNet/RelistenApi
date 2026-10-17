using System;
using System.Threading.Tasks;
using Relisten.Data;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace Relisten.Ingestion.Providers.TapersSection;

/// <summary>Fetches current Archive evidence before catalog transactions. Each attempt uses fresh metadata.</summary>
public sealed class TapersSectionActivities(
    ArchiveIntakeService intake,
    IngestionHttpClientFactory httpClients
)
{
    [Activity("TapersSection.FetchPage")]
    public async Task<TapersSectionSearchPage> FetchPage(
        IngestionExecution execution,
        string? cursor
    )
    {
        using var heartbeat = new ActivityHeartbeat();
        RequireActive(execution);
        var token = ActivityExecutionContext.Current.CancellationToken;
        using var http = httpClients.Create(token);
        return await new TapersSectionSearchClient(http).FetchPageAsync(cursor, token);
    }

    [Activity("TapersSection.PersistDiscovery")]
    public async Task<ArchiveItemId[]> PersistDiscovery(
        IngestionExecution execution,
        ArchiveItemId[] items
    )
    {
        using var heartbeat = new ActivityHeartbeat();
        RequireActive(execution);
        return await intake.PersistDiscoveryAsync(
            items,
            ActivityExecutionContext.Current.CancellationToken,
            execution.Deadline
        );
    }

    [Activity("TapersSection.SelectPendingObservations")]
    public async Task<ArchiveItemId[]> SelectPendingObservations(IngestionExecution execution)
    {
        using var heartbeat = new ActivityHeartbeat();
        RequireActive(execution);
        return await intake.PendingObservationsAsync(
            ActivityExecutionContext.Current.CancellationToken
        );
    }

    [Activity("TapersSection.ObserveItem")]
    public Task<TapersSectionItemResult> ObserveItem(
        IngestionExecution execution,
        ArchiveItemId item
    ) =>
        RunObservationAttemptAsync(
            execution,
            item,
            (projection, _) =>
                intake.RecordObservationAsync(
                    projection,
                    ActivityExecutionContext.Current.CancellationToken,
                    execution.Deadline
                )
        );

    [Activity("TapersSection.ImportItem")]
    public Task<TapersSectionItemResult> ImportItem(
        IngestionExecution execution,
        TapersSectionItemInput input
    ) =>
        RunObservationAttemptAsync(
            execution,
            input.Item,
            (projection, raw) =>
                intake.ApplyObservationAsync(
                    input,
                    projection,
                    raw,
                    ActivityExecutionContext.Current.CancellationToken,
                    execution.Deadline
                )
        );

    private async Task<TapersSectionItemResult> RunObservationAttemptAsync(
        IngestionExecution execution,
        ArchiveItemId item,
        Func<ArchiveObservationProjection, string, Task<TapersSectionItemResult>> apply
    )
    {
        using var heartbeat = new ActivityHeartbeat();
        RequireActive(execution);
        var token = ActivityExecutionContext.Current.CancellationToken;
        try
        {
            var (projection, raw) = await FetchEvidence(item);
            RequireActive(execution);
            return await apply(projection, raw);
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            await intake.RecordObservationFailureAsync(item, error.Message, token);
            throw;
        }
    }

    [Activity("TapersSection.PrepareCohort")]
    public async Task<ArtistId[]> PrepareCohort(IngestionExecution execution)
    {
        using var heartbeat = new ActivityHeartbeat();
        RequireActive(execution);
        return await intake.PrepareCohortAsync(
            ActivityExecutionContext.Current.CancellationToken,
            execution.Deadline
        );
    }

    [Activity("TapersSection.SelectArtistItems")]
    public async Task<TapersSectionItemInput[]> SelectArtistItems(ProviderWorkflowContext context)
    {
        using var heartbeat = new ActivityHeartbeat();
        RequireActive(context.Execution);
        return await intake.ItemsForArtistAsync(
            context.Artist,
            ActivityExecutionContext.Current.CancellationToken
        );
    }

    [Activity("TapersSection.CaptureDirtyRevisions")]
    public async Task<TapersSectionDirtyRevision[]> CaptureDirtyRevisions(
        ProviderWorkflowContext context
    )
    {
        using var heartbeat = new ActivityHeartbeat();
        RequireActive(context.Execution);
        return await intake.CaptureDirtyRevisionsAsync(
            context.Artist,
            ActivityExecutionContext.Current.CancellationToken
        );
    }

    [Activity("TapersSection.CompleteRebuild")]
    public async Task CompleteRebuild(
        ProviderWorkflowContext context,
        TapersSectionDirtyRevision[] revisions
    )
    {
        using var heartbeat = new ActivityHeartbeat();
        RequireActive(context.Execution);
        await intake.CompleteRebuildAsync(
            context.Artist,
            revisions,
            ActivityExecutionContext.Current.CancellationToken,
            context.Execution.Deadline
        );
    }

    private async Task<(ArchiveObservationProjection Projection, string Raw)> FetchEvidence(
        ArchiveItemId item
    )
    {
        var token = ActivityExecutionContext.Current.CancellationToken;
        using var http = httpClients.Create(token);
        using var response = await http.GetAsync(
            "https://archive.org/metadata/" + Uri.EscapeDataString(item.Value),
            token
        );
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync(token);
        return (ArchiveObservationProjector.Project(item, raw), raw);
    }

    private static void RequireActive(IngestionExecution execution)
    {
        ActivityExecutionContext.Current.CancellationToken.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow >= execution.Deadline)
        {
            throw new ApplicationFailureException(
                "The import execution deadline has passed.",
                "ImportExpired",
                nonRetryable: true
            );
        }
    }
}
