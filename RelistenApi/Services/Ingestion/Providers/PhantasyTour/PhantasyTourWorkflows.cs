using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Ingestion.Workflows;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Providers.PhantasyTour;

public sealed record PhantasyTourProgress(
    ProviderStage Stage = ProviderStage.NotStarted,
    int ItemIndex = 0,
    PhantasyTourPage? Next = null,
    PhantasyTourListedDate[]? ListedDates = null,
    ProviderSummary? Summary = null
);

/// <summary>Retains every listed date, then repairs dates only after enumeration finishes.</summary>
[Workflow]
public sealed class PhantasyTourWorkflow
{
    [WorkflowRun]
    public async Task<ProviderSummary> RunAsync(
        ProviderRequest<RefreshPhantasyTourArtist> input,
        PhantasyTourDiscovery? page = null,
        PhantasyTourProgress? progress = null
    )
    {
        progress ??= new();
        var summary = progress.Summary ?? new();
        var dates = (progress.ListedDates ?? []).ToDictionary(
            entry => entry.Id,
            entry => entry.Date
        );
        var context = new ProviderWorkflowContext(input.Request.Artist, input.Execution);
        var options = ImportWorkflowOptions.Activity(
            IngestionQueues.Providers[UpstreamProvider.PhantasyTour]
        );
        var calls = 0;
        while (progress.Stage != ProviderStage.Complete)
        {
            ImportWorkflowOptions.RequireActive(input.Execution);
            if (ImportWorkflowOptions.ShouldContinue(calls))
            {
                progress = progress with { Summary = summary, ListedDates = DateEntries(dates) };
                throw Workflow.CreateContinueAsNewException(
                    (PhantasyTourWorkflow w) => w.RunAsync(input, page, progress)
                );
            }
            if (progress.Stage == ProviderStage.Finalizing)
            {
                await Workflow.ExecuteActivityAsync(
                    (PhantasyTourActivities a) =>
                        a.RepairListedDatesAsync(context, DateEntries(dates)),
                    options
                );
                progress = progress with { Stage = ProviderStage.Complete };
                continue;
            }
            if (page == null)
            {
                page = await Workflow.ExecuteActivityAsync(
                    (PhantasyTourActivities a) => a.ReadShowPageAsync(context, progress.Next),
                    options
                );
                calls++;
                foreach (var entry in page.ListedDates)
                {
                    if (dates.TryGetValue(entry.Id, out var prior) && prior != entry.Date)
                        throw new ApplicationFailureException(
                            $"Phantasy Tour returned conflicting dates for {entry.Id.Value}.",
                            "ConflictingListedDates",
                            nonRetryable: true
                        );
                    dates[entry.Id] = entry.Date;
                }
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
                            (PhantasyTourActivities a) => a.ImportShowAsync(context, item),
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
                Stage = progress.Next == null ? ProviderStage.Finalizing : ProviderStage.Importing,
                ItemIndex = 0,
            };
        }
        return summary;
    }

    private static PhantasyTourListedDate[] DateEntries(
        Dictionary<PhantasyTourShowId, DateTime> dates
    ) => dates.Select(pair => new PhantasyTourListedDate(pair.Key, pair.Value)).ToArray();
}
