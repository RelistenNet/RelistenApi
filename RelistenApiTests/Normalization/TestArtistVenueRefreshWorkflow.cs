using FluentAssertions;
using Relisten.Normalization.Temporal;
using Relisten.Normalization.Venues;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace RelistenApiTests.Normalization;

/// <summary>Exercises reconciliation and retries with synthetic activities, never a catalog or model.</summary>
[TestFixture, NonParallelizable]
public sealed class TestArtistVenueRefreshWorkflow
{
    private WorkflowEnvironment environment = null!;

    [OneTimeSetUp]
    public async Task StartServer() => environment = await WorkflowEnvironment.StartLocalAsync();

    [OneTimeTearDown]
    public async Task StopServer() => await environment.DisposeAsync();

    [Test]
    public async Task ReconciliationReturnsTheCommittedRefreshResult()
    {
        var activities = new RefreshActivities(new(3, 4));
        var result = await Run(activities);

        result.Should().Be(new ArtistVenueRefreshResult(3, 4));
        activities.Calls.Should().Be(1);
    }

    [Test]
    public async Task FailedRefreshRetriesBeforeReturningTheCommittedResult()
    {
        var activities = new RefreshActivities(new(2, 3)) { FailuresRemaining = 1 };
        var result = await Run(activities);

        result.Should().Be(new ArtistVenueRefreshResult(2, 3));
        activities.Calls.Should().Be(2);
    }

    [Test]
    public async Task ExhaustedFailureCanRecoverOnALaterRun()
    {
        var activities = new RefreshActivities(new(1, 2)) { FailuresRemaining = 3 };
        var failed = async () => await Run(activities);
        await failed.Should().ThrowAsync<WorkflowFailedException>();

        var recovered = await Run(activities);
        recovered.Should().Be(new ArtistVenueRefreshResult(1, 2));
        activities.Calls.Should().Be(4);
    }

    [Test]
    public async Task ScheduleRegistrationPreservesOperatorPauseAndCanReconcile()
    {
        var client = new NormalizationClient(environment.Client);
        var ids = new[] { "relisten-norm-venues", "relisten-artist-venues-refresh" };
        await client.RegisterSchedulesAsync();
        try
        {
            foreach (var id in ids)
            {
                await environment.Client.GetScheduleHandle(id).PauseAsync("Test operator pause");
            }

            await client.RegisterSchedulesAsync();
            foreach (var id in ids)
            {
                var saved = await environment.Client.GetScheduleHandle(id).DescribeAsync();
                saved.Schedule.State.Paused.Should().BeTrue();
            }

            var refresh = environment.Client.GetScheduleHandle("relisten-artist-venues-refresh");
            var activities = new RefreshActivities(new(1, 2));
            using var workflows = WorkflowWorker();
            using var database = DatabaseWorker(activities);
            await workflows.ExecuteAsync(() =>
                database.ExecuteAsync(async () =>
                {
                    await refresh.TriggerAsync();
                    var id = await activities.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    var result = await environment
                        .Client.GetWorkflowHandle(id)
                        .GetResultAsync<ArtistVenueRefreshResult>()
                        .WaitAsync(TimeSpan.FromSeconds(20));
                    result.Should().Be(new ArtistVenueRefreshResult(1, 2));
                })
            );
        }
        finally
        {
            foreach (var id in ids)
            {
                await environment.Client.GetScheduleHandle(id).DeleteAsync();
            }
        }
    }

    private async Task<ArtistVenueRefreshResult> Run(RefreshActivities activities)
    {
        using var workflows = WorkflowWorker();
        using var database = DatabaseWorker(activities);
        return await workflows.ExecuteAsync(() =>
            database.ExecuteAsync(() =>
                environment.Client.ExecuteWorkflowAsync(
                    (ArtistVenueRefreshWorkflow workflow) => workflow.RunAsync(),
                    new WorkflowOptions(
                        "artist-venues/test/" + Guid.NewGuid(),
                        NormalizationQueues.Workflows
                    )
                )
            )
        );
    }

    private TemporalWorker WorkflowWorker() =>
        new(
            environment.Client,
            new TemporalWorkerOptions(
                NormalizationQueues.Workflows
            ).AddWorkflow<ArtistVenueRefreshWorkflow>()
        );

    private TemporalWorker DatabaseWorker(RefreshActivities activities) =>
        new(
            environment.Client,
            new TemporalWorkerOptions(NormalizationQueues.Database).AddAllActivities(activities)
        );

    private sealed class RefreshActivities(ArtistVenueRefreshResult result)
    {
        public int Calls { get; private set; }
        public int FailuresRemaining { get; set; }
        public TaskCompletionSource<string> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Activity("ArtistVenues.RefreshAll")]
        public Task<ArtistVenueRefreshResult> RefreshAll()
        {
            Started.TrySetResult(ActivityExecutionContext.Current.Info.WorkflowId!);
            Calls++;
            if (FailuresRemaining-- > 0)
            {
                throw new InvalidOperationException("Synthetic database failure");
            }
            return Task.FromResult(result);
        }
    }
}
