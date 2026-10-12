using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Ingestion.Workflows;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Providers.PhishIn;

/// <summary>Retains an unfinished page and its item index without repeating upstream discovery.</summary>
public sealed record PhishInProgress(
    ProviderStage Stage = ProviderStage.NotStarted,
    Page<DiscoveredItem<PhishInShowId, PhishInShowInput>, PhishInPage>? Page = null,
    int ItemIndex = 0,
    PhishInPage? NextPage = null,
    ProviderSummary? Summary = null
);

/// <summary>Prepare catalogs, then import show pages sequentially. The artist coordinator owns rebuilding.</summary>
[Workflow]
public sealed class PhishInWorkflow
{
    [WorkflowRun]
    public async Task<ProviderSummary> RunAsync(
        ProviderRequest<RefreshPhishInArtist> input,
        PhishInCatalogs? catalogs = null,
        PhishInProgress? progress = null
    )
    {
        progress ??= new();
        var summary = progress.Summary ?? new();
        var context = new ProviderWorkflowContext(input.Request.Artist, input.Execution);
        var options = ImportWorkflowOptions.Activity(
            IngestionQueues.Providers[UpstreamProvider.Phishin]
        );
        var calls = 0;
        ImportWorkflowOptions.RequireActive(input.Execution);
        if (progress.Stage == ProviderStage.NotStarted)
        {
            catalogs = await Workflow.ExecuteActivityAsync(
                (PhishInActivities a) => a.PrepareCatalogsAsync(context),
                options
            );
            calls++;
            progress = progress with { Stage = ProviderStage.Importing };
        }
        while (progress.Stage == ProviderStage.Importing)
        {
            ImportWorkflowOptions.RequireActive(input.Execution);
            if (ImportWorkflowOptions.ShouldContinue(calls))
            {
                var continuation = progress with { Summary = summary };
                throw Workflow.CreateContinueAsNewException(
                    (PhishInWorkflow w) => w.RunAsync(input, catalogs, continuation)
                );
            }
            if (progress.Page == null)
            {
                var page = await Workflow.ExecuteActivityAsync(
                    (PhishInActivities a) => a.ReadShowPageAsync(input, progress.NextPage),
                    options
                );
                calls++;
                progress = progress with { Page = page, ItemIndex = 0 };
                continue;
            }
            if (progress.ItemIndex < progress.Page.Items.Length)
            {
                var item = progress.Page.Items[progress.ItemIndex];
                var outcome = await ImportItemOperation.RunAsync(
                    item.Id,
                    () =>
                        Workflow.ExecuteActivityAsync(
                            (PhishInActivities a) => a.ImportShowAsync(context, catalogs!, item),
                            options
                        )
                );
                calls++;
                summary =
                    outcome.Failure == null
                        ? summary.Add(outcome.Result!.Disposition)
                        : summary.AddFailure(outcome.Failure);
                progress = progress with { ItemIndex = progress.ItemIndex + 1 };
                continue;
            }
            progress = progress with
            {
                Stage =
                    progress.Page.Next == null ? ProviderStage.Complete : ProviderStage.Importing,
                NextPage = progress.Page.Next,
                Page = null,
                ItemIndex = 0,
            };
        }
        return summary;
    }
}
