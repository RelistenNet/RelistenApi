using System;
using System.Linq;
using System.Threading.Tasks;
using Relisten.Import;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Workflows;

/// <summary>
/// Prepares an artist, imports each provider, then rebuilds the catalog. Helpers
/// separate provider sequencing, page iteration, and checkpoint advancement.
/// </summary>
[Workflow]
public class ArtistRefreshWorkflow
{
    private ArtistImportRequest request = null!;
    private ImportCheckpoint progress = null!;
    private int activityCount;

    [WorkflowRun]
    public async Task<ImportResult> RunAsync(
        ArtistImportRequest input,
        ImportCheckpoint? checkpoint = null
    )
    {
        request = ImportWorkflowOptions.AssignRun(input);
        progress = checkpoint ?? new ImportCheckpoint(Stats: new ImportStats());
        if (progress.Providers == null)
        {
            var plan = await Workflow.ExecuteActivityAsync(
                (IIngestionActivities activities) => activities.PrepareArtist(request),
                ImportWorkflowOptions.Activity(IngestionQueues.Database)
            );
            progress = progress with { Providers = plan.Providers };
        }

        if (!request.RebuildOnly)
        {
            while (progress.ProviderIndex < progress.Providers.Length)
            {
                await ImportProvider(progress.Providers[progress.ProviderIndex]);
                AdvanceProvider();
                ContinueIfNeeded();
            }
        }

        var result = new ImportResult(request.RequestId, progress.Stats!, progress.FailedItems);
        return await Workflow.ExecuteActivityAsync(
            (IIngestionActivities activities) => activities.FinishArtist(request, result),
            ImportWorkflowOptions.Activity(IngestionQueues.Database)
        );
    }

    private async Task ImportProvider(string provider)
    {
        var options = ImportWorkflowOptions.Activity(IngestionQueues.Providers[provider]);
        if (!progress.ProviderPrepared)
        {
            var stats = await Workflow.ExecuteActivityAsync(
                (IIngestionActivities activities) => activities.PrepareProvider(request, provider),
                options
            );
            progress = progress with { Stats = progress.Stats! + stats, ProviderPrepared = true };
            activityCount++;
        }

        await ImportPages(provider);

        var result = await Workflow.ExecuteActivityAsync(
            (IIngestionActivities activities) =>
                activities.ReconcileProvider(
                    request,
                    provider,
                    progress.IneligibleItems ?? Array.Empty<string>(),
                    progress.ProviderFailures > 0
                ),
            options
        );
        progress = progress with { Stats = progress.Stats! + result.Stats };
        activityCount++;
    }

    private async Task ImportPages(string provider)
    {
        do
        {
            var page = await Workflow.ExecuteActivityAsync(
                (IIngestionActivities activities) =>
                    activities.DiscoverItems(request, provider, progress.Cursor),
                ImportWorkflowOptions.Activity(IngestionQueues.Providers[provider])
            );
            activityCount++;

            await ImportPage(provider, page);
            progress = progress with { Cursor = page.NextCursor, ItemIndex = 0 };

            // After the final page, proceed directly to reconciliation. Rollover
            // here would make a null cursor look like discovery had not started.
            if (progress.Cursor != null)
            {
                ContinueIfNeeded();
            }
        } while (progress.Cursor != null);
    }

    private async Task ImportPage(string provider, ImportPage page)
    {
        while (progress.ItemIndex < page.Items.Length)
        {
            ContinueIfNeeded();
            var item = page.Items[progress.ItemIndex];
            var result = await ImportItemOperation.RunAsync(request, provider, item);
            RecordItemResult(item.Identifier, result);
            activityCount++;
        }
    }

    private void RecordItemResult(string identifier, ItemImportResult? result)
    {
        if (result == null)
        {
            progress = progress with
            {
                ItemIndex = progress.ItemIndex + 1,
                FailedItems = progress.FailedItems + 1,
                ProviderFailures = progress.ProviderFailures + 1,
            };
            return;
        }

        var ineligible = progress.IneligibleItems ?? Array.Empty<string>();
        if (result.Ineligible && ineligible.Length < 6)
        {
            // Six identifiers are enough to trip Archive's five-deletion guard.
            ineligible = ineligible.Append(identifier).ToArray();
        }

        progress = progress with
        {
            ItemIndex = progress.ItemIndex + 1,
            Stats = progress.Stats! + result.Stats,
            IneligibleItems = ineligible,
        };
    }

    private void AdvanceProvider() =>
        progress = progress with
        {
            ProviderIndex = progress.ProviderIndex + 1,
            Cursor = null,
            ItemIndex = 0,
            ProviderPrepared = false,
            ProviderFailures = 0,
            IneligibleItems = null,
        };

    private void ContinueIfNeeded()
    {
        if (activityCount >= 100 || Workflow.ContinueAsNewSuggested)
        {
            throw Workflow.CreateContinueAsNewException(
                (ArtistRefreshWorkflow workflow) => workflow.RunAsync(request, progress)
            );
        }
    }
}
