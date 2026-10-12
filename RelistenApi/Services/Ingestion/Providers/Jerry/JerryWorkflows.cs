using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Ingestion.Workflows;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Providers.Jerry;

public sealed record JerryProgress(
    ProviderStage Stage = ProviderStage.NotStarted,
    int ItemIndex = 0,
    ProviderSummary? Summary = null
);

/// <summary>Imports a fixed selection of missing mirror pages.</summary>
[Workflow]
public sealed class JerryWorkflow
{
    [WorkflowRun]
    public async Task<ProviderSummary> RunAsync(
        ProviderRequest<RefreshJerryArtist> input,
        JerrySelection? selection = null,
        JerryProgress? progress = null
    )
    {
        progress ??= new();
        var summary = progress.Summary ?? new();
        var context = new ProviderWorkflowContext(input.Request.Artist, input.Execution);
        var options = ImportWorkflowOptions.Activity(
            IngestionQueues.Providers[UpstreamProvider.JerryGarcia]
        );
        var calls = 0;
        ImportWorkflowOptions.RequireActive(input.Execution);
        if (progress.Stage == ProviderStage.NotStarted)
        {
            selection = await Workflow.ExecuteActivityAsync(
                (JerryActivities a) => a.SelectMissingPagesAsync(context),
                options
            );
            calls++;
            progress = progress with { Stage = ProviderStage.Importing };
        }
        while (
            progress.Stage == ProviderStage.Importing
            && progress.ItemIndex < selection!.Items.Length
        )
        {
            ImportWorkflowOptions.RequireActive(input.Execution);
            if (ImportWorkflowOptions.ShouldContinue(calls))
            {
                progress = progress with { Summary = summary };
                throw Workflow.CreateContinueAsNewException(
                    (JerryWorkflow w) => w.RunAsync(input, selection, progress)
                );
            }
            var item = selection.Items[progress.ItemIndex];
            var outcome = await ImportItemOperation.RunAsync(
                item.Id,
                () =>
                    Workflow.ExecuteActivityAsync(
                        (JerryActivities a) => a.ImportPageAsync(context, item),
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

/// <summary>Repairs existing venues. Individual failures retain successful repairs and suppress orphan deletion.</summary>
[Workflow]
public sealed class JerryVenueRepairProviderWorkflow
{
    [WorkflowRun]
    public async Task<ProviderSummary> RunAsync(
        ProviderRequest<RepairJerryVenues> input,
        JerrySelection? selection = null,
        JerryProgress? progress = null
    )
    {
        progress ??= new();
        var summary = progress.Summary ?? new();
        var context = new ProviderWorkflowContext(input.Request.Artist, input.Execution);
        var options = ImportWorkflowOptions.Activity(
            IngestionQueues.Providers[UpstreamProvider.JerryGarcia]
        );
        var calls = 0;
        ImportWorkflowOptions.RequireActive(input.Execution);
        if (progress.Stage == ProviderStage.NotStarted)
        {
            selection = await Workflow.ExecuteActivityAsync(
                (JerryActivities a) => a.SelectExistingPagesAsync(context),
                options
            );
            calls++;
            progress = progress with { Stage = ProviderStage.Importing };
        }
        while (
            progress.Stage == ProviderStage.Importing
            && progress.ItemIndex < selection!.Items.Length
        )
        {
            ImportWorkflowOptions.RequireActive(input.Execution);
            if (ImportWorkflowOptions.ShouldContinue(calls))
            {
                progress = progress with { Summary = summary };
                throw Workflow.CreateContinueAsNewException(
                    (JerryVenueRepairProviderWorkflow w) => w.RunAsync(input, selection, progress)
                );
            }
            var item = selection.Items[progress.ItemIndex];
            var outcome = await ImportItemOperation.RunAsync(
                item.Id,
                () =>
                    Workflow.ExecuteActivityAsync(
                        (JerryActivities a) => a.RepairVenueAsync(context, item),
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
        progress = progress with { Stage = ProviderStage.Finalizing };
        ImportWorkflowOptions.RequireActive(input.Execution);
        if (ImportWorkflowOptions.ShouldContinue(calls))
        {
            progress = progress with { Summary = summary };
            throw Workflow.CreateContinueAsNewException(
                (JerryVenueRepairProviderWorkflow w) => w.RunAsync(input, selection, progress)
            );
        }
        await Workflow.ExecuteActivityAsync(
            (JerryActivities a) =>
                a.FinishVenueRepairAsync(
                    context,
                    summary.FailedItems == 0 && summary.SkippedItems == 0
                ),
            options
        );
        return summary;
    }
}
