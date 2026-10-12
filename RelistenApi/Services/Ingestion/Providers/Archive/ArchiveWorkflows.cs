using System.Linq;
using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Ingestion.Workflows;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Providers.Archive;

/// <summary>Retains a complete listing and applies only deletion evidence verified from that listing.</summary>
[Workflow]
public sealed class ArchiveWorkflow
{
    [WorkflowRun]
    public async Task<ProviderSummary> RunAsync(
        ProviderRequest<RefreshArchiveArtist> input,
        ArchiveSelection? selection = null,
        ArchiveProgress? progress = null
    )
    {
        progress ??= new();
        var context = new ProviderWorkflowContext(input.Request.Artist, input.Execution);
        var options = ImportWorkflowOptions.Activity(
            IngestionQueues.Providers[UpstreamProvider.ArchiveOrg]
        );
        var calls = 0;
        ImportWorkflowOptions.RequireActive(input.Execution);
        if (progress.Stage == ProviderStage.NotStarted)
        {
            selection = await Workflow.ExecuteActivityAsync(
                (ArchiveActivities a) => a.DiscoverRecordings(context, input.Request.Year),
                options
            );
            calls++;
            progress = progress with { Stage = ProviderStage.Importing };
        }
        var summary = progress.Summary ?? new ProviderSummary();
        var noMp3 = (progress.NoMp3 ?? []).ToList();
        while (
            progress.Stage == ProviderStage.Importing && progress.NextItem < selection!.Items.Length
        )
        {
            ImportWorkflowOptions.RequireActive(input.Execution);
            if (ImportWorkflowOptions.ShouldContinue(calls))
            {
                var continuation = progress with { Summary = summary, NoMp3 = noMp3.ToArray() };
                throw Workflow.CreateContinueAsNewException(
                    (ArchiveWorkflow w) => w.RunAsync(input, selection, continuation)
                );
            }
            var item = selection.Items[progress.NextItem];
            var outcome = await ImportItemOperation.RunAsync(
                item.Id,
                () =>
                    Workflow.ExecuteActivityAsync(
                        (ArchiveActivities a) =>
                            a.ImportItem(context, new DiscoveredArchiveItem(item)),
                        options
                    )
            );
            calls++;
            if (outcome.Failure != null)
                summary = summary.AddFailure(outcome.Failure);
            else
            {
                summary = summary.Add(outcome.Result!.Disposition);
                if (outcome.Result.SkipReason == ArchiveSkipReason.NoVbrMp3 && noMp3.Count < 6)
                    noMp3.Add(item.Id);
            }
            progress = progress with { NextItem = progress.NextItem + 1 };
        }
        progress = progress with
        {
            Stage = ProviderStage.Finalizing,
            Summary = summary,
            NoMp3 = noMp3.ToArray(),
        };
        if (input.Request.Year == null)
        {
            ImportWorkflowOptions.RequireActive(input.Execution);
            if (ImportWorkflowOptions.ShouldContinue(calls))
                throw Workflow.CreateContinueAsNewException(
                    (ArchiveWorkflow w) => w.RunAsync(input, selection, progress)
                );
            var plan =
                progress.Deletions
                ?? await Workflow.ExecuteActivityAsync(
                    (ArchiveActivities a) =>
                        a.VerifyDeletionCandidates(context, selection!, noMp3.ToArray()),
                    options
                );
            calls++;
            progress = progress with { Deletions = plan };
            if (ImportWorkflowOptions.ShouldContinue(calls))
                throw Workflow.CreateContinueAsNewException(
                    (ArchiveWorkflow w) => w.RunAsync(input, selection, progress)
                );
            await Workflow.ExecuteActivityAsync(
                (ArchiveActivities a) => a.ApplyVerifiedDeletions(context, plan),
                options
            );
        }
        return summary;
    }
}
