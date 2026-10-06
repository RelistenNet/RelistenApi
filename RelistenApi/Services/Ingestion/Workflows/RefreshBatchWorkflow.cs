using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Workflows;

/// <summary>Runs the stored artist selection with at most ten active child workflows.</summary>
[Workflow]
public class RefreshBatchWorkflow
{
    [WorkflowRun]
    public async Task<ImportBatchResult> RunAsync(ImportBatchRequest request)
    {
        request = request with { RequestId = request.RequestId ?? Workflow.NewGuid().ToString() };
        var page = await Workflow.ExecuteActivityAsync(
            (IngestionBatchActivities a) => a.ListArtists(request),
            ImportComposition.Options(IngestionQueues.Database)
        );
        var completed = request.Completed;
        var failed = request.Failed;
        var active = new List<Task<bool>>();
        foreach (var artist in page.Artists)
        {
            active.Add(ImportArtist(artist));
            if (active.Count == 10)
                await FinishOne();
        }
        // Drain before rollover, but keep replacing finished artists while there
        // is work. A slow artist must not leave nine provider slots idle.
        while (active.Count > 0)
            await FinishOne();
        if (page.NextOffset.HasValue)
        {
            var next = request with
            {
                Offset = page.NextOffset.Value,
                Completed = completed,
                Failed = failed,
            };
            throw Workflow.CreateContinueAsNewException(
                (RefreshBatchWorkflow w) => w.RunAsync(next)
            );
        }
        return new ImportBatchResult(completed, failed);

        async Task FinishOne()
        {
            var finished = await Workflow.WhenAnyAsync(active);
            active.Remove(finished);
            if (await finished)
                completed++;
            else
                failed++;
        }

        async Task<bool> ImportArtist(string artist)
        {
            var childRequest = new ArtistImportRequest(
                artist,
                request.RequestId + "/" + artist,
                Year: request.Mode == "thin" ? Workflow.UtcNow.Year : null,
                RebuildOnly: request.Mode == "rebuild"
            );
            try
            {
                var result = await Workflow.ExecuteChildWorkflowAsync(
                    (ArtistRefreshWorkflow w) => w.RunAsync(childRequest, null),
                    new ChildWorkflowOptions
                    {
                        Id = "ingest/artist/" + artist,
                        TaskQueue = IngestionQueues.Workflows,
                        ParentClosePolicy = ParentClosePolicy.RequestCancel,
                    }
                );
                return result.FailedItems == 0;
            }
            catch (TemporalException error) when (!TemporalException.IsCanceledException(error))
            {
                // A conflicting manual import or failed artist is visible in the batch result.
                return false;
            }
        }
    }
}

/// <summary>Indexes Archive artist collections one upstream page at a time.</summary>
[Workflow]
public class ArchiveArtistDiscoveryWorkflow
{
    [WorkflowRun]
    public async Task<int> RunAsync(
        string? requestId = null,
        string? cursor = null,
        int fetched = 0
    )
    {
        requestId ??= Workflow.NewGuid().ToString();
        for (var pages = 0; ; pages++)
        {
            var page = await Workflow.ExecuteActivityAsync(
                (IngestionBatchActivities a) => a.DiscoverArtists(requestId, cursor, fetched),
                ImportComposition.Options(IngestionQueues.Providers["archive.org"])
            );
            fetched += page.Count;
            cursor = page.NextCursor;
            if (cursor == null)
                return fetched;
            if (pages >= 100 || Workflow.ContinueAsNewSuggested)
                throw Workflow.CreateContinueAsNewException(
                    (ArchiveArtistDiscoveryWorkflow w) => w.RunAsync(requestId, cursor, fetched)
                );
        }
    }
}
