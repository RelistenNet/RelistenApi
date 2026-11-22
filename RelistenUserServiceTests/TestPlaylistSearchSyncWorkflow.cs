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

                (await reindex.Indexed.Task.WaitAsync(TimeSpan.FromSeconds(10)))
                    .Should()
                    .Be(playlist);
                await run.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
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
                var now = (await environment.GetCurrentTimeAsync()).ToUniversalTime();
                var window = new PlaylistSearchSyncWorkflow.EditWindow(now.AddMinutes(-5), now);
                var run = await environment.Client.StartWorkflowAsync(
                    (PlaylistSearchSyncWorkflow workflow) => workflow.RunAsync(playlist, window),
                    new WorkflowOptions(PlaylistSearchJob.SyncId(playlist), PlaylistSearchJob.Queue)
                );

                try
                {
                    await reindex.Indexed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await run.SignalAsync(workflow => workflow.Changed());
                }
                finally
                {
                    reindex.Release.TrySetResult();
                }

                Func<Task> originalRun = () =>
                    run.GetResultAsync(followRuns: false).WaitAsync(TimeSpan.FromSeconds(5));
                await originalRun.Should().ThrowAsync<WorkflowContinuedAsNewException>();
            }
        );

    private static async Task WithWorker(
        ReindexProbe reindex,
        Func<WorkflowEnvironment, ReindexProbe, Task> test
    )
    {
        await using var environment = await WorkflowEnvironment.StartLocalAsync();
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
        public TaskCompletionSource<string> Indexed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HoldIndex { get; init; }
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Activity("Sync")]
        public Task Sync(string playlistId)
        {
            Indexed.TrySetResult(playlistId);
            return HoldIndex ? Release.Task : Task.CompletedTask;
        }
    }
}
