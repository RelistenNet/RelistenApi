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
    // Replay restores these fields within a run. Continuation passes the current
    // page, its next item, and all listed dates to the next run explicitly.
    private ProviderRequest<RefreshPhantasyTourArtist> _input = null!;
    private ProviderWorkflowContext _context = null!;
    private ActivityOptions _options = null!;
    private PhantasyTourDiscovery? _page;
    private PhantasyTourProgress _progress = null!;
    private ProviderSummary _summary = null!;
    private Dictionary<PhantasyTourShowId, DateTime> _listedDates = null!;
    private int _activityCalls;

    [WorkflowRun]
    public async Task<ProviderSummary> RunAsync(
        ProviderRequest<RefreshPhantasyTourArtist> input,
        PhantasyTourDiscovery? page = null,
        PhantasyTourProgress? progress = null
    )
    {
        _input = input;
        _context = new ProviderWorkflowContext(input.Request.Artist, input.Execution);
        _options = ImportWorkflowOptions.Activity(
            IngestionQueues.Providers[UpstreamProvider.PhantasyTour]
        );
        _page = page;
        _progress = progress ?? new();
        _summary = _progress.Summary ?? new();
        _listedDates = (_progress.ListedDates ?? []).ToDictionary(
            entry => entry.Id,
            entry => entry.Date
        );
        _activityCalls = 0;

        await ImportShowPagesAsync();
        await RepairListedDatesAsync();

        return _summary;
    }

    private async Task ImportShowPagesAsync()
    {
        while (
            _progress.Stage != ProviderStage.Finalizing && _progress.Stage != ProviderStage.Complete
        )
        {
            ContinueBeforeNextStepIfNeeded();

            if (_page == null)
            {
                await ReadNextPageAsync();
                continue;
            }

            if (_progress.ItemIndex < _page.Items.Length)
            {
                await ImportNextShowAsync();
                continue;
            }

            _page = null;
            _progress = _progress with
            {
                Stage = _progress.Next == null ? ProviderStage.Finalizing : ProviderStage.Importing,
                ItemIndex = 0,
            };
        }
    }

    private async Task ReadNextPageAsync()
    {
        _page = await Workflow.ExecuteActivityAsync(
            (PhantasyTourActivities a) => a.ReadShowPageAsync(_context, _progress.Next),
            _options
        );
        _activityCalls++;

        // Listing dates also correct existing shows, including shows that did not
        // need an item import. Conflicting dates cannot produce a safe repair set.
        foreach (var entry in _page.ListedDates)
        {
            if (_listedDates.TryGetValue(entry.Id, out var prior) && prior != entry.Date)
            {
                throw new ApplicationFailureException(
                    $"Phantasy Tour returned conflicting dates for {entry.Id.Value}.",
                    "ConflictingListedDates",
                    nonRetryable: true
                );
            }

            _listedDates[entry.Id] = entry.Date;
        }

        _progress = _progress with
        {
            Stage = ProviderStage.Importing,
            ItemIndex = 0,
            Next = _page.Next,
        };
    }

    private async Task ImportNextShowAsync()
    {
        var item = _page!.Items[_progress.ItemIndex];
        var outcome = await ImportItemOperation.RunAsync(
            item.Id,
            () =>
                Workflow.ExecuteActivityAsync(
                    (PhantasyTourActivities a) => a.ImportShowAsync(_context, item),
                    _options
                )
        );

        _activityCalls++;
        _summary =
            outcome.Failure == null
                ? _summary.Add(outcome.Result!.Disposition)
                : _summary.AddFailure(outcome.Failure);
        _progress = _progress with { ItemIndex = _progress.ItemIndex + 1 };
    }

    private async Task RepairListedDatesAsync()
    {
        if (_progress.Stage == ProviderStage.Complete)
        {
            return;
        }

        ContinueBeforeNextStepIfNeeded();

        // Apply the complete listing only after enumeration ends. Item failures
        // do not invalidate dates supplied independently by the listing itself.
        await Workflow.ExecuteActivityAsync(
            (PhantasyTourActivities a) =>
                a.RepairListedDatesAsync(_context, DateEntries(_listedDates)),
            _options
        );
        _progress = _progress with { Stage = ProviderStage.Complete };
    }

    private void ContinueBeforeNextStepIfNeeded()
    {
        ImportWorkflowOptions.RequireActive(_input.Execution);
        if (!ImportWorkflowOptions.ShouldContinue(_activityCalls))
        {
            return;
        }

        _progress = _progress with { Summary = _summary, ListedDates = DateEntries(_listedDates) };
        throw Workflow.CreateContinueAsNewException(
            (PhantasyTourWorkflow w) => w.RunAsync(_input, _page, _progress)
        );
    }

    private static PhantasyTourListedDate[] DateEntries(
        Dictionary<PhantasyTourShowId, DateTime> dates
    ) => dates.Select(pair => new PhantasyTourListedDate(pair.Key, pair.Value)).ToArray();
}
