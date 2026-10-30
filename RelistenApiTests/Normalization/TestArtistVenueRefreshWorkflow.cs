using FluentAssertions;
using Relisten.Normalization.Temporal;
using Relisten.Normalization.Venues;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace RelistenApiTests.Normalization;

/// <summary>Exercises the durable orchestration with synthetic activities, never a catalog or model.</summary>
[TestFixture, NonParallelizable]
public sealed class TestArtistVenueRefreshWorkflow
{
    private WorkflowEnvironment environment = null!;

    [OneTimeSetUp]
    public async Task StartServer() => environment = await WorkflowEnvironment.StartLocalAsync();

    [OneTimeTearDown]
    public async Task StopServer() => await environment.DisposeAsync();

    [Test]
    public async Task DrainAccumulatesCommittedBatchesAndStopsAtAnEmptyBatch()
    {
        var activities = new RefreshActivities([new(2, 4), new(1, 0)]);
        var result = await Run(activities, new(BatchSize: 3));

        result.Should().Be(new ArtistVenueRefreshRunResult(2, 3, 4, false));
        activities.BatchSizes.Should().Equal(3, 3, 3);
        activities.ReconciliationCalls.Should().Be(0);
    }

    [Test]
    public async Task ReconciliationRequestsAllOwnersBeforeRefreshing()
    {
        var activities = new RefreshActivities([new(1, 2)]);
        var result = await Run(activities, new(Reconcile: true));

        result.ArtistsRefreshed.Should().Be(1);
        activities.Events.Should().Equal("reconcile", "refresh", "refresh");
    }

    [Test]
    public async Task BatchLimitLeavesTheRemainingWorkForTheNextRun()
    {
        var activities = new RefreshActivities([new(1, 2), new(1, 3), new(1, 4)]);
        var first = await Run(activities, new(MaxBatches: 2));
        var second = await Run(activities, new());

        first.Should().Be(new ArtistVenueRefreshRunResult(2, 2, 5, true));
        second.Should().Be(new ArtistVenueRefreshRunResult(1, 1, 4, false));
    }

    [Test]
    public async Task FailedBatchRetriesWithoutCountingTheFailureAsCommittedWork()
    {
        var activities = new RefreshActivities([new(2, 3)]) { FailuresRemaining = 1 };
        var result = await Run(activities, new());

        result.Should().Be(new ArtistVenueRefreshRunResult(1, 2, 3, false));
        activities.BatchSizes.Should().HaveCount(3); // failed, successful, then empty
    }

    [Test]
    public async Task ExhaustedFailureLeavesTheBatchAvailableToALaterRun()
    {
        var activities = new RefreshActivities([new(1, 2)]) { FailuresRemaining = 3 };
        var failed = async () => await Run(activities, new());
        await failed.Should().ThrowAsync<WorkflowFailedException>();

        var recovered = await Run(activities, new());
        recovered.Should().Be(new ArtistVenueRefreshRunResult(1, 1, 2, false));
    }

    [TestCase(0, 1)]
    [TestCase(1, 0)]
    public async Task InvalidBoundsFailBeforeRequestingAnyWork(int batchSize, int maxBatches)
    {
        var activities = new RefreshActivities([]);
        var failed = async () => await Run(activities, new(true, batchSize, maxBatches));

        await failed.Should().ThrowAsync<WorkflowFailedException>();
        activities.Events.Should().BeEmpty();
    }

    [Test]
    public async Task ScheduleRegistrationPreservesOperatorPauseAndCanRunTheRefresh()
    {
        var client = new NormalizationClient(environment.Client);
        var ids = new[]
        {
            "relisten-norm-venues",
            "relisten-artist-venues-refresh",
            "relisten-artist-venues-reconcile",
        };
        await client.RegisterSchedulesAsync();
        try
        {
            foreach (var id in ids)
            {
                await environment.Client.GetScheduleHandle(id).PauseAsync("Test operator pause");
            }

            await client.RegisterSchedulesAsync();
            var refresh = environment.Client.GetScheduleHandle("relisten-artist-venues-refresh");
            (await refresh.DescribeAsync()).Schedule.State.Paused.Should().BeTrue();
            var activities = new RefreshActivities([new(1, 2)]);
            using var workflows = WorkflowWorker();
            using var database = DatabaseWorker(activities);
            await workflows.ExecuteAsync(() =>
                database.ExecuteAsync(async () =>
                {
                    await refresh.TriggerAsync();
                    var id = await activities.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    var result = await environment
                        .Client.GetWorkflowHandle(id)
                        .GetResultAsync<ArtistVenueRefreshRunResult>()
                        .WaitAsync(TimeSpan.FromSeconds(20));
                    result.ArtistsRefreshed.Should().Be(1);
                    activities.ReconciliationCalls.Should().Be(0);
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

    private async Task<ArtistVenueRefreshRunResult> Run(
        RefreshActivities activities,
        RefreshArtistVenues request
    )
    {
        using var workflows = WorkflowWorker();
        using var database = DatabaseWorker(activities);
        return await workflows.ExecuteAsync(() =>
            database.ExecuteAsync(() =>
                environment.Client.ExecuteWorkflowAsync(
                    (ArtistVenueRefreshWorkflow workflow) => workflow.RunAsync(request),
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

    private sealed class RefreshActivities(IEnumerable<ArtistVenueRefreshResult> batches)
    {
        private readonly Queue<ArtistVenueRefreshResult> pending = new(batches);
        public List<int> BatchSizes { get; } = [];
        public List<string> Events { get; } = [];
        public int ReconciliationCalls { get; private set; }
        public int FailuresRemaining { get; set; }
        public TaskCompletionSource<string> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Activity("ArtistVenues.RequestReconciliation")]
        public Task Reconcile()
        {
            Events.Add("reconcile");
            ReconciliationCalls++;
            return Task.CompletedTask;
        }

        [Activity("ArtistVenues.RefreshBatch")]
        public Task<ArtistVenueRefreshResult> Refresh(int batchSize)
        {
            Started.TrySetResult(ActivityExecutionContext.Current.Info.WorkflowId!);
            Events.Add("refresh");
            BatchSizes.Add(batchSize);
            if (FailuresRemaining-- > 0)
            {
                throw new InvalidOperationException("Synthetic database failure");
            }
            return Task.FromResult(pending.Count == 0 ? new(0, 0) : pending.Dequeue());
        }
    }
}
