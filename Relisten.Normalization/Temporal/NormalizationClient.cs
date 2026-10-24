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
    /// The nightly venue run (10:00 UTC, after the 07:00 full ingest): the model for what the
    /// post-import matches left, for the artists whose venues are switched on, at most
    /// nightlyCalls model calls. Registration never replaces or unpauses a saved schedule.
    public async Task RegisterSchedulesAsync(int nightlyCalls = 60)
    {
        var nightly = new BuildVenueCatalogue(null, nightlyCalls, FlaggedOnly: true);
        var schedule = new Schedule(
            ScheduleActionStartWorkflow.Create(
                (VenueCatalogueWorkflow workflow) => workflow.RunAsync(nightly),
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
    }

    /// Starts the venue catalogue, for every artist's venue rows or one artist's. Venues are
    /// cross-artist, so venue runs never overlap: one workflow id for all of them.
    public async Task<string> BuildVenueCatalogueAsync(BuildVenueCatalogue request)
    {
        const string workflowId = "norm/venues";
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
