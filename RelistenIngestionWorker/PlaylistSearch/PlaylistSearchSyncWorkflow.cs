using Relisten.Accounts.Contracts.Search;
using Relisten.Ingestion;
using Temporalio.Activities;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace RelistenIngestionWorker.PlaylistSearch;

[Workflow(PlaylistSearchJob.SyncWorkflow)]
public sealed class PlaylistSearchSyncWorkflow
{
    private bool changed;

    [WorkflowSignal(PlaylistSearchJob.ChangedSignal)]
    public Task Changed()
    {
        changed = true;
        return Task.CompletedTask;
    }

    [WorkflowRun]
    public async Task RunAsync(string playlistId)
    {
        do
        {
            do
            {
                changed = false;
                // Each edit starts a fresh quiet period for this playlist.
                await Workflow.WaitConditionAsync(() => changed, TimeSpan.FromMinutes(1));
            } while (changed);

            await Workflow.ExecuteActivityAsync(
                (PlaylistSearchActivities activity) => activity.Sync(playlistId),
                PlaylistSearchActivities.Options(TimeSpan.FromMinutes(5))
            );
            // A write may have arrived after the activity read the playlist.
        } while (changed);
    }
}

[Workflow]
public sealed class PlaylistSearchRebuildWorkflow
{
    [WorkflowRun]
    public Task RunAsync() =>
        Workflow.ExecuteActivityAsync(
            (PlaylistSearchActivities activity) => activity.Rebuild(),
            PlaylistSearchActivities.Options(TimeSpan.FromHours(1))
        );
}

[Workflow]
public sealed class ScheduledPlaylistSearchWorkflow
{
    [WorkflowRun]
    public async Task RunAsync()
    {
        try
        {
            await Workflow.ExecuteChildWorkflowAsync(
                (PlaylistSearchRebuildWorkflow workflow) => workflow.RunAsync(),
                new ChildWorkflowOptions { Id = PlaylistSearchJob.RebuildId }
            );
        }
        catch (WorkflowAlreadyStartedException)
        {
            // An operator's full build already owns publication.
        }
    }
}

internal sealed class PlaylistSearchActivities(PlaylistSearchIndex index)
{
    internal static ActivityOptions Options(TimeSpan timeout) =>
        new()
        {
            StartToCloseTimeout = timeout,
            HeartbeatTimeout = TimeSpan.FromSeconds(30),
            CancellationType = ActivityCancellationType.WaitCancellationCompleted,
            RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
        };

    [Activity]
    public async Task Sync(string playlistId)
    {
        using var heartbeat = new ActivityHeartbeat();
        await index.Sync(playlistId, ActivityExecutionContext.Current.CancellationToken);
    }

    [Activity]
    public async Task Rebuild()
    {
        using var heartbeat = new ActivityHeartbeat();
        await index.Rebuild(ActivityExecutionContext.Current.CancellationToken);
    }
}
