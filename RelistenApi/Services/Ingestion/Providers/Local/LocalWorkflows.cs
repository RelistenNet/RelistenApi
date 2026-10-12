using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Ingestion.Workflows;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Providers.Local;

/// <summary>Prepares venues from one root response, selects changed dates, then imports each date.</summary>
[Workflow]
public sealed class LocalWorkflow
{
    [WorkflowRun]
    public async Task<ProviderSummary> RunAsync(
        ProviderRequest<RefreshLocalArtist> input,
        LocalSelection? selection = null,
        LocalProgress? progress = null
    )
    {
        progress ??= new();
        var context = new ProviderWorkflowContext(input.Request.Artist, input.Execution);
        var options = ImportWorkflowOptions.Activity(
            IngestionQueues.Providers[UpstreamProvider.Local]
        );
        var calls = 0;
        ImportWorkflowOptions.RequireActive(input.Execution);
        if (progress.Stage == ProviderStage.NotStarted)
        {
            var root = await Workflow.ExecuteActivityAsync(
                (LocalActivities a) => a.FetchRootAndPrepareVenues(context),
                options
            );
            selection = await Workflow.ExecuteActivityAsync(
                (LocalActivities a) => a.SelectChangedDates(context, root),
                options
            );
            calls += 2;
            progress = progress with { Stage = ProviderStage.Importing };
        }
        var summary = progress.Summary ?? new ProviderSummary();
        while (progress.NextItem < selection!.Items.Length)
        {
            ImportWorkflowOptions.RequireActive(input.Execution);
            if (ImportWorkflowOptions.ShouldContinue(calls))
            {
                var continuation = progress with { Summary = summary };
                throw Workflow.CreateContinueAsNewException(
                    (LocalWorkflow w) => w.RunAsync(input, selection, continuation)
                );
            }
            var item = selection.Items[progress.NextItem];
            var outcome = await ImportItemOperation.RunAsync(
                item.Id,
                () =>
                    Workflow.ExecuteActivityAsync(
                        (LocalActivities a) => a.ImportDate(context, item),
                        options
                    )
            );
            calls++;
            summary =
                outcome.Failure == null
                    ? summary.Add(outcome.Result!.Disposition)
                    : summary.AddFailure(outcome.Failure);
            progress = progress with { NextItem = progress.NextItem + 1 };
        }
        return summary;
    }
}
