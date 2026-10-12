using System.Threading.Tasks;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Workflows;

/// <summary>Rebuilds derived catalog data in dependency order, after all item work has settled.</summary>
internal static class CatalogSteps
{
    internal static async Task RebuildArtist(ArtistId artist, IngestionExecution execution)
    {
        var context = new ProviderWorkflowContext(artist, execution);

        ImportWorkflowOptions.RequireActive(execution);
        await Workflow.ExecuteActivityAsync(
            (CatalogActivities a) => a.RebuildShows(context),
            ImportWorkflowOptions.Activity(IngestionQueues.Database)
        );

        ImportWorkflowOptions.RequireActive(execution);
        await Workflow.ExecuteActivityAsync(
            (CatalogActivities a) => a.RebuildYears(context),
            ImportWorkflowOptions.Activity(IngestionQueues.Database)
        );

        // Invalidate cached responses only after both derived tables are rebuilt.
        ImportWorkflowOptions.RequireActive(execution);
        await Workflow.ExecuteActivityAsync(
            (CatalogActivities a) => a.InvalidateArtistCache(context),
            ImportWorkflowOptions.Activity(IngestionQueues.Database)
        );
    }
}
