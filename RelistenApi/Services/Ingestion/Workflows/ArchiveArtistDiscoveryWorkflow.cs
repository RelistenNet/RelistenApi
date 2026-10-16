using System.Threading.Tasks;
using Relisten.Api.Models;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Workflows;

/// <summary>Records each fetched page before applying it, so a write retry cannot refetch a different collection list.</summary>
[Workflow]
public class ArchiveArtistDiscoveryWorkflow
{
    private ArchiveDiscoveryProgress _progress = null!;

    [WorkflowRun]
    public async Task<int> RunAsync(
        DiscoverArchiveArtists request,
        ArchiveDiscoveryProgress? progress = null
    )
    {
        _progress =
            progress ?? new ArchiveDiscoveryProgress(ImportWorkflowOptions.AssignExecution(null));

        await DiscoverUntilCheckpointAsync();

        if (_progress.Cursor != null)
        {
            throw Workflow.CreateContinueAsNewException(
                (ArchiveArtistDiscoveryWorkflow workflow) => workflow.RunAsync(request, _progress)
            );
        }

        return _progress.Processed;
    }

    private async Task DiscoverUntilCheckpointAsync()
    {
        var activityCalls = 0;

        do
        {
            ImportWorkflowOptions.RequireActive(_progress.Execution);
            await FetchAndApplyPageAsync();
            activityCalls += 2;
        } while (_progress.Cursor != null && !ImportWorkflowOptions.ShouldContinue(activityCalls));
    }

    private async Task FetchAndApplyPageAsync()
    {
        // Persist the fetched page in workflow history before writing the catalog.
        // A database retry must apply that same page even if Archive changes meanwhile.
        var page = await Workflow.ExecuteActivityAsync(
            (IngestionBatchActivities activities) => activities.FetchCollectionPage(_progress),
            ImportWorkflowOptions.Activity(IngestionQueues.Providers[UpstreamProvider.ArchiveOrg])
        );

        var count = await Workflow.ExecuteActivityAsync(
            (IngestionBatchActivities activities) =>
                activities.ApplyCollectionPage(_progress.Execution, page),
            ImportWorkflowOptions.Activity(IngestionQueues.Database)
        );

        _progress = _progress with { Cursor = page.Next, Processed = _progress.Processed + count };
    }
}
