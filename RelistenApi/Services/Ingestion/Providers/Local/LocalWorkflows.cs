using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Ingestion.Workflows;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Providers.Local;

/// <summary>Prepares venues from one root response, selects changed dates, then imports each date.</summary>
[Workflow]
public sealed class LocalWorkflow
{
    // Replay restores these fields within a run. Continue-as-new carries only the explicit
    // selection and progress arguments into a new run with a fresh activity count.
    private ProviderRequest<RefreshLocalArtist> _input = null!;
    private ProviderWorkflowContext _context = null!;
    private ActivityOptions _options = null!;
    private LocalSelection _selection = null!;
    private LocalProgress _progress = null!;
    private ProviderSummary _summary = null!;
    private int _activityCalls;

    [WorkflowRun]
    public async Task<ProviderSummary> RunAsync(
        ProviderRequest<RefreshLocalArtist> input,
        LocalSelection? selection = null,
        LocalProgress? progress = null
    )
    {
        _input = input;
        _context = new ProviderWorkflowContext(input.Request.Artist, input.Execution);
        _options = ImportWorkflowOptions.Activity(
            IngestionQueues.Providers[UpstreamProvider.Local]
        );
        _selection = selection!;
        _progress = progress ?? new();
        _summary = _progress.Summary ?? new();
        _activityCalls = 0;

        ImportWorkflowOptions.RequireActive(input.Execution);
        await PrepareSelectionAsync();
        await ImportSelectedDatesAsync();

        return _summary;
    }

    private async Task PrepareSelectionAsync()
    {
        if (_progress.Stage != ProviderStage.NotStarted)
        {
            return;
        }

        // Venue preparation and date selection use the same root response so the
        // selected shows refer to the venues established for this refresh.
        var root = await Workflow.ExecuteActivityAsync(
            (LocalActivities a) => a.FetchRootAndPrepareVenues(_context),
            _options
        );
        _selection = await Workflow.ExecuteActivityAsync(
            (LocalActivities a) => a.SelectChangedDates(_context, root),
            _options
        );

        _activityCalls += 2;
        _progress = _progress with { Stage = ProviderStage.Importing };
    }

    private async Task ImportSelectedDatesAsync()
    {
        while (_progress.NextItem < _selection.Items.Length)
        {
            ContinueBeforeNextDateIfNeeded();

            var item = _selection.Items[_progress.NextItem];
            var outcome = await ImportItemOperation.RunAsync(
                item.Id,
                () =>
                    Workflow.ExecuteActivityAsync(
                        (LocalActivities a) => a.ImportDate(_context, item),
                        _options
                    )
            );

            _activityCalls++;
            _summary =
                outcome.Failure == null
                    ? _summary.Add(outcome.Result!.Disposition)
                    : _summary.AddFailure(outcome.Failure);
            _progress = _progress with { NextItem = _progress.NextItem + 1 };
        }
    }

    private void ContinueBeforeNextDateIfNeeded()
    {
        ImportWorkflowOptions.RequireActive(_input.Execution);
        if (!ImportWorkflowOptions.ShouldContinue(_activityCalls))
        {
            return;
        }

        // Advance only after a date has an outcome. A new run resumes the fixed
        // selection instead of fetching a changed root and choosing different work.
        var continuation = _progress with
        {
            Summary = _summary,
        };
        throw Workflow.CreateContinueAsNewException(
            (LocalWorkflow w) => w.RunAsync(_input, _selection, continuation)
        );
    }
}
