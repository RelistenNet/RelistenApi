using System.Threading.Tasks;
using Relisten.Import;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Workflows;

/// <summary>Imports the requested item through the same provider activity, without artist-wide discovery or deletion.</summary>
[Workflow]
public class ReimportItemWorkflow
{
    [WorkflowRun]
    public async Task<ImportResult> RunAsync(ArtistImportRequest input)
    {
        var request = ImportWorkflowOptions.AssignRun(input);
        var plan = await Workflow.ExecuteActivityAsync(
            (IIngestionActivities activities) => activities.PrepareArtist(request),
            ImportWorkflowOptions.Activity(IngestionQueues.Database)
        );
        var stats = new ImportStats();
        var failures = 0;

        foreach (var provider in plan.Providers)
        {
            stats += await Workflow.ExecuteActivityAsync(
                (IIngestionActivities activities) => activities.PrepareProvider(request, provider),
                ImportWorkflowOptions.Activity(IngestionQueues.Providers[provider])
            );
            var result = await ImportItemOperation.RunAsync(request, provider, null);
            if (result == null)
            {
                failures++;
                continue;
            }

            stats += result.Stats;
        }

        var summary = new ImportResult(request.RequestId, stats, failures);
        return await Workflow.ExecuteActivityAsync(
            (IIngestionActivities activities) => activities.FinishArtist(request, summary),
            ImportWorkflowOptions.Activity(IngestionQueues.Database)
        );
    }
}
