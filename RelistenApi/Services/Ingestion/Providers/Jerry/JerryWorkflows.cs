using System.Linq;
using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Import;
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
    // Replay rebuilds these fields within a run. Continuation carries the fixed
    // selection and progress into a fresh run with a new activity count.
    private ProviderRequest<RefreshJerryArtist> _input = null!;
    private ProviderWorkflowContext _context = null!;
    private ActivityOptions _options = null!;
    private JerrySelection _selection = null!;
    private JerryProgress _progress = null!;
    private ProviderSummary _summary = null!;
    private int _activityCalls;

    [WorkflowRun]
    public async Task<ProviderSummary> RunAsync(
        ProviderRequest<RefreshJerryArtist> input,
        JerrySelection? selection = null,
        JerryProgress? progress = null
    )
    {
        _input = input;
        _context = new ProviderWorkflowContext(input.Request.Artist, input.Execution);
        _options = ImportWorkflowOptions.Activity(
            IngestionQueues.Providers[UpstreamProvider.JerryGarcia]
        );
        _selection = selection!;
        _progress = progress ?? new();
        _summary = _progress.Summary ?? new();
        _activityCalls = 0;

        ImportWorkflowOptions.RequireActive(input.Execution);
        await SelectMissingPagesAsync();
        await ImportSelectedPagesAsync();

        return _summary;
    }

    private async Task SelectMissingPagesAsync()
    {
        if (_progress.Stage != ProviderStage.NotStarted)
        {
            return;
        }

        _selection = await Workflow.ExecuteActivityAsync(
            (JerryActivities a) => a.SelectMissingPagesAsync(_context),
            _options
        );

        _activityCalls++;
        _progress = _progress with { Stage = ProviderStage.Importing };
    }

    private async Task ImportSelectedPagesAsync()
    {
        while (
            _progress.Stage == ProviderStage.Importing
            && _progress.ItemIndex < _selection.Items.Length
        )
        {
            ContinueBeforeNextPageIfNeeded();

            var segment = _selection
                .Items.Skip(_progress.ItemIndex)
                .Take(ImportItemOperation.CheckpointBatchSize)
                .ToArray();
            await ImportItemOperation.RunConcurrentlyAsync(
                segment,
                _input.Execution,
                ImportPageAsync
            );

            _progress = _progress with { ItemIndex = _progress.ItemIndex + segment.Length };
        }
    }

    private async Task ImportPageAsync(
        DiscoveredItem<JerryShowId, JerryGarciaComImporter.FileMetaObject> item
    )
    {
        var outcome = await ImportItemOperation.RunAsync(
            item.Id,
            () =>
                Workflow.ExecuteActivityAsync(
                    (JerryActivities a) => a.ImportPageAsync(_context, item),
                    _options
                )
        );

        _activityCalls++;
        _summary =
            outcome.Failure == null
                ? _summary.Add(outcome.Result!.Disposition)
                : _summary.AddFailure(outcome.Failure);
    }

    private void ContinueBeforeNextPageIfNeeded()
    {
        ImportWorkflowOptions.RequireActive(_input.Execution);
        if (!ImportWorkflowOptions.ShouldContinue(_activityCalls))
        {
            return;
        }

        // Retain the original missing-page selection even though earlier items
        // have now been inserted into the catalog.
        _progress = _progress with
        {
            Summary = _summary,
        };
        throw Workflow.CreateContinueAsNewException(
            (JerryWorkflow w) => w.RunAsync(_input, _selection, _progress)
        );
    }
}

/// <summary>Repairs existing venues. Individual failures retain successful repairs and suppress orphan deletion.</summary>
[Workflow]
public sealed class JerryVenueRepairProviderWorkflow
{
    // These fields are run-local replay state. Selection and progress are the
    // explicit checkpoint passed to a new run when its history must be shortened.
    private ProviderRequest<RepairJerryVenues> _input = null!;
    private ProviderWorkflowContext _context = null!;
    private ActivityOptions _options = null!;
    private JerrySelection _selection = null!;
    private JerryProgress _progress = null!;
    private ProviderSummary _summary = null!;
    private int _activityCalls;

    [WorkflowRun]
    public async Task<ProviderSummary> RunAsync(
        ProviderRequest<RepairJerryVenues> input,
        JerrySelection? selection = null,
        JerryProgress? progress = null
    )
    {
        _input = input;
        _context = new ProviderWorkflowContext(input.Request.Artist, input.Execution);
        _options = ImportWorkflowOptions.Activity(
            IngestionQueues.Providers[UpstreamProvider.JerryGarcia]
        );
        _selection = selection!;
        _progress = progress ?? new();
        _summary = _progress.Summary ?? new();
        _activityCalls = 0;

        ImportWorkflowOptions.RequireActive(input.Execution);
        await SelectExistingPagesAsync();
        await RepairSelectedVenuesAsync();
        await FinishVenueRepairAsync();

        return _summary;
    }

    private async Task SelectExistingPagesAsync()
    {
        if (_progress.Stage != ProviderStage.NotStarted)
        {
            return;
        }

        _selection = await Workflow.ExecuteActivityAsync(
            (JerryActivities a) => a.SelectExistingPagesAsync(_context),
            _options
        );

        _activityCalls++;
        _progress = _progress with { Stage = ProviderStage.Importing };
    }

    private async Task RepairSelectedVenuesAsync()
    {
        while (
            _progress.Stage == ProviderStage.Importing
            && _progress.ItemIndex < _selection.Items.Length
        )
        {
            ContinueBeforeNextStepIfNeeded();

            var segment = _selection
                .Items.Skip(_progress.ItemIndex)
                .Take(ImportItemOperation.CheckpointBatchSize)
                .ToArray();
            await ImportItemOperation.RunConcurrentlyAsync(
                segment,
                _input.Execution,
                RepairVenueAsync
            );

            _progress = _progress with { ItemIndex = _progress.ItemIndex + segment.Length };
        }
    }

    private async Task RepairVenueAsync(
        DiscoveredItem<JerryShowId, JerryGarciaComImporter.FileMetaObject> item
    )
    {
        var outcome = await ImportItemOperation.RunAsync(
            item.Id,
            () =>
                Workflow.ExecuteActivityAsync(
                    (JerryActivities a) => a.RepairVenueAsync(_context, item),
                    _options
                )
        );

        _activityCalls++;
        _summary =
            outcome.Failure == null
                ? _summary.Add(outcome.Result!.Disposition)
                : _summary.AddFailure(outcome.Failure);
    }

    private async Task FinishVenueRepairAsync()
    {
        // Record this phase before continuation so a new run goes directly to
        // finalization without selecting or repairing the same venues again.
        _progress = _progress with
        {
            Stage = ProviderStage.Finalizing,
        };
        ContinueBeforeNextStepIfNeeded();

        // A failed or skipped page may still refer to an old venue. Delete orphan
        // venues only when every selected repair completed successfully.
        var canDeleteOrphanVenues = _summary.FailedItems == 0 && _summary.SkippedItems == 0;
        await Workflow.ExecuteActivityAsync(
            (JerryActivities a) => a.FinishVenueRepairAsync(_context, canDeleteOrphanVenues),
            _options
        );
    }

    private void ContinueBeforeNextStepIfNeeded()
    {
        ImportWorkflowOptions.RequireActive(_input.Execution);
        if (!ImportWorkflowOptions.ShouldContinue(_activityCalls))
        {
            return;
        }

        _progress = _progress with { Summary = _summary };
        throw Workflow.CreateContinueAsNewException(
            (JerryVenueRepairProviderWorkflow w) => w.RunAsync(_input, _selection, _progress)
        );
    }
}
