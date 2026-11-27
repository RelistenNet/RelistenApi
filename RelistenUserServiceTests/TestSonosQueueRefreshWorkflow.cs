using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RelistenUserService.Sonos;
using Temporalio.Activities;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace RelistenUserServiceTests;

[TestFixture]
public sealed class TestSonosQueueRefreshWorkflow
{
    [Test]
    public async Task An_edit_during_a_failed_refresh_still_reaches_the_room()
    {
        await using var temporal = await WorkflowEnvironment.StartLocalAsync();
        var room = new Room();
        using var worker = new TemporalWorker(
            temporal.Client,
            new TemporalWorkerOptions(SonosQueueRefreshStarter.TaskQueue)
                .AddWorkflow<SonosQueueRefreshWorkflow>()
                .AddAllActivities(room)
        );
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var starter = new SonosQueueRefreshStarter(
            temporal.Client,
            NullLogger<SonosQueueRefreshStarter>.Instance
        );
        var queueId = Guid.NewGuid();

        await worker.ExecuteAsync(async () =>
        {
            (await starter.TrySignalAsync(queueId, deadline.Token)).Should().BeTrue();
            await room.FirstRefreshStarted.Task.WaitAsync(deadline.Token);
            try
            {
                (await starter.TrySignalAsync(queueId, deadline.Token)).Should().BeTrue();
            }
            finally
            {
                room.RejectFirstRefresh.SetResult();
            }

            await room.Delivered.Task.WaitAsync(deadline.Token);
        });
    }

    public sealed class Room
    {
        public TaskCompletionSource FirstRefreshStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RejectFirstRefresh { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Delivered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Activity("Refresh")]
        public async Task RefreshAsync(Guid queueId)
        {
            if (FirstRefreshStarted.TrySetResult())
            {
                await RejectFirstRefresh.Task;
                throw new ApplicationFailureException(
                    "Sonos rejected this edit.",
                    nonRetryable: true
                );
            }

            Delivered.TrySetResult();
        }
    }
}
