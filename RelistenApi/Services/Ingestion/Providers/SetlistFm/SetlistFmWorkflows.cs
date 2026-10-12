using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Ingestion.Workflows;
using Relisten.Vendor.SetlistFm;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Providers.SetlistFm;

public sealed record SetlistFmProgress(
    ProviderStage Stage = ProviderStage.NotStarted,
    int ItemIndex = 0,
    SetlistFmPage? Next = null,
    ProviderSummary? Summary = null
);

/// <summary>Reads and applies setlists sequentially, retaining a partly consumed page across continuation.</summary>
[Workflow]
public sealed class SetlistFmWorkflow
{
    [WorkflowRun]
    public async Task<ProviderSummary> RunAsync(
        ProviderRequest<RefreshSetlistFmArtist> input,
        Page<DiscoveredItem<SetlistFmId, Setlist>, SetlistFmPage>? page = null,
        SetlistFmProgress? progress = null
    )
    {
        progress ??= new();
        var summary = progress.Summary ?? new();
        var context = new ProviderWorkflowContext(input.Request.Artist, input.Execution);
        var options = ImportWorkflowOptions.Activity(
            IngestionQueues.Providers[UpstreamProvider.SetlistFm]
        );
        var calls = 0;
        while (progress.Stage != ProviderStage.Complete)
        {
            ImportWorkflowOptions.RequireActive(input.Execution);
            if (ImportWorkflowOptions.ShouldContinue(calls))
            {
                progress = progress with { Summary = summary };
                throw Workflow.CreateContinueAsNewException(
                    (SetlistFmWorkflow w) => w.RunAsync(input, page, progress)
                );
            }
            if (page == null)
            {
                page = await Workflow.ExecuteActivityAsync(
                    (SetlistFmActivities a) => a.ReadSetlistPageAsync(context, progress.Next),
                    options
                );
                calls++;
                progress = progress with
                {
                    Stage = ProviderStage.Importing,
                    ItemIndex = 0,
                    Next = page.Next,
                };
                continue;
            }
            if (progress.ItemIndex < page.Items.Length)
            {
                var item = page.Items[progress.ItemIndex];
                var outcome = await ImportItemOperation.RunAsync(
                    item.Id,
                    () =>
                        Workflow.ExecuteActivityAsync(
                            (SetlistFmActivities a) => a.ImportSetlistAsync(context, item),
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
            page = null;
            progress = progress with
            {
                Stage = progress.Next == null ? ProviderStage.Complete : ProviderStage.Importing,
                ItemIndex = 0,
            };
        }
        return summary;
    }
}
