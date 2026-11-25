using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Relisten.Normalization.Temporal;
using Relisten.Search;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Workflows;

/// <summary>Rebuilds derived catalog data in dependency order, after all item work has settled.</summary>
internal static class CatalogSteps
{
    /// Artist refreshes match venues and songs before starting the search reindex.
    internal static async Task RebuildArtist(
        ArtistId artist,
        IngestionExecution execution,
        bool matchVenues = false,
        bool matchSongs = false
    )
    {
        var context = new ProviderWorkflowContext(artist, execution);

        ImportWorkflowOptions.RequireActive(execution);
        await Workflow.ExecuteActivityAsync(
            (CatalogActivities a) => a.RebuildShows(context),
            ImportWorkflowOptions.Activity(IngestionQueues.Database)
        );

        ImportWorkflowOptions.RequireActive(execution);
        await Workflow.ExecuteActivityAsync(
            (CatalogActivities a) => a.RebuildYears(context),
            ImportWorkflowOptions.Activity(IngestionQueues.Database)
        );

        // Invalidate cached responses only after both derived tables are rebuilt.
        ImportWorkflowOptions.RequireActive(execution);
        await Workflow.ExecuteActivityAsync(
            (CatalogActivities a) => a.InvalidateArtistCache(context),
            ImportWorkflowOptions.Activity(IngestionQueues.Database)
        );

        await MatchImportedCatalogue(artist, matchVenues, matchSongs);

        if (Workflow.Patched("search-after-artist-rebuild"))
        {
            await ArtistSearchReindexWorkflow.Start(artist.Value);
        }
    }

    private static async Task MatchImportedCatalogue(
        ArtistId artist,
        bool matchVenues,
        bool matchSongs
    )
    {
        // Each activity checks its artist's norm feature flag. The patch gates keep
        // imports already in flight replayable when new matching is deployed.
        if (matchVenues && Workflow.Patched("norm-venue-match"))
        {
            await TryMatch(
                artist,
                "Venue",
                () =>
                    Workflow.ExecuteActivityAsync(
                        (VenueMatchActivities a) => a.MatchArtist(artist.Value),
                        MatchStep
                    )
            );
        }

        if (matchSongs && Workflow.Patched("norm-song-match"))
        {
            await TryMatch(
                artist,
                "Song",
                () =>
                    Workflow.ExecuteActivityAsync(
                        (SongActivities a) => a.MatchImportedTracks(artist.Value),
                        MatchStep
                    )
            );
        }
    }

    private static async Task TryMatch(ArtistId artist, string kind, Func<Task> run)
    {
        try
        {
            await run();
        }
        catch (ActivityFailureException e) when (!TemporalException.IsCanceledException(e))
        {
            Workflow.Logger.LogWarning(
                e,
                "{Kind} match after the import of {Artist} failed",
                kind,
                artist.Value
            );
        }
    }

    private static readonly ActivityOptions MatchStep = new()
    {
        TaskQueue = IngestionQueues.Database,
        StartToCloseTimeout = TimeSpan.FromMinutes(20),
        ScheduleToCloseTimeout = TimeSpan.FromMinutes(45),
        // As core ingestion: a canceled import waits for this step to stop.
        CancellationType = ActivityCancellationType.WaitCancellationCompleted,
        HeartbeatTimeout = TimeSpan.FromSeconds(30),
        RetryPolicy = new RetryPolicy
        {
            MaximumAttempts = 2,
            InitialInterval = TimeSpan.FromSeconds(30),
        },
    };
}
