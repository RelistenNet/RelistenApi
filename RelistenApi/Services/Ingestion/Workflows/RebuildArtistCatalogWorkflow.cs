using System.Threading.Tasks;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Workflows;

/// <summary>Repairs derived artist data independently of provider availability.</summary>
[Workflow]
public class RebuildArtistCatalogWorkflow
{
    [WorkflowRun]
    public async Task<ArtistImportResult> RunAsync(
        RebuildArtistCatalog request,
        IngestionExecution? execution = null
    )
    {
        execution = ImportWorkflowOptions.AssignExecution(execution);

        await CatalogSteps.RebuildArtist(request.Artist, execution);

        return new ArtistImportResult(request.Artist, 0, 0, 0);
    }
}
