using System;
using System.Linq;
using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Ingestion.Workflows;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Providers.TapersSection;

/// <summary>Owns a cohort artist's item writes and rebuild under the shared artist workflow ID.</summary>
[Workflow]
public sealed class TapersSectionArtistWorkflow
{
    private RefreshTapersSectionArtist _request = null!;
    private ProviderWorkflowContext _context = null!;
    private TapersSectionItemInput[] _items = null!;
    private TapersSectionArtistProgress _progress = null!;
    private TapersSectionSummary _summary = null!;
    private int _activityCalls;

    [WorkflowRun]
    public async Task<TapersSectionArtistResult> RunAsync(
        RefreshTapersSectionArtist request,
        IngestionExecution? execution = null,
        TapersSectionItemInput[]? items = null,
        TapersSectionArtistProgress? progress = null
    )
    {
        _request = request;
        _progress = progress ?? new(ImportWorkflowOptions.AssignExecution(execution));
        _context = new(request.Artist, _progress.Execution);
        _summary = _progress.Summary ?? new();
        ImportWorkflowOptions.RequireActive(_progress.Execution);
        _items = items ?? await SelectItemsAsync();

        try
        {
            await ImportSelectedItemsAsync();
        }
        catch (Exception error)
            when (error is TemporalException
                && !TemporalException.IsCanceledException(error)
                && !ImportWorkflowOptions.ContainsExpiration(error)
            )
        {
            await RebuildAsync(_context);
            throw;
        }

        await RebuildAsync(_context);
        return new(request.Artist, _summary, true);
    }

    private Task<TapersSectionItemInput[]> SelectItemsAsync() =>
        Workflow.ExecuteActivityAsync(
            (TapersSectionActivities a) => a.SelectArtistItems(_context),
            ImportWorkflowOptions.Activity(IngestionQueues.Database)
        );

    private async Task ImportSelectedItemsAsync()
    {
        while (_progress.NextItem < _items.Length)
        {
            ContinueIfNeeded();
            var segment = _items
                .Skip(_progress.NextItem)
                .Take(ImportItemOperation.CheckpointBatchSize)
                .ToArray();
            await ImportItemOperation.RunConcurrentlyAsync(
                segment,
                _progress.Execution,
                ImportItemAsync
            );
            _progress = _progress with { NextItem = _progress.NextItem + segment.Length };
        }
    }

    private async Task ImportItemAsync(TapersSectionItemInput item)
    {
        var result = await ImportItemOperation.RunAsync(
            item.Item,
            () =>
                Workflow.ExecuteActivityAsync(
                    (TapersSectionActivities a) => a.ImportItem(_progress.Execution, item),
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

    private void ContinueIfNeeded()
    {
        if (ImportWorkflowOptions.ShouldContinue(_activityCalls))
        {
            var checkpoint = _progress with { Summary = _summary };
            throw Workflow.CreateContinueAsNewException(
                (TapersSectionArtistWorkflow w) =>
                    w.RunAsync(_request, _progress.Execution, _items, checkpoint)
            );
        }
    }

    private static async Task RebuildAsync(ProviderWorkflowContext context)
    {
        var revisions = await Workflow.ExecuteActivityAsync(
            (TapersSectionActivities a) => a.CaptureDirtyRevisions(context),
            ImportWorkflowOptions.Activity(IngestionQueues.Database)
        );
        await CatalogSteps.RebuildArtist(context.Artist, context.Execution);
        await Workflow.ExecuteActivityAsync(
            (TapersSectionActivities a) => a.CompleteRebuild(context, revisions),
            ImportWorkflowOptions.Activity(IngestionQueues.Database)
        );
    }
}
