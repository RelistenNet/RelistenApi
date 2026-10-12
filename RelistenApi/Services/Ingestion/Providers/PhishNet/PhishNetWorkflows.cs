using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Ingestion.Workflows;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Providers.PhishNet;

public sealed record PhishNetProgress(
    ProviderStage Stage = ProviderStage.NotStarted,
    int ItemIndex = 0,
    ProviderSummary? Summary = null
);

/// <summary>Enriches a fixed source selection after Phish.in has completed for this artist.</summary>
[Workflow]
public sealed class PhishNetWorkflow
{
    [WorkflowRun]
    public async Task<ProviderSummary> RunAsync(
        ProviderRequest<RefreshPhishNetArtist> input,
        DiscoveredItem<CatalogSourceId, PhishNetSourceInput>[]? selection = null,
        PhishNetProgress? progress = null
    )
    {
        progress ??= new();
        var summary = progress.Summary ?? new();
        var context = new ProviderWorkflowContext(input.Request.Artist, input.Execution);
        var options = ImportWorkflowOptions.Activity(
            IngestionQueues.Providers[UpstreamProvider.PhishNet]
        );
        var calls = 0;
        ImportWorkflowOptions.RequireActive(input.Execution);
        if (progress.Stage == ProviderStage.NotStarted)
        {
            selection = await Workflow.ExecuteActivityAsync(
                (PhishNetActivities a) => a.SelectSourcesAsync(input),
                options
            );
            calls++;
            progress = progress with { Stage = ProviderStage.Importing };
        }
        while (progress.Stage == ProviderStage.Importing && progress.ItemIndex < selection!.Length)
        {
            ImportWorkflowOptions.RequireActive(input.Execution);
            if (ImportWorkflowOptions.ShouldContinue(calls))
            {
                var continuation = progress with { Summary = summary };
                throw Workflow.CreateContinueAsNewException(
                    (PhishNetWorkflow w) => w.RunAsync(input, selection, continuation)
                );
            }
            var item = selection[progress.ItemIndex];
            var outcome = await ImportItemOperation.RunAsync(
                item.Id,
                () =>
                    Workflow.ExecuteActivityAsync(
                        (PhishNetActivities a) => a.EnrichSourceAsync(context, item),
                        options
                    )
            );
            calls++;
            summary =
                outcome.Failure == null
                    ? summary.Add(outcome.Result!.Disposition)
                    : summary.AddFailure(outcome.Failure);
            progress = progress with { ItemIndex = progress.ItemIndex + 1 };
        }
        return summary;
    }
}
