using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Ingestion.Workflows;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Providers.TapersSection;

/// <summary>Persists each search page before observing its items or advancing the cursor.</summary>
[Workflow]
public sealed class TapersSectionDiscoveryWorkflow
{
    private DiscoverTapersSection _request = null!;
    private TapersSectionDiscoveryProgress _progress = null!;
    private TapersSectionSummary _summary = null!;
    private int _activityCalls;

    [WorkflowRun]
    public async Task<TapersSectionSummary> RunAsync(
        DiscoverTapersSection request,
        TapersSectionDiscoveryProgress? progress = null
    )
    {
        _request = request;
        _progress = progress ?? new(ImportWorkflowOptions.AssignExecution(null));
        _summary = _progress.Summary ?? new();

        await SelectRetainedRecoveryWorkAsync();

        do
        {
            ImportWorkflowOptions.RequireActive(_progress.Execution);
            await ObservePersistedItemsAsync();
            if (_progress.SearchComplete)
            {
                return _summary;
            }

            ContinueIfNeeded();
            await FetchAndPersistPageAsync();
        } while (true);
    }

    private async Task SelectRetainedRecoveryWorkAsync()
    {
        if (_progress.RecoverySelected)
        {
            return;
        }

        // A full traversal also retries retained failures whose items left the collection.
        // Pilot limits apply only to the requested search selection.
        var pending = _request.MaxItems.HasValue
            ? []
            : await Workflow.ExecuteActivityAsync(
                (TapersSectionActivities a) => a.SelectPendingObservations(_progress.Execution),
                ImportWorkflowOptions.Activity(IngestionQueues.Database)
            );
        _progress = _progress with { RecoverySelected = true, PendingObservations = pending };
    }

    private async Task FetchAndPersistPageAsync()
    {
        var page = await Workflow.ExecuteActivityAsync(
            (TapersSectionActivities a) => a.FetchPage(_progress.Execution, _progress.Cursor),
            ImportWorkflowOptions.Activity(IngestionQueues.Providers[UpstreamProvider.ArchiveOrg])
        );
        var seen = new HashSet<string>(_progress.SeenItems ?? [], StringComparer.Ordinal);
        var cursors = new HashSet<string>(_progress.SeenCursors ?? [], StringComparer.Ordinal);
        if (page.Cursor != null && !cursors.Add(page.Cursor))
        {
            throw new ApplicationFailureException(
                "The search cursor repeated an earlier page.",
                "NonAdvancingSearch",
                nonRetryable: true
            );
        }
        var fresh = page.Items.Where(item => seen.Add(item.Value)).ToArray();
        if (page.Items.Length > 0 && fresh.Length == 0)
        {
            throw new ApplicationFailureException(
                "The search page contains no new identifiers.",
                "NonAdvancingSearch",
                nonRetryable: true
            );
        }

        var remaining = _request.MaxItems.HasValue
            ? _request.MaxItems.Value - _summary.Discovered
            : int.MaxValue;
        var selected = fresh.Take(remaining).ToArray();
        var pending = await Workflow.ExecuteActivityAsync(
            (TapersSectionActivities a) => a.PersistDiscovery(_progress.Execution, selected),
            ImportWorkflowOptions.Activity(IngestionQueues.Database)
        );
        _activityCalls += 2;
        var limited =
            _request.MaxItems.HasValue
            && selected.Length == remaining
            && (page.Cursor != null || selected.Length < fresh.Length);
        _summary = _summary with
        {
            Discovered = _summary.Discovered + selected.Length,
            Limited = limited,
        };
        _progress = _progress with
        {
            SeenItems = seen.ToArray(),
            SeenCursors = cursors.ToArray(),
            Cursor = page.Cursor,
            PendingObservations = pending,
            NextObservation = 0,
            SearchComplete = limited || page.Cursor == null,
        };
    }

    private async Task ObservePersistedItemsAsync()
    {
        var pending = _progress.PendingObservations ?? [];
        while (_progress.NextObservation < pending.Length)
        {
            ContinueIfNeeded();
            var segment = pending
                .Skip(_progress.NextObservation)
                .Take(ImportItemOperation.CheckpointBatchSize)
                .ToArray();
            await ImportItemOperation.RunConcurrentlyAsync(
                segment,
                _progress.Execution,
                async item =>
                {
                    var result = await ImportItemOperation.RunAsync(
                        item,
                        () =>
                            Workflow.ExecuteActivityAsync(
                                (TapersSectionActivities a) =>
                                    a.ObserveItem(_progress.Execution, item),
                                ImportWorkflowOptions.Activity(
                                    IngestionQueues.Providers[UpstreamProvider.ArchiveOrg]
                                )
                            )
                    );
                    if (result.Failure == null)
                    {
                        _summary = _summary.Add(result.Result!);
                    }
                    else
                    {
                        _summary = _summary.AddFailure(result.Failure);
                    }
                    _activityCalls++;
                }
            );
            _progress = _progress with
            {
                NextObservation = _progress.NextObservation + segment.Length,
            };
        }
    }

    private void ContinueIfNeeded()
    {
        if (ImportWorkflowOptions.ShouldContinue(_activityCalls))
        {
            var progress = _progress with { Summary = _summary };
            throw Workflow.CreateContinueAsNewException(
                (TapersSectionDiscoveryWorkflow w) => w.RunAsync(_request, progress)
            );
        }
    }
}
