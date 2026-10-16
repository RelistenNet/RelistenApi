using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Ingestion.Workflows;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Providers.Archive;

/// <summary>
/// Imports a complete Archive selection, then reconciles deletions for an unfiltered refresh.
/// The artist coordinator rebuilds shows, years, and cache after this workflow completes.
/// </summary>
[Workflow]
public sealed class ArchiveWorkflow
{
    private ProviderRequest<RefreshArchiveArtist> _input = null!;
    private ProviderWorkflowContext _context = null!;
    private ActivityOptions _options = null!;
    private ArchiveSelection _selection = null!;
    private ArchiveProgress _progress = null!;
    private ProviderSummary _summary = null!;
    private List<ArchiveItemId> _noMp3 = null!;
    private int _completedCalls;

    [WorkflowRun]
    public async Task<ProviderSummary> RunAsync(
        ProviderRequest<RefreshArchiveArtist> input,
        ArchiveSelection? selection = null,
        ArchiveProgress? progress = null
    )
    {
        RestoreCheckpoint(input, selection, progress);
        ImportWorkflowOptions.RequireActive(input.Execution);

        await DiscoverRecordingsIfNeededAsync();
        await ImportSelectedRecordingsAsync();
        await ReconcileDeletionsForFullRefreshAsync();

        return _summary;
    }

    private void RestoreCheckpoint(
        ProviderRequest<RefreshArchiveArtist> input,
        ArchiveSelection? selection,
        ArchiveProgress? progress
    )
    {
        // Replay reconstructs these fields. ContinueAsNew starts a fresh instance,
        // so selection and durable progress must also travel in the run arguments.
        _input = input;
        _context = new ProviderWorkflowContext(input.Request.Artist, input.Execution);
        _options = ImportWorkflowOptions.Activity(
            IngestionQueues.Providers[UpstreamProvider.ArchiveOrg]
        );
        _selection = selection!;
        _progress = progress ?? new();
        _summary = _progress.Summary ?? new();
        _noMp3 = (_progress.NoMp3 ?? []).ToList();
        _completedCalls = 0;
    }

    private async Task DiscoverRecordingsIfNeededAsync()
    {
        if (_progress.Stage != ProviderStage.NotStarted)
        {
            return;
        }

        _selection = await Workflow.ExecuteActivityAsync(
            (ArchiveActivities a) => a.DiscoverRecordings(_context, _input.Request.Year),
            _options
        );
        _completedCalls++;

        // Retain the original listing: later deletion checks must use the same
        // observed identifiers even if Archive changes during this refresh.
        _progress = _progress with
        {
            Stage = ProviderStage.Importing,
        };
    }

    private async Task ImportSelectedRecordingsAsync()
    {
        while (
            _progress.Stage == ProviderStage.Importing
            && _progress.NextItem < _selection.Items.Length
        )
        {
            ImportWorkflowOptions.RequireActive(_input.Execution);
            ContinueWithCheckpointIfNeeded();

            var segment = _selection
                .Items.Skip(_progress.NextItem)
                .Take(ImportItemOperation.CheckpointBatchSize)
                .ToArray();
            await ImportItemOperation.RunConcurrentlyAsync(
                segment,
                _input.Execution,
                ImportRecordingAsync
            );

            // Every item in this segment now has an outcome. No active activity
            // or partially completed segment crosses a continuation.
            _progress = _progress with
            {
                NextItem = _progress.NextItem + segment.Length,
            };
        }

        _progress = _progress with { Stage = ProviderStage.Finalizing };
    }

    private async Task ImportRecordingAsync(DiscoveredItem<ArchiveItemId, ArchiveSelectedItem> item)
    {
        var outcome = await ImportItemOperation.RunAsync(
            item.Id,
            () =>
                Workflow.ExecuteActivityAsync(
                    (ArchiveActivities a) =>
                        a.ImportItem(_context, new DiscoveredArchiveItem(item)),
                    _options
                )
        );
        _completedCalls++;

        if (outcome.Failure != null)
        {
            _summary = _summary.AddFailure(outcome.Failure);
        }
        else
        {
            _summary = _summary.Add(outcome.Result!.Disposition);

            // New unplayable items have no stored recording to delete. Only keep
            // existing recordings here; six are enough to trigger the deletion
            // guard without enlarging the continuation payload further.
            if (
                outcome.Result.SkipReason == ArchiveSkipReason.NoVbrMp3
                && outcome.Result.Source != null
                && _noMp3.Count < 6
            )
            {
                _noMp3.Add(item.Id);
            }
        }
    }

    private async Task ReconcileDeletionsForFullRefreshAsync()
    {
        if (_input.Request.Year != null)
        {
            return;
        }

        ImportWorkflowOptions.RequireActive(_input.Execution);
        ContinueWithCheckpointIfNeeded();

        var plan =
            _progress.Deletions
            ?? await Workflow.ExecuteActivityAsync(
                (ArchiveActivities a) =>
                    a.VerifyDeletionCandidates(_context, _selection, _noMp3.ToArray()),
                _options
            );
        _completedCalls++;

        // Keep the verified list across rollover. Applying it must never discover
        // new candidates, including when an activity retries after committing.
        _progress = _progress with
        {
            Deletions = plan,
        };
        ContinueWithCheckpointIfNeeded();

        await Workflow.ExecuteActivityAsync(
            (ArchiveActivities a) => a.ApplyVerifiedDeletions(_context, plan),
            _options
        );
    }

    private void ContinueWithCheckpointIfNeeded()
    {
        if (!ImportWorkflowOptions.ShouldContinue(_completedCalls))
        {
            return;
        }

        var checkpoint = _progress with { Summary = _summary, NoMp3 = _noMp3.ToArray() };
        throw Workflow.CreateContinueAsNewException(
            (ArchiveWorkflow w) => w.RunAsync(_input, _selection, checkpoint)
        );
    }
}
