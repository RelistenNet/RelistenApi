using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RelistenUserService.Sonos;
using Temporalio.Activities;
using Temporalio.Client;
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
            new TemporalWorkerOptions(SonosWorkflowStarter.TaskQueue)
                .AddWorkflow<SonosQueueRefreshWorkflow>()
                .AddAllActivities(room)
        );
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var starter = new SonosWorkflowStarter(
            temporal.Client,
            NullLogger<SonosWorkflowStarter>.Instance
        );
        var queueId = Guid.NewGuid();

        await worker.ExecuteAsync(async () =>
        {
            (await starter.TrySignalQueueAsync(queueId, deadline.Token)).Should().BeTrue();
            await room.FirstRefreshStarted.Task.WaitAsync(deadline.Token);
            try
            {
                (await starter.TrySignalQueueAsync(queueId, deadline.Token)).Should().BeTrue();
            }
            finally
            {
                room.RejectFirstRefresh.SetResult();
            }

            await room.Delivered.Task.WaitAsync(deadline.Token);
        });
    }

    [Test]
    public async Task A_household_workflow_stops_retrying_a_persistent_provider_failure()
    {
        await using var temporal = await WorkflowEnvironment.StartTimeSkippingAsync();
        using var worker = new TemporalWorker(
            temporal.Client,
            new TemporalWorkerOptions(SonosWorkflowStarter.TaskQueue)
                .AddWorkflow<SonosHouseholdWorkflow>()
                .AddAllActivities(new FailingHousehold())
        );
        await worker.ExecuteAsync(async () =>
        {
            var handle = await temporal.Client.StartWorkflowAsync(
                (SonosHouseholdWorkflow workflow) => workflow.RunAsync("stuck-household"),
                new WorkflowOptions("household-retry-test", SonosWorkflowStarter.TaskQueue)
            );
            var failure = Assert.ThrowsAsync<WorkflowFailedException>(() =>
                handle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10))
            );
            failure!
                .InnerException.Should()
                .BeOfType<ActivityFailureException>()
                .Which.InnerException.Should()
                .BeOfType<ApplicationFailureException>();
        });
    }

    [Test]
    public async Task A_regroup_received_during_a_failed_refresh_still_refreshes_names()
    {
        var household = new FailingHousehold { Recover = true };
        await using var temporal = await WorkflowEnvironment.StartTimeSkippingAsync();
        using var worker = new TemporalWorker(
            temporal.Client,
            new TemporalWorkerOptions(SonosWorkflowStarter.TaskQueue)
                .AddWorkflow<SonosHouseholdWorkflow>()
                .AddAllActivities(household)
        );
        await worker.ExecuteAsync(async () =>
        {
            var handle = await temporal.Client.StartWorkflowAsync(
                (SonosHouseholdWorkflow workflow) => workflow.RunAsync("regrouped-household"),
                new WorkflowOptions("regroup-recovery-test", SonosWorkflowStarter.TaskQueue)
            );
            await household.FirstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await handle.SignalAsync(workflow => workflow.GroupsChanged());
            }
            finally
            {
                household.ReleaseFirstAttempt.TrySetResult();
            }

            await handle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
            household.Refreshed.Should().BeTrue();
        });
    }

    public sealed class FailingHousehold
    {
        public bool Recover { get; init; }
        public bool Refreshed { get; private set; }
        public TaskCompletionSource FirstAttempt { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstAttempt { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Activity("RefreshNames")]
        public async Task RefreshNamesAsync(string household)
        {
            if (!Recover)
            {
                throw new ApplicationFailureException("Sonos is unavailable.");
            }

            if (FirstAttempt.TrySetResult())
            {
                await ReleaseFirstAttempt.Task;
                throw new ApplicationFailureException(
                    "Sonos rejected this refresh.",
                    nonRetryable: true
                );
            }

            Refreshed = true;
        }
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
