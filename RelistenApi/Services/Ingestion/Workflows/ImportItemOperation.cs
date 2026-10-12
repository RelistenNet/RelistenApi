using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Relisten.Api.Models;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Workflows;

/// <summary>Shared item failure policy for full imports and manual reimports.</summary>
internal static class ImportItemOperation
{
    // Null means an item exhausted retries. Prerequisites and discovery are never
    // caught here: failure there prevents provider finalization altogether.
    internal static async Task<ItemImportResult?> RunAsync(
        ArtistImportRequest request,
        UpstreamProvider provider,
        ImportItem? item
    )
    {
        try
        {
            return await Workflow.ExecuteActivityAsync(
                (IIngestionActivities activities) => activities.ImportItem(request, provider, item),
                ImportWorkflowOptions.Activity(IngestionQueues.Providers[provider])
            );
        }
        catch (ActivityFailureException error) when (!TemporalException.IsCanceledException(error))
        {
            if (error.InnerException is ApplicationFailureException { ErrorType: "ImportExpired" })
            {
                throw;
            }

            Workflow.Logger.LogWarning(
                error,
                "Import failed for {Provider} item {Item}",
                provider.ToValue(),
                item?.Identifier ?? request.Item
            );
            return null;
        }
    }
}
