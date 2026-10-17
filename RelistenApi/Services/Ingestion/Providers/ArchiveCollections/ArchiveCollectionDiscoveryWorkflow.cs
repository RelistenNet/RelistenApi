using System;
using System.Linq;
using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Ingestion.Workflows;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Providers.ArchiveCollections;

/// <summary>
/// Indexes memberships and exact existing sources. Pending page work lives in PostgreSQL;
/// cancellation or lost history can repeat observations without losing unprocessed identifiers.
/// </summary>
[Workflow]
public sealed class ArchiveCollectionDiscoveryWorkflow
{
    private DiscoverArchiveCollection _request = null!;
    private ArchiveCollectionDiscoveryProgress _progress = null!;
    private ArchiveCollectionDiscoveryCheckpoint _checkpoint = null!;
    private ArchiveCollectionDiscoverySummary _summary = null!;
    private int _activityCalls;

    [WorkflowRun]
    public async Task<ArchiveCollectionDiscoverySummary> RunAsync(
        DiscoverArchiveCollection request,
        ArchiveCollectionDiscoveryProgress? progress = null
    )
    {
        if (request.SelectorUuid == Guid.Empty || request.MaxPages is <= 0)
        {
            throw new ApplicationFailureException(
                "A selector UUID and positive page limit are required.",
                "InvalidImportRequest",
                nonRetryable: true
            );
        }
        _request = request;
        _progress = progress ?? new(ImportWorkflowOptions.AssignExecution(null));
        _summary = _progress.Summary ?? new();
        _checkpoint =
            _progress.Checkpoint
            ?? await Workflow.ExecuteActivityAsync(
                (ArchiveCollectionDiscoveryActivities a) =>
                    a.BeginOrResume(_progress.Execution, request.SelectorUuid),
                ImportWorkflowOptions.Activity(IngestionQueues.Database)
            );

        while (true)
        {
            ImportWorkflowOptions.RequireActive(_progress.Execution);
            if (!_checkpoint.Enabled)
            {
                return _summary with { Paused = true };
            }

            await ObservePendingItemsAsync();
            if (!_checkpoint.Enabled)
            {
                return _summary with { Paused = true };
            }
            if (_checkpoint.SearchComplete && _checkpoint.PendingItems.Length == 0)
            {
                _checkpoint = await Workflow.ExecuteActivityAsync(
                    (ArchiveCollectionDiscoveryActivities a) =>
                        a.Complete(_progress.Execution, _checkpoint),
                    ImportWorkflowOptions.Activity(IngestionQueues.Database)
                );
                return _summary with { Completed = _checkpoint.Completed };
            }
            if (request.MaxPages.HasValue && _progress.FetchedPages >= request.MaxPages.Value)
            {
                return _summary with { Limited = true };
            }

            ContinueIfNeeded();
            await FetchAndRetainPageAsync();
        }
    }

    private async Task FetchAndRetainPageAsync()
    {
        var page = await Workflow.ExecuteActivityAsync(
            (ArchiveCollectionDiscoveryActivities a) =>
                a.FetchPage(_progress.Execution, _checkpoint),
            ImportWorkflowOptions.Activity(IngestionQueues.Providers[UpstreamProvider.ArchiveOrg])
        );
        var priorCount = _checkpoint.Discovered;
        _checkpoint = await Workflow.ExecuteActivityAsync(
            (ArchiveCollectionDiscoveryActivities a) =>
                a.ApplyPage(_progress.Execution, _checkpoint, page),
            ImportWorkflowOptions.Activity(IngestionQueues.Database)
        );
        _summary = _summary with
        {
            Discovered = _summary.Discovered + Math.Max(0, _checkpoint.Discovered - priorCount),
        };
        _progress = _progress with { FetchedPages = _progress.FetchedPages + 1 };
        _activityCalls += 2;
    }

    private async Task ObservePendingItemsAsync()
    {
        while (_checkpoint.Enabled && _checkpoint.PendingItems.Length > 0)
        {
            ContinueIfNeeded();
            var segment = _checkpoint
                .PendingItems.Take(ImportItemOperation.CheckpointBatchSize)
                .ToArray();
            await ImportItemOperation.RunConcurrentlyAsync(
                segment,
                _progress.Execution,
                async identifier =>
                {
                    var item = new ArchiveItemId(identifier);
                    var result = await ImportItemOperation.RunAsync(
                        item,
                        () =>
                            Workflow.ExecuteActivityAsync(
                                (ArchiveCollectionDiscoveryActivities a) =>
                                    a.ObserveItem(_progress.Execution, item),
                                ImportWorkflowOptions.Activity(
                                    IngestionQueues.Providers[UpstreamProvider.ArchiveOrg]
                                )
                            )
                    );
                    _summary =
                        result.Failure == null
                            ? _summary.Add(result.Result!)
                            : _summary.AddFailure(result.Failure);
                    _activityCalls++;
                }
            );

            // Only settled attempts are acknowledged. An interrupted segment remains pending,
            // and retries safely reuse retained evidence and the same canonical source reference.
            _checkpoint = await Workflow.ExecuteActivityAsync(
                (ArchiveCollectionDiscoveryActivities a) =>
                    a.AcknowledgeItems(_progress.Execution, _checkpoint, segment),
                ImportWorkflowOptions.Activity(IngestionQueues.Database)
            );
            _activityCalls++;
        }
    }

    private void ContinueIfNeeded()
    {
        if (ImportWorkflowOptions.ShouldContinue(_activityCalls))
        {
            var progress = _progress with { Checkpoint = _checkpoint, Summary = _summary };
            throw Workflow.CreateContinueAsNewException(
                (ArchiveCollectionDiscoveryWorkflow w) => w.RunAsync(_request, progress)
            );
        }
    }
}
