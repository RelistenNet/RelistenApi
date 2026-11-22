using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Relisten.Ingestion;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Client.Schedules;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Relisten.Search;

internal static class SearchBuildSchedule
{
    internal const string Id = "relisten-search-build";

    internal static async Task RegisterAsync(ITemporalClient client)
    {
        var schedule = new Schedule(
            ScheduleActionStartWorkflow.Create(
                (ScheduledSearchBuildWorkflow workflow) => workflow.RunAsync(),
                new WorkflowOptions(Id, IngestionQueues.Workflows)
            ),
            new ScheduleSpec { CronExpressions = ["0 11 * * *"], TimeZoneName = "UTC" }
        )
        {
            Policy = new SchedulePolicy { Overlap = ScheduleOverlapPolicy.Skip },
            // Enable once the initial catalog index and production search are verified.
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

/// <summary>Runs the daily build under the same exclusive ID as a manual build.</summary>
[Workflow]
public sealed class ScheduledSearchBuildWorkflow
{
    [WorkflowRun]
    public async Task RunAsync()
    {
        try
        {
            await Workflow.ExecuteChildWorkflowAsync(
                (SearchBuildWorkflow workflow) => workflow.RunAsync(null),
                // Temporal gives each scheduled run a new ID. Only this child
                // can acquire the ID shared with manual builds and retirement.
                new ChildWorkflowOptions { Id = SearchBuildWorkflow.Id }
            );
        }
        catch (WorkflowAlreadyStartedException)
        {
            Workflow.Logger.LogInformation(
                "Skipped daily search build because the build ID is in use"
            );
        }
    }
}
