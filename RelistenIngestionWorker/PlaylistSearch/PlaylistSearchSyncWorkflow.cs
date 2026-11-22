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
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaximumWait = TimeSpan.FromMinutes(5);
    private EditWindow? pending;

    [WorkflowSignal(PlaylistSearchJob.ChangedSignal)]
    public Task Changed()
    {
        var now = Workflow.UtcNow;
        pending = new(pending?.FirstEditAt ?? now, now);
        return Task.CompletedTask;
    }

    [WorkflowRun]
    public async Task RunAsync(string playlistId, EditWindow? previous = null)
    {
        if (previous is not null)
        {
            pending = new(previous.FirstEditAt, pending?.LastEditAt ?? previous.LastEditAt);
        }
        await Workflow.WaitConditionAsync(() => pending is not null);
        await WaitForBatch();
        pending = null;
        await Workflow.ExecuteActivityAsync(
            (PlaylistSearchActivities activity) => activity.Sync(playlistId),
            PlaylistSearchActivities.Options(TimeSpan.FromMinutes(5))
        );

        if (pending is { } next)
        {
            // Edits arriving during indexing need a later read. Carry only that
            // batch into a new history under the same workflow ID.
            throw Workflow.CreateContinueAsNewException(
                (PlaylistSearchSyncWorkflow workflow) => workflow.RunAsync(playlistId, next)
            );
        }
    }

    private async Task WaitForBatch()
    {
        while (pending is { } batch)
        {
            var quietUntil = batch.LastEditAt + QuietPeriod;
            var deadline = batch.FirstEditAt + MaximumWait;
            var wakeAt = quietUntil < deadline ? quietUntil : deadline;
            var delay = wakeAt - Workflow.UtcNow;
            if (
                delay <= TimeSpan.Zero
                || !await Workflow.WaitConditionAsync(() => pending != batch, delay)
            )
            {
                return;
            }
        }
    }

    public sealed record EditWindow(DateTime FirstEditAt, DateTime LastEditAt);
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
