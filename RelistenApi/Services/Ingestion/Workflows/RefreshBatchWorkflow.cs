using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Workflows;

/// <summary>Retains one artist selection and replaces completed children up to a limit of ten active artists.</summary>
[Workflow]
public class RefreshBatchWorkflow
{
    private const int ConcurrentArtists = 10;
    private const int ArtistsPerRun = 250;

    // Temporal recreates these fields by replaying RunAsync. Only the selection and
    // completed progress cross a continuation; no active child is carried forward.
    private CatalogBatchCommand _request = null!;
    private ArtistId[] _selectedArtists = null!;
    private BatchProgress _progress = null!;
    private ImportBatchResult _result = null!;
    private readonly List<Task<ArtistOutcome>> _active = new();

    [WorkflowRun]
    public async Task<ImportBatchResult> RunAsync(
        CatalogBatchCommand request,
        ArtistId[]? selectedArtists = null,
        BatchProgress? progress = null
    )
    {
        _request = request;
        _progress =
            progress
            ?? new BatchProgress(ImportWorkflowOptions.AssignExecution(null), Workflow.UtcNow.Year);
        _result = _progress.Result ?? new ImportBatchResult();

        ImportWorkflowOptions.RequireActive(_progress.Execution);
        _selectedArtists = selectedArtists ?? await SelectArtistsAsync();

        await ImportCurrentSegmentAsync();

        if (_progress.NextArtist < _selectedArtists.Length)
        {
            ContinueWithRemainingArtists();
        }

        return _result;
    }

    private Task<ArtistId[]> SelectArtistsAsync() =>
        Workflow.ExecuteActivityAsync(
            (IngestionBatchActivities activities) =>
                activities.SelectArtists(_request, _progress.Execution),
            ImportWorkflowOptions.Activity(IngestionQueues.Database)
        );

    private async Task ImportCurrentSegmentAsync()
    {
        var end = Math.Min(_progress.NextArtist + ArtistsPerRun, _selectedArtists.Length);

        try
        {
            while (_progress.NextArtist < end)
            {
                Workflow.CancellationToken.ThrowIfCancellationRequested();
                ImportWorkflowOptions.RequireActive(_progress.Execution);

                _active.Add(ImportArtistAsync(_selectedArtists[_progress.NextArtist]));
                _progress = _progress with { NextArtist = _progress.NextArtist + 1 };

                if (_active.Count == ConcurrentArtists)
                {
                    await FinishOneArtistAsync();
                }

                if (Workflow.ContinueAsNewSuggested)
                {
                    break;
                }
            }

            // Every started artist must settle before its result enters the next run.
            while (_active.Count > 0)
            {
                await FinishOneArtistAsync();
            }
        }
        catch (Exception error) when (TemporalException.IsCanceledException(error))
        {
            await WaitForCanceledArtistsAsync();
            throw;
        }
    }

    private async Task WaitForCanceledArtistsAsync()
    {
        // One child's cancellation can complete before the other nine. Keep the
        // parent open until every started child has acknowledged cancellation.
        try
        {
            await Workflow.WhenAllAsync(_active);
        }
        catch (Exception)
        {
            // WhenAll waits for every task even when one fails. The caller then
            // rethrows the original cancellation, rather than a sibling's failure.
        }
    }

    private async Task FinishOneArtistAsync()
    {
        var finished = await Workflow.WhenAnyAsync(_active);
        _active.Remove(finished);

        _result = await finished switch
        {
            ArtistOutcome.Completed => _result with
            {
                CompletedArtists = _result.CompletedArtists + 1,
            },
            ArtistOutcome.Partial => _result with { PartialArtists = _result.PartialArtists + 1 },
            ArtistOutcome.Conflict => _result with
            {
                ConflictingArtists = _result.ConflictingArtists + 1,
            },
            _ => _result with { FailedArtists = _result.FailedArtists + 1 },
        };
    }

    private void ContinueWithRemainingArtists()
    {
        _progress = _progress with { Result = _result };

        throw Workflow.CreateContinueAsNewException(
            (RefreshBatchWorkflow workflow) =>
                workflow.RunAsync(_request, _selectedArtists, _progress)
        );
    }

    private async Task<ArtistOutcome> ImportArtistAsync(ArtistId artist)
    {
        var options = CoordinatorOptions.Child($"ingest/artist/{artist}", _progress.Execution);

        try
        {
            ArtistImportResult result;

            if (_request is RebuildCatalog)
            {
                result = await Workflow.ExecuteChildWorkflowAsync(
                    (RebuildArtistCatalogWorkflow workflow) =>
                        workflow.RunAsync(new RebuildArtistCatalog(artist), _progress.Execution),
                    options
                );
            }
            else
            {
                ArtistRefreshCommand command =
                    _request is RefreshRecentlyActiveArtists
                        ? new RefreshFilteredArtist(artist, _progress.FilterYear)
                        : new RefreshArtist(artist);

                result = await Workflow.ExecuteChildWorkflowAsync(
                    (ArtistRefreshWorkflow workflow) =>
                        workflow.RunAsync(command, _progress.Execution),
                    options
                );
            }

            return result.Status == ImportStatus.Completed
                ? ArtistOutcome.Completed
                : ArtistOutcome.Partial;
        }
        catch (WorkflowAlreadyStartedException)
        {
            return ArtistOutcome.Conflict;
        }
        catch (TemporalException error)
            when (!TemporalException.IsCanceledException(error)
                && !ImportWorkflowOptions.ContainsExpiration(error)
            )
        {
            return ContainsConflict(error) ? ArtistOutcome.Conflict : ArtistOutcome.Failed;
        }
    }

    private static bool ContainsConflict(Exception error) =>
        error is WorkflowAlreadyStartedException
        || error.InnerException != null && ContainsConflict(error.InnerException);

    private enum ArtistOutcome
    {
        Completed,
        Partial,
        Failed,
        Conflict,
    }
}
