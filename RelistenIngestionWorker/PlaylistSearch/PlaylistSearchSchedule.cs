using Relisten.Accounts.Contracts.Search;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Client.Schedules;
using Temporalio.Exceptions;

namespace RelistenIngestionWorker.PlaylistSearch;

internal static class PlaylistSearchSchedule
{
    public const string Id = "relisten-playlist-search-build";

    public static async Task RegisterAsync(ITemporalClient client)
    {
        var schedule = new Schedule(
            ScheduleActionStartWorkflow.Create(
                (ScheduledPlaylistSearchWorkflow workflow) => workflow.RunAsync(),
                new WorkflowOptions(Id, PlaylistSearchJob.Queue)
            ),
            new ScheduleSpec { CronExpressions = ["30 11 * * *"], TimeZoneName = "UTC" }
        )
        {
            Policy = new SchedulePolicy { Overlap = ScheduleOverlapPolicy.Skip },
            State = new ScheduleState { Paused = true },
        };

        try
        {
            await client.CreateScheduleAsync(Id, schedule);
        }
        catch (ScheduleAlreadyRunningException)
        {
            // Preserve the operator's cadence and pause state.
        }
    }
}
