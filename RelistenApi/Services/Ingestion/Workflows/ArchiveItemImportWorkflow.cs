using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Ingestion.Providers.Archive;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Workflows;

/// <summary>Imports one requested Archive recording and rebuilds its artist without discovery or deletion reconciliation.</summary>
[Workflow]
public class ArchiveItemImportWorkflow
{
    [WorkflowRun]
    public async Task<ArtistImportResult> RunAsync(
        ImportArchiveItem request,
        IngestionExecution? execution = null
    )
    {
        execution = ImportWorkflowOptions.AssignExecution(execution);
        ImportWorkflowOptions.RequireActive(execution);

        var context = new ProviderWorkflowContext(request.Artist, execution);
        var (item, failure) = await ImportItemOperation.RunAsync(
            request.Item,
            () =>
                Workflow.ExecuteActivityAsync(
                    (ArchiveActivities a) =>
                        a.ImportItem(context, new RequestedArchiveItem(request.Item)),
                    ImportWorkflowOptions.Activity(
                        IngestionQueues.Providers[UpstreamProvider.ArchiveOrg]
                    )
                )
        );

        var summary =
            failure != null
                ? new ProviderSummary().AddFailure(failure)
                : new ProviderSummary().Add(item!.Disposition);

        await CatalogSteps.RebuildArtist(request.Artist, execution);

        return new ArtistImportResult(
            request.Artist,
            summary.CompletedItems,
            summary.SkippedItems,
            summary.FailedItems
        );
    }
}
