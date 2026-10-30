using System;
using System.Threading.Tasks;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Client.Schedules;
using Temporalio.Exceptions;

namespace Relisten.Normalization.Temporal;

/// A normalization workflow of that id is already running.
public sealed class NormalizationAlreadyRunning(string workflowId)
    : Exception($"{workflowId} is already running")
{
    public string WorkflowId { get; } = workflowId;
}

/// Starts normalization workflows (operator commands and schedules).
public sealed class NormalizationClient(ITemporalClient client)
{
    /// The one id every venue run has, manual or nightly: venues are cross-artist, so two runs
    /// never overlap.
    public const string VenueRunId = "norm/venues";

    /// Register the nightly venue model run (10:00 UTC) and count reconciliation every
    /// five minutes. The model run uses at most nightlyCalls calls for artists whose
    /// venues are switched on. Registration preserves saved schedules.
    public async Task RegisterSchedulesAsync(int nightlyCalls = 60)
    {
        var nightly = new BuildVenueCatalogue(null, nightlyCalls, FlaggedOnly: true);
        var schedule = new Schedule(
            ScheduleActionStartWorkflow.Create(
                (NightlyVenueCatalogueWorkflow workflow) => workflow.RunAsync(nightly),
                new WorkflowOptions("norm/venues/nightly", NormalizationQueues.Workflows)
                {
                    ExecutionTimeout = TimeSpan.FromDays(1),
                    IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
                }
            ),
            new ScheduleSpec { CronExpressions = ["0 10 * * *"], TimeZoneName = "UTC" }
        )
        {
            Policy = new SchedulePolicy { Overlap = ScheduleOverlapPolicy.Skip },
        };

        try
        {
            await client.CreateScheduleAsync("relisten-norm-venues", schedule);
        }
        catch (ScheduleAlreadyRunningException)
        {
            // Operators own existing schedules, including their paused state.
        }

        await RegisterArtistVenueScheduleAsync();
    }

    /// Starts an operator-requested reconciliation of all stored artist venue counts.
    public async Task<string> RefreshArtistVenuesAsync()
    {
        const string workflowId = "norm/artist-venues/manual";
        try
        {
            var handle = await client.StartWorkflowAsync(
                (ArtistVenueRefreshWorkflow workflow) => workflow.RunAsync(),
                ArtistVenueOptions(workflowId)
            );
            return handle.Id;
        }
        catch (WorkflowAlreadyStartedException)
        {
            throw new NormalizationAlreadyRunning(workflowId);
        }
    }

    private async Task RegisterArtistVenueScheduleAsync()
    {
        // Skip overlapping scheduled runs. Manual runs and post-write hooks may overlap;
        // only refreshers take the database's dedicated refresh advisory lock.
        var schedule = new Schedule(
            ScheduleActionStartWorkflow.Create(
                (ArtistVenueRefreshWorkflow workflow) => workflow.RunAsync(),
                ArtistVenueOptions("norm/artist-venues/scheduled")
            ),
            new ScheduleSpec { CronExpressions = ["*/5 * * * *"], TimeZoneName = "UTC" }
        )
        {
            Policy = new SchedulePolicy { Overlap = ScheduleOverlapPolicy.Skip },
        };

        try
        {
            await client.CreateScheduleAsync("relisten-artist-venues-refresh", schedule);
        }
        catch (ScheduleAlreadyRunningException)
        {
            // Existing schedules, including manual pause state, belong to operators.
        }
    }

    private static WorkflowOptions ArtistVenueOptions(string id) =>
        new(id, NormalizationQueues.Workflows)
        {
            ExecutionTimeout = TimeSpan.FromMinutes(10),
            IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
        };

    /// Starts the venue catalogue, for every artist's venue rows or one artist's. Venues are
    /// cross-artist, so venue runs never overlap: one workflow id for all of them.
    public async Task<string> BuildVenueCatalogueAsync(BuildVenueCatalogue request)
    {
        const string workflowId = VenueRunId;
        try
        {
            var handle = await client.StartWorkflowAsync(
                (VenueCatalogueWorkflow workflow) => workflow.RunAsync(request),
                new WorkflowOptions(workflowId, NormalizationQueues.Workflows)
                {
                    ExecutionTimeout = TimeSpan.FromDays(7),
                    IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
                }
            );
            return handle.Id;
        }
        catch (WorkflowAlreadyStartedException)
        {
            throw new NormalizationAlreadyRunning(workflowId);
        }
    }

    /// Starts the LLM song catalogue for one artist. Its workflow id is separate from imports,
    /// so a long run never blocks the artist's import; one run per artist at a time.
    public async Task<string> BuildSongCatalogueAsync(BuildSongCatalogue request)
    {
        var workflowId = "norm/songs/" + request.Artist;
        try
        {
            var handle = await client.StartWorkflowAsync(
                (SongCatalogueWorkflow workflow) => workflow.RunAsync(request),
                new WorkflowOptions(workflowId, NormalizationQueues.Workflows)
                {
                    ExecutionTimeout = TimeSpan.FromDays(7),
                    IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
                }
            );
            return handle.Id;
        }
        catch (WorkflowAlreadyStartedException)
        {
            throw new NormalizationAlreadyRunning(workflowId);
        }
    }
}
