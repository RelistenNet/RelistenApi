using System.Collections.Concurrent;
using FluentAssertions;
using NUnit.Framework;
using Relisten.Accounts.Contracts.Identifiers;
using Relisten.Accounts.Contracts.Search;
using RelistenIngestionWorker.PlaylistSearch;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace RelistenUserServiceTests;

[TestFixture]
public sealed class TestPlaylistSearchSyncWorkflow
{
    [Test]
    public Task ContinuingEditsCannotDelayReindexPastTheFirstEditsDeadline() =>
        WithWorker(
            new ReindexProbe(),
            async (environment, reindex) =>
            {
                var playlist = PlaylistId.New().Value;
                var now = (await environment.GetCurrentTimeAsync()).ToUniversalTime();
                // Resume a batch near the end of its window. Recent and repeated
                // edits would keep this waiting long past the cap with debounce alone.
                var window = new PlaylistSearchSyncWorkflow.EditWindow(now.AddSeconds(-292), now);
                var run = await environment.Client.StartWorkflowAsync(
                    (PlaylistSearchSyncWorkflow workflow) => workflow.RunAsync(playlist, window),
                    new WorkflowOptions(PlaylistSearchJob.SyncId(playlist), PlaylistSearchJob.Queue)
                );
                for (var edit = 0; edit < 4; edit++)
                {
                    await run.SignalAsync(workflow => workflow.Changed());
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }

                await run.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
                reindex.Completed.Should().ContainSingle(result => result.Playlist == playlist);
                var scheduled = (await run.FetchHistoryAsync()).Events.Single(item =>
                    item.ActivityTaskScheduledEventAttributes is not null
                );
                (scheduled.EventTime.ToDateTime() - window.FirstEditAt)
                    .Should()
                    .BeLessThan(TimeSpan.FromSeconds(303));
            }
        );

    [Test]
    public Task AnEditDuringReindexContinuesWithAFreshHistory() =>
        WithWorker(
            new ReindexProbe { HoldIndex = true },
            async (environment, reindex) =>
            {
                var playlist = PlaylistId.New().Value;
                var run = await StartDueSync(environment, playlist);

                try
                {
                    await reindex.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await run.SignalAsync(workflow => workflow.Changed());
                }
                finally
                {
                    reindex.Release.TrySetResult();
                }

                var nextRun = await FollowContinuation(run);
                reindex.Completed.Should().Equal((run.ResultRunId!, playlist), (nextRun, playlist));
            }
        );

    [Test]
    public Task ATransientFailureIsRetriedWithinTheSameRun() =>
        WithWorker(
            new ReindexProbe { Fail = (_, attempt) => attempt == 1 },
            async (environment, reindex) =>
            {
                var playlist = PlaylistId.New().Value;
                var run = await StartDueSync(environment, playlist);

                await run.GetResultAsync(followRuns: false).WaitAsync(TimeSpan.FromSeconds(15));

                reindex.Completed.Should().Equal((run.ResultRunId!, playlist));
            }
        );

    [Test]
    public Task FailedSyncCarriesTheEditIntoANewRunWithoutAnotherSignal()
    {
        string? firstRun = null;
        return WithWorker(
            new ReindexProbe
            {
                Fail = (runId, _) =>
                {
                    var recordedRun = Interlocked.CompareExchange(ref firstRun, runId, null);
                    return recordedRun is null || recordedRun == runId;
                },
            },
            async (environment, reindex) =>
            {
                var playlist = PlaylistId.New().Value;
                var run = await StartDueSync(environment, playlist);

                var nextRun = await FollowContinuation(run);

                reindex.Completed.Should().Equal((nextRun, playlist));
            }
        );
    }

    private static async Task<WorkflowHandle<PlaylistSearchSyncWorkflow>> StartDueSync(
        WorkflowEnvironment environment,
        string playlist
    )
    {
        var now = await environment.GetCurrentTimeAsync();
        var window = new PlaylistSearchSyncWorkflow.EditWindow(now.AddMinutes(-5), now);
        return await environment.Client.StartWorkflowAsync(
            (PlaylistSearchSyncWorkflow workflow) => workflow.RunAsync(playlist, window),
            new WorkflowOptions(PlaylistSearchJob.SyncId(playlist), PlaylistSearchJob.Queue)
        );
    }

    private static async Task<string> FollowContinuation(
        WorkflowHandle<PlaylistSearchSyncWorkflow> run
    )
    {
        var continued = Assert.ThrowsAsync<WorkflowContinuedAsNewException>(() =>
            run.GetResultAsync(followRuns: false).WaitAsync(TimeSpan.FromSeconds(15))
        );
        await run.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(15));
        return continued!.NewRunId;
    }

    private static async Task WithWorker(
        ReindexProbe reindex,
        Func<WorkflowEnvironment, ReindexProbe, Task> test
    )
    {
        await using var environment = await WorkflowEnvironment.StartTimeSkippingAsync();
        using var worker = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(PlaylistSearchJob.Queue)
                .AddWorkflow<PlaylistSearchSyncWorkflow>()
                .AddAllActivities(reindex)
        );
        await worker.ExecuteAsync(() => test(environment, reindex));
    }

    public sealed class ReindexProbe
    {
        public ConcurrentQueue<(string RunId, string Playlist)> Completed { get; } = new();
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<string, int, bool>? Fail { get; init; }
        public bool HoldIndex { get; init; }
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Activity("Sync")]
        public async Task Sync(string playlistId)
        {
            Started.TrySetResult();
            if (HoldIndex)
            {
                await Release.Task;
            }

            var info = ActivityExecutionContext.Current.Info;
            var runId =
                info.WorkflowRunId
                ?? throw new InvalidOperationException("Expected a workflow run ID.");
            if (Fail?.Invoke(runId, info.Attempt) == true)
            {
                throw new ApplicationFailureException("Typesense temporarily unavailable");
            }

            Completed.Enqueue((runId, playlistId));
        }
    }
}
