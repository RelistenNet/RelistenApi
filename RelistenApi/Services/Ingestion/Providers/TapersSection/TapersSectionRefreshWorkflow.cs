using System;
using System.Linq;
using System.Threading.Tasks;
using Relisten.Ingestion.Workflows;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Providers.TapersSection;

/// <summary>Materializes approved candidates, then refreshes approved and dirty cohort artists.</summary>
[Workflow]
public sealed class TapersSectionRefreshWorkflow
{
    [WorkflowRun]
    public async Task<TapersSectionSummary> RunAsync(
        RefreshTapersSection request,
        ArtistId[]? artists = null,
        TapersSectionRefreshProgress? progress = null
    )
    {
        progress ??= new(ImportWorkflowOptions.AssignExecution(null));
        var execution = progress.Execution;
        ImportWorkflowOptions.RequireActive(execution);
        artists ??= await Workflow.ExecuteActivityAsync(
            (TapersSectionActivities a) => a.PrepareCohort(execution),
            ImportWorkflowOptions.Activity(IngestionQueues.Database)
        );
        var summary = progress.Summary ?? new();
        var segmentEnd = Math.Min(progress.NextArtist + 250, artists.Length);

        while (progress.NextArtist < segmentEnd)
        {
            // A settled group makes continuation and cancellation ownership explicit.
            var group = artists
                .Skip(progress.NextArtist)
                .Take(Math.Min(10, segmentEnd - progress.NextArtist))
                .ToArray();
            var outcomes = await Workflow.WhenAllAsync(
                group.Select(artist => RefreshArtistAsync(artist, execution))
            );
            foreach (var outcome in outcomes)
            {
                summary = summary.Add(outcome);
            }

            progress = progress with { NextArtist = progress.NextArtist + group.Length };
            if (Workflow.ContinueAsNewSuggested)
            {
                break;
            }
        }

        if (progress.NextArtist < artists.Length)
        {
            var checkpoint = progress with { Summary = summary };
            throw Workflow.CreateContinueAsNewException(
                (TapersSectionRefreshWorkflow w) => w.RunAsync(request, artists, checkpoint)
            );
        }

        return summary;
    }

    private static async Task<TapersSectionSummary> RefreshArtistAsync(
        ArtistId artist,
        IngestionExecution execution
    )
    {
        try
        {
            var result = await Workflow.ExecuteChildWorkflowAsync(
                (TapersSectionArtistWorkflow w) => w.RunAsync(new(artist), execution, null, null),
                CoordinatorOptions.Child("ingest/artist/" + artist, execution)
            );
            return result.Summary;
        }
        catch (WorkflowAlreadyStartedException)
        {
            return new(Conflicts: 1);
        }
        catch (TemporalException error)
            when (!TemporalException.IsCanceledException(error)
                && !ImportWorkflowOptions.ContainsExpiration(error)
            )
        {
            return ContainsConflict(error)
                ? new(Conflicts: 1)
                : new TapersSectionSummary().AddFailure(
                    new ItemFailure<ArtistId>(artist, "ArtistRefreshFailed", error.Message)
                );
        }
    }

    private static bool ContainsConflict(Exception error) =>
        error is WorkflowAlreadyStartedException
        || error.InnerException != null && ContainsConflict(error.InnerException);
}
