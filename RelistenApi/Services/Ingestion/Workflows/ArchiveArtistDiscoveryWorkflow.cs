using System.Threading.Tasks;
using Relisten.Api.Models;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Workflows;

/// <summary>Records each fetched page before applying it, so a write retry cannot refetch a different collection list.</summary>
[Workflow]
public class ArchiveArtistDiscoveryWorkflow
{
    [WorkflowRun]
    public async Task<int> RunAsync(
        DiscoverArchiveArtists request,
        ArchiveDiscoveryProgress? progress = null
    )
    {
        progress ??= new ArchiveDiscoveryProgress(ImportWorkflowOptions.AssignExecution(null));
        for (var calls = 0; ; )
        {
            ImportWorkflowOptions.RequireActive(progress.Execution);
            var page = await Workflow.ExecuteActivityAsync(
                (IngestionBatchActivities a) => a.FetchCollectionPage(progress),
                ImportWorkflowOptions.Activity(
                    IngestionQueues.Providers[UpstreamProvider.ArchiveOrg]
                )
            );
            var count = await Workflow.ExecuteActivityAsync(
                (IngestionBatchActivities a) => a.ApplyCollectionPage(progress.Execution, page),
                ImportWorkflowOptions.Activity(IngestionQueues.Database)
            );
            calls += 2;
            progress = progress with { Cursor = page.Next, Processed = progress.Processed + count };
            if (page.Next == null)
                return progress.Processed;
            if (ImportWorkflowOptions.ShouldContinue(calls))
                throw Workflow.CreateContinueAsNewException(
                    (ArchiveArtistDiscoveryWorkflow w) => w.RunAsync(request, progress)
                );
        }
    }
}
