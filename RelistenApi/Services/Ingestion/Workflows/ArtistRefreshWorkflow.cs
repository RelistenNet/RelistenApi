using System;
using System.Threading.Tasks;
using Relisten.Import;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Workflows;

/// <summary>Refreshes an artist through shared provider activities; raw data never enters workflow history.</summary>
[Workflow]
public class ArtistRefreshWorkflow
{
    [WorkflowRun]
    public Task<ImportResult> RunAsync(ArtistImportRequest request, ImportCheckpoint? checkpoint = null) =>
        ImportComposition.RunAsync(request, checkpoint, false);
}

/// <summary>Reimports one item through the same activities as a full refresh.</summary>
[Workflow]
public class ReimportItemWorkflow
{
    [WorkflowRun]
    public Task<ImportResult> RunAsync(ArtistImportRequest request, ImportCheckpoint? checkpoint = null) =>
        ImportComposition.RunAsync(request, checkpoint, true);
}

// Plain workflow composition keeps two entry points without a child workflow per item.
internal static class ImportComposition
{
    internal static ActivityOptions Options(string queue) => new()
    {
        TaskQueue = queue,
        StartToCloseTimeout = TimeSpan.FromMinutes(5),
        ScheduleToCloseTimeout = TimeSpan.FromHours(6),
        RetryPolicy = new RetryPolicy { MaximumAttempts = 5, InitialInterval = TimeSpan.FromSeconds(2),
            MaximumInterval = TimeSpan.FromMinutes(1) }
    };

    internal static async Task<ImportResult> RunAsync(ArtistImportRequest request,
        ImportCheckpoint? checkpoint, bool singleItem)
    {
        var state = checkpoint ?? new ImportCheckpoint();
        var db = Options(IngestionQueues.Database);
        var plan = await Workflow.ExecuteActivityAsync((IIngestionActivities a) => a.Prepare(request), db);
        var stats = state.Stats ?? new ImportStats();
        var failures = state.FailedItems;
        var calls = 1;
        if (!request.RebuildOnly)
        {
            for (var providerIndex = state.ProviderIndex; providerIndex < plan.Providers.Length; providerIndex++)
            {
                var provider = plan.Providers[providerIndex];
                var http = Options(IngestionQueues.Providers[provider]);
                var cursor = providerIndex == state.ProviderIndex ? state.Cursor : null;
                var itemIndex = providerIndex == state.ProviderIndex ? state.ItemIndex : 0;
                do
                {
                    var page = await Workflow.ExecuteActivityAsync(
                        (IIngestionActivities a) => a.Discover(request, provider, cursor), http);
                    calls++;
                    for (; itemIndex < page.ItemCount; itemIndex++)
                    {
                        var item = new ImportItemRef(request, provider, page.Key, itemIndex);
                        try
                        {
                            for (var part = 0; ; part++)
                            {
                                var done = await Workflow.ExecuteActivityAsync(
                                    (IIngestionActivities a) => a.Fetch(item, part), http);
                                calls++;
                                if (done) break;
                            }
                            stats += await Workflow.ExecuteActivityAsync((IIngestionActivities a) => a.Apply(item), db);
                            calls++;
                        }
                        catch (ActivityFailureException error) when (!TemporalException.IsCanceledException(error))
                        {
                            failures++;
                            await Workflow.ExecuteActivityAsync(
                                (IIngestionActivities a) => a.RecordFailure(item, error.Message), db);
                        }
                        // Finish the item before rollover; a restarted run reads the same staged page.
                        if (calls >= 500 || Workflow.ContinueAsNewSuggested)
                        {
                            var next = new ImportCheckpoint(providerIndex, cursor, itemIndex + 1, failures, stats);
                            if (singleItem)
                                throw Workflow.CreateContinueAsNewException(
                                    (ReimportItemWorkflow w) => w.RunAsync(request, next));
                            throw Workflow.CreateContinueAsNewException(
                                (ArtistRefreshWorkflow w) => w.RunAsync(request, next));
                        }
                    }
                    cursor = page.NextCursor;
                    itemIndex = 0;
                } while (cursor != null);
                var candidates = await Workflow.ExecuteActivityAsync(
                    (IIngestionActivities a) => a.Candidates(request, provider), db);
                foreach (var key in candidates)
                    await Workflow.ExecuteActivityAsync((IIngestionActivities a) => a.Verify(request, provider, key), http);
                stats += await Workflow.ExecuteActivityAsync(
                    (IIngestionActivities a) => a.FinishProvider(request, provider), db);
            }
        }
        var result = new ImportResult(request.RequestId, stats, failures);
        await Workflow.ExecuteActivityAsync((IIngestionActivities a) => a.FinishArtist(request, result), db);
        return result;
    }
}
