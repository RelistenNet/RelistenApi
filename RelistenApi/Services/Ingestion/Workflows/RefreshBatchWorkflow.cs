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
    [WorkflowRun]
    public async Task<ImportBatchResult> RunAsync(
        CatalogBatchCommand request,
        ArtistId[]? selectedArtists = null,
        BatchProgress? progress = null
    )
    {
        progress ??= new BatchProgress(
            ImportWorkflowOptions.AssignExecution(null),
            Workflow.UtcNow.Year
        );
        ImportWorkflowOptions.RequireActive(progress.Execution);
        selectedArtists ??= await Workflow.ExecuteActivityAsync(
            (IngestionBatchActivities a) => a.SelectArtists(request, progress.Execution),
            ImportWorkflowOptions.Activity(IngestionQueues.Database)
        );
        var result = progress.Result ?? new ImportBatchResult();
        var active = new List<Task<ArtistOutcome>>();
        var first = progress.NextArtist;
        var end = Math.Min(first + 250, selectedArtists.Length);
        try
        {
            while (progress.NextArtist < end)
            {
                Workflow.CancellationToken.ThrowIfCancellationRequested();
                ImportWorkflowOptions.RequireActive(progress.Execution);
                active.Add(ImportArtist(request, selectedArtists[progress.NextArtist], progress));
                progress = progress with { NextArtist = progress.NextArtist + 1 };
                if (active.Count == 10)
                    result = await FinishOne(active, result);
                if (Workflow.ContinueAsNewSuggested)
                    break;
            }
            // Continuation carries no live children.
            while (active.Count > 0)
                result = await FinishOne(active, result);
        }
        catch (Exception error) when (TemporalException.IsCanceledException(error))
        {
            // One child's cancellation can complete before the other nine. Keep the
            // parent open until every started child has acknowledged cancellation.
            try
            {
                await Workflow.WhenAllAsync(active);
            }
            catch (Exception)
            {
                // WhenAll waits for every task even when one fails. Preserve the
                // original cancellation after the remaining children have settled.
            }
            throw;
        }
        if (progress.NextArtist < selectedArtists.Length)
        {
            progress = progress with { Result = result };
            throw Workflow.CreateContinueAsNewException(
                (RefreshBatchWorkflow w) => w.RunAsync(request, selectedArtists, progress)
            );
        }
        return result;
    }

    private static async Task<ImportBatchResult> FinishOne(
        List<Task<ArtistOutcome>> active,
        ImportBatchResult result
    )
    {
        var finished = await Workflow.WhenAnyAsync(active);
        active.Remove(finished);
        return await finished switch
        {
            ArtistOutcome.Completed => result with
            {
                CompletedArtists = result.CompletedArtists + 1,
            },
            ArtistOutcome.Partial => result with { PartialArtists = result.PartialArtists + 1 },
            ArtistOutcome.Conflict => result with
            {
                ConflictingArtists = result.ConflictingArtists + 1,
            },
            _ => result with { FailedArtists = result.FailedArtists + 1 },
        };
    }

    private static async Task<ArtistOutcome> ImportArtist(
        CatalogBatchCommand request,
        ArtistId artist,
        BatchProgress progress
    )
    {
        var options = CoordinatorOptions.Child($"ingest/artist/{artist}", progress.Execution);
        try
        {
            ArtistImportResult result;
            if (request is RebuildCatalog)
                result = await Workflow.ExecuteChildWorkflowAsync(
                    (RebuildArtistCatalogWorkflow w) =>
                        w.RunAsync(new RebuildArtistCatalog(artist), progress.Execution),
                    options
                );
            else
            {
                ArtistRefreshCommand command =
                    request is RefreshRecentlyActiveArtists
                        ? new RefreshFilteredArtist(artist, progress.FilterYear)
                        : new RefreshArtist(artist);
                result = await Workflow.ExecuteChildWorkflowAsync(
                    (ArtistRefreshWorkflow w) => w.RunAsync(command, progress.Execution),
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
            when (!TemporalException.IsCanceledException(error) && !ContainsExpiration(error))
        {
            return ContainsConflict(error) ? ArtistOutcome.Conflict : ArtistOutcome.Failed;
        }
    }

    private static bool ContainsExpiration(Exception error) =>
        error is ApplicationFailureException { ErrorType: "ImportExpired" }
        || error.InnerException != null && ContainsExpiration(error.InnerException);

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
