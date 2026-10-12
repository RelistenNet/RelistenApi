using System.Threading.Tasks;
using Relisten.Ingestion.Providers.Jerry;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Workflows;

/// <summary>Runs the explicit Jerry venue repair under artist ownership, then refreshes derived data.</summary>
[Workflow]
public class JerryVenueRepairWorkflow
{
    [WorkflowRun]
    public async Task<ArtistImportResult> RunAsync(
        RepairJerryVenues request,
        IngestionExecution? execution = null
    )
    {
        execution = ImportWorkflowOptions.AssignExecution(execution);

        var providerRequest = new ProviderRequest<RepairJerryVenues>(request, execution);
        var summary = await Workflow.ExecuteChildWorkflowAsync(
            (JerryVenueRepairProviderWorkflow workflow) =>
                workflow.RunAsync(providerRequest, null, null),
            CoordinatorOptions.Child(
                $"{Workflow.Info.WorkflowId}/provider/jerry-venue-repair",
                execution
            )
        );

        await CatalogSteps.RebuildArtist(request.Artist, execution);

        return new ArtistImportResult(
            request.Artist,
            summary.CompletedItems,
            summary.SkippedItems,
            summary.FailedItems
        );
    }
}
