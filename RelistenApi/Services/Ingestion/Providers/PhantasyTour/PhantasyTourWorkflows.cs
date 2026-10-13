using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Ingestion.Workflows;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Providers.PhantasyTour;

/// <summary>Retains completed-page results and dates needed for final reconciliation.</summary>
public sealed record PhantasyTourProgress(
    PhantasyTourPage NextPage,
    PhantasyTourListedDate[] ListedDates,
    ProviderSummary Summary
);

/// <summary>Imports show pages, then repairs dates only after enumeration finishes.</summary>
[Workflow]
public sealed class PhantasyTourWorkflow
{
    private ProviderRequest<RefreshPhantasyTourArtist> _input = null!;
    private ProviderWorkflowContext _context = null!;
    private ActivityOptions _options = null!;
    private ProviderSummary _summary = null!;
    private Dictionary<PhantasyTourShowId, DateTime> _listedDates = null!;
    private int _activityCalls;

    [WorkflowRun]
    public async Task<ProviderSummary> RunAsync(
        ProviderRequest<RefreshPhantasyTourArtist> input,
        PhantasyTourProgress? progress = null
    )
    {
        _input = input;
        _context = new ProviderWorkflowContext(input.Request.Artist, input.Execution);
        _options = ImportWorkflowOptions.Activity(
            IngestionQueues.Providers[UpstreamProvider.PhantasyTour]
        );
        _summary = progress?.Summary ?? new();
        _listedDates = (progress?.ListedDates ?? []).ToDictionary(
            entry => entry.Id,
            entry => entry.Date
        );

        await ImportShowPagesAsync(progress?.NextPage);
        await RepairListedDatesAsync();

        return _summary;
    }

    private async Task ImportShowPagesAsync(PhantasyTourPage? nextPage)
    {
        do
        {
            ImportWorkflowOptions.RequireActive(_input.Execution);
            var page = await ReadPageAsync(nextPage);
            await ImportPageAsync(page);

            nextPage = page.Next;
            if (nextPage != null && ImportWorkflowOptions.ShouldContinue(_activityCalls))
            {
                var checkpoint = new PhantasyTourProgress(nextPage, DateEntries(), _summary);
                throw Workflow.CreateContinueAsNewException(
                    (PhantasyTourWorkflow workflow) => workflow.RunAsync(_input, checkpoint)
                );
            }
        } while (nextPage != null);
    }

    private async Task<PhantasyTourDiscovery> ReadPageAsync(PhantasyTourPage? cursor)
    {
        var page = await Workflow.ExecuteActivityAsync(
            (PhantasyTourActivities activities) => activities.ReadShowPageAsync(_context, cursor),
            _options
        );
        _activityCalls++;

        // Upstream pages can overlap while shows change. The last observed local
        // date wins; a later sync can correct it without aborting this artist.
        foreach (var entry in page.ListedDates)
        {
            _listedDates[entry.Id] = entry.Date;
        }

        return page;
    }

    private async Task ImportPageAsync(PhantasyTourDiscovery page)
    {
        // Each fetched page is retained in Temporal history until all its items
        // settle. Rollover can exceed the soft activity limit by one 100-show page.
        foreach (var item in page.Items)
        {
            Workflow.CancellationToken.ThrowIfCancellationRequested();
            ImportWorkflowOptions.RequireActive(_input.Execution);

            var outcome = await ImportItemOperation.RunAsync(
                item.Id,
                () =>
                    Workflow.ExecuteActivityAsync(
                        (PhantasyTourActivities activities) =>
                            activities.ImportShowAsync(_context, item),
                        _options
                    )
            );
            _activityCalls++;
            _summary =
                outcome.Failure == null
                    ? _summary.Add(outcome.Result!.Disposition)
                    : _summary.AddFailure(outcome.Failure);
        }
    }

    private Task RepairListedDatesAsync()
    {
        ImportWorkflowOptions.RequireActive(_input.Execution);

        // Only a complete listing repairs dates. Item failures do not invalidate
        // dates supplied independently by the listing itself.
        return Workflow.ExecuteActivityAsync(
            (PhantasyTourActivities activities) =>
                activities.RepairListedDatesAsync(_context, DateEntries()),
            _options
        );
    }

    private PhantasyTourListedDate[] DateEntries() =>
        _listedDates.Select(pair => new PhantasyTourListedDate(pair.Key, pair.Value)).ToArray();
}
