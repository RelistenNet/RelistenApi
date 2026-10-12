using System.Collections.Generic;
using System.Threading.Tasks;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Workflows;

/// <summary>Imports a stable artist selection, replacing finished children until the page is complete.</summary>
[Workflow]
public class RefreshBatchWorkflow
{
    [WorkflowRun]
    public async Task<ImportBatchResult> RunAsync(ImportBatchRequest request)
    {
        var requestId = request.RequestId ?? Workflow.NewGuid().ToString();
        request = request with
        {
            RequestId = requestId,
            Run =
                request.Run
                ?? new IngestionRun(requestId, Workflow.UtcNow + IngestionRun.MaximumDuration),
        };
        var page = await Workflow.ExecuteActivityAsync(
            (IngestionBatchActivities activities) => activities.ListArtists(request),
            ImportWorkflowOptions.Activity(IngestionQueues.Database)
        );
        var result = await ImportArtists(request, page.Artists);

        if (page.NextOffset.HasValue)
        {
            var next = request with
            {
                Offset = page.NextOffset.Value,
                Completed = result.Completed,
                Failed = result.Failed,
            };
            throw Workflow.CreateContinueAsNewException(
                (RefreshBatchWorkflow workflow) => workflow.RunAsync(next)
            );
        }

        return result;
    }

    private static async Task<ImportBatchResult> ImportArtists(
        ImportBatchRequest request,
        string[] artists
    )
    {
        var result = new ImportBatchResult(request.Completed, request.Failed);
        var active = new List<Task<bool>>();
        foreach (var artist in artists)
        {
            active.Add(ImportArtist(request, artist));
            if (active.Count == 10)
            {
                result = await FinishOne(active, result);
            }
        }

        // Rollover must not strand active children. During admission, a slow artist
        // does not block replacing any of the other nine completed children.
        while (active.Count > 0)
        {
            result = await FinishOne(active, result);
        }

        return result;
    }

    private static async Task<ImportBatchResult> FinishOne(
        List<Task<bool>> active,
        ImportBatchResult result
    )
    {
        var finished = await Workflow.WhenAnyAsync(active);
        active.Remove(finished);
        return await finished
            ? result with
            {
                Completed = result.Completed + 1,
            }
            : result with
            {
                Failed = result.Failed + 1,
            };
    }

    private static async Task<bool> ImportArtist(ImportBatchRequest batch, string artist)
    {
        var requestId = batch.RequestId + "/" + artist;
        var request = new ArtistImportRequest(
            artist,
            requestId,
            Year: batch.Mode == "thin" ? Workflow.UtcNow.Year : null,
            RebuildOnly: batch.Mode == "rebuild",
            Run: new IngestionRun(requestId, batch.Run!.Deadline)
        );
        var options = new ChildWorkflowOptions
        {
            Id = "ingest/artist/" + artist,
            TaskQueue = IngestionQueues.Workflows,
            ParentClosePolicy = ParentClosePolicy.RequestCancel,
            ExecutionTimeout = IngestionRun.MaximumDuration,
        };

        try
        {
            var result = await Workflow.ExecuteChildWorkflowAsync(
                (ArtistRefreshWorkflow workflow) => workflow.RunAsync(request, null),
                options
            );
            return result.FailedItems == 0;
        }
        catch (TemporalException error) when (!TemporalException.IsCanceledException(error))
        {
            // A conflicting manual import or failed artist remains visible in the batch result.
            return false;
        }
    }
}
