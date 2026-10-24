using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Relisten.Normalization.Temporal;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Workflows;

/// <summary>Rebuilds derived catalog data in dependency order, after all item work has settled.</summary>
internal static class CatalogSteps
{
    /// matchVenues: after a whole-artist import, bring its new venue rows into norm too. A
    /// one-item import leaves them to the nightly venue run, which starts with the match.
    internal static async Task RebuildArtist(
        ArtistId artist,
        IngestionExecution execution,
        bool matchVenues = false
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

        // The artist's new venue rows into norm, when its venues are switched on
        // (features.norm_venues): the free match now, the model in the nightly run. Venues
        // never fail an import. Patched: imports running at deploy replay without it.
        if (matchVenues && Workflow.Patched("norm-venue-match"))
        {
            try
            {
                await Workflow.ExecuteActivityAsync(
                    (VenueMatchActivities a) => a.MatchArtist(artist.Value),
                    VenueMatchStep
                );
            }
            catch (ActivityFailureException e)
            {
                Workflow.Logger.LogWarning(
                    e,
                    "Venue match after the import of {Artist} failed",
                    artist.Value
                );
            }
        }
    }

    private static readonly ActivityOptions VenueMatchStep = new()
    {
        TaskQueue = IngestionQueues.Database,
        StartToCloseTimeout = TimeSpan.FromMinutes(20),
        ScheduleToCloseTimeout = TimeSpan.FromMinutes(45),
        HeartbeatTimeout = TimeSpan.FromSeconds(30),
        RetryPolicy = new RetryPolicy
        {
            MaximumAttempts = 2,
            InitialInterval = TimeSpan.FromSeconds(30),
        },
    };
}
