using System.Threading.Tasks;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Workflows;

/// <summary>Indexes Archive artist collections one upstream page at a time.</summary>
[Workflow]
public class ArchiveArtistDiscoveryWorkflow
{
    [WorkflowRun]
    public async Task<int> RunAsync(
        IngestionRun? run = null,
        string? cursor = null,
        int fetched = 0
    )
    {
        run ??= new IngestionRun(
            Workflow.NewGuid().ToString(),
            Workflow.UtcNow + IngestionRun.MaximumDuration
        );
        for (var pages = 0; ; pages++)
        {
            var page = await Workflow.ExecuteActivityAsync(
                (IngestionBatchActivities a) => a.DiscoverArtists(run, cursor, fetched),
                ImportWorkflowOptions.Activity(IngestionQueues.Providers["archive.org"])
            );
            fetched += page.Count;
            cursor = page.NextCursor;
            if (cursor == null)
            {
                return fetched;
            }

            if (pages >= 100 || Workflow.ContinueAsNewSuggested)
            {
                throw Workflow.CreateContinueAsNewException(
                    (ArchiveArtistDiscoveryWorkflow w) => w.RunAsync(run, cursor, fetched)
                );
            }
        }
    }
}
