using Microsoft.Extensions.Logging;
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
    private static readonly TimeSpan RecoveryDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ProlongedFailure = TimeSpan.FromMinutes(30);
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
        await SyncBatch(playlistId, pending!);

        if (pending is { } next)
        {
            // Carry edits that still need a successful read into a fresh history.
            throw Workflow.CreateContinueAsNewException(
                (PlaylistSearchSyncWorkflow workflow) => workflow.RunAsync(playlistId, next)
            );
        }
    }

    private async Task SyncBatch(string playlistId, EditWindow batch)
    {
        // An edit that arrives during indexing needs a later read of the playlist.
        pending = null;
        try
        {
            await Workflow.ExecuteActivityAsync(
                (PlaylistSearchActivities activity) => activity.Sync(playlistId),
                PlaylistSearchActivities.Options(
                    TimeSpan.FromMinutes(5),
                    new RetryPolicy
                    {
                        MaximumAttempts = 5,
                        InitialInterval = TimeSpan.FromSeconds(10),
                        MaximumInterval = TimeSpan.FromMinutes(1),
                    }
                )
            );
        }
        catch (ActivityFailureException error) when (!TemporalException.IsCanceledException(error))
        {
            // Keep the original deadline and include any edits since the attempt started.
            pending = batch with
            {
                LastEditAt = pending?.LastEditAt ?? batch.LastEditAt,
            };
            var editAge = Workflow.UtcNow - batch.FirstEditAt;
            Workflow.Logger.Log(
                editAge >= ProlongedFailure ? LogLevel.Error : LogLevel.Warning,
                "Playlist search sync for {PlaylistId} still pending after {PendingMinutes:F1} minutes. Retrying in a new run.",
                playlistId,
                editAge.TotalMinutes
            );
            await Workflow.DelayAsync(RecoveryDelay);
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
    public async Task RunAsync()
    {
        var startedAt = await Workflow.ExecuteActivityAsync(
            (PlaylistSearchActivities activity) => activity.Rebuild(),
            PlaylistSearchActivities.Options(TimeSpan.FromHours(1))
        );
        await Workflow.ExecuteActivityAsync(
            (PlaylistSearchActivities activity) => activity.CatchUp(startedAt),
            PlaylistSearchActivities.Options(
                TimeSpan.FromMinutes(15),
                new RetryPolicy { MaximumAttempts = 3 }
            )
        );
    }
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
    internal static ActivityOptions Options(TimeSpan timeout, RetryPolicy? retryPolicy = null) =>
        new()
        {
            StartToCloseTimeout = timeout,
            HeartbeatTimeout = TimeSpan.FromSeconds(30),
            CancellationType = ActivityCancellationType.WaitCancellationCompleted,
            RetryPolicy = retryPolicy ?? new RetryPolicy { MaximumAttempts = 1 },
        };

    [Activity]
    public async Task Sync(string playlistId)
    {
        using var heartbeat = new ActivityHeartbeat();
        await index.Sync(playlistId, ActivityExecutionContext.Current.CancellationToken);
    }

    [Activity]
    public async Task<DateTime> Rebuild()
    {
        using var heartbeat = new ActivityHeartbeat();
        return await index.Rebuild(ActivityExecutionContext.Current.CancellationToken);
    }

    [Activity]
    public async Task CatchUp(DateTime startedAt)
    {
        using var heartbeat = new ActivityHeartbeat();
        await index.CatchUp(startedAt, ActivityExecutionContext.Current.CancellationToken);
    }
}
