using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Relisten.Import;
using Relisten.Ingestion;
using Relisten.Ingestion.Workflows;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace RelistenApiTests.Ingestion;

[TestFixture, NonParallelizable]
public class TestIngestionWorkflows
{
    private WorkflowEnvironment environment = null!;

    [OneTimeSetUp]
    public async Task StartServer() => environment = await WorkflowEnvironment.StartLocalAsync();

    [OneTimeTearDown]
    public async Task StopServer() => await environment.DisposeAsync();

    [Test]
    public async Task FullImportStagesDependenciesBeforeApplyingAndReportsFailures()
    {
        var activities = new RecordingActivities(3, failedIndex: 1);
        var result = await Run(activities, false);
        result.Stats.Created.Should().Be(2);
        result.FailedItems.Should().Be(1);
        activities.Applied.Should().Equal(0, 2);
        activities.Failures.Should().Equal(1);
        activities.Finished.Should().BeTrue();
    }

    [Test]
    public async Task ManualItemUsesTheSameActivityComposition()
    {
        var activities = new RecordingActivities(1);
        var result = await Run(activities, true);
        result.Stats.Created.Should().Be(1);
        activities.Fetches.Should().Equal((0, 0), (0, 1));
        activities.Applied.Should().Equal(0);
        activities.Finished.Should().BeTrue();
    }

    [Test]
    public async Task ContinueAsNewResumesAfterTheLastCompletedItem()
    {
        var activities = new RecordingActivities(180);
        var result = await Run(activities, false);
        result.Stats.Created.Should().Be(180);
        activities.Applied.Should().Equal(Enumerable.Range(0, 180));
        activities.Preparations.Should().BeGreaterThan(1);
    }

    [Test]
    public async Task RetryDoesNotApplyAnItemUntilItsDependenciesSucceed()
    {
        var activities = new RecordingActivities(1) { FailFirstFetch = true };
        var result = await Run(activities, false);
        result.FailedItems.Should().Be(0);
        activities.Applied.Should().Equal(0);
        activities.Fetches.Should().Equal((0, 0), (0, 0), (0, 1));
    }

    [Test]
    public async Task CancelWaitsForTheActiveOperationAndPreventsTheNextApply()
    {
        var activities = new RecordingActivities(1) { HoldFetch = true };
        await WithWorkers(
            activities,
            async () =>
            {
                var request = new ArtistImportRequest(
                    Guid.NewGuid().ToString(),
                    Guid.NewGuid().ToString()
                );
                var handle = await environment.Client.StartWorkflowAsync(
                    (ArtistRefreshWorkflow w) => w.RunAsync(request, null),
                    new WorkflowOptions("cancel/" + request.RequestId, IngestionQueues.Workflows)
                );
                await activities.FetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await handle.CancelAsync();
                activities.ReleaseFetch.SetResult();
                Func<Task> finish = async () => await handle.GetResultAsync();
                var failure = await finish.Should().ThrowAsync<WorkflowFailedException>();
                failure.Which.InnerException.Should().BeOfType<CanceledFailureException>();
                activities.Applied.Should().BeEmpty();
                activities.Finished.Should().BeFalse();
                return true;
            }
        );
    }

    [Test]
    public async Task TwoManualStartsForOneArtistShareTheRunningWorkflow()
    {
        var activities = new RecordingActivities(1) { HoldFetch = true };
        await WithWorkers(
            activities,
            async () =>
            {
                var admission = new TemporalIngestionClient(
                    environment.Client,
                    new ConfigurationBuilder().Build()
                );
                var first = new ArtistImportRequest(
                    Guid.NewGuid().ToString(),
                    Guid.NewGuid().ToString()
                );
                var id = await admission.StartArtistAsync(first);
                await activities.FetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                (
                    await admission.StartArtistAsync(
                        first with
                        {
                            RequestId = Guid.NewGuid().ToString(),
                        }
                    )
                )
                    .Should()
                    .Be(id);
                activities.ReleaseFetch.SetResult();
                var result = await environment
                    .Client.GetWorkflowHandle<ArtistRefreshWorkflow, ImportResult>(id)
                    .GetResultAsync();
                result.RequestId.Should().Be(first.RequestId);
                activities.Applied.Should().Equal(0);
                return true;
            }
        );
    }

    [Test]
    public async Task BatchProcessesEveryArtistWithAtMostTenActiveChildren()
    {
        var activities = new RecordingActivities(1)
        {
            BatchArtists = 23,
            FetchDelay = true,
            HoldFirstArtist = true,
        };
        await WithWorkers(
            activities,
            async () =>
            {
                var request = new ImportBatchRequest("full", Guid.NewGuid().ToString());
                var handle = await environment.Client.StartWorkflowAsync(
                    (RefreshBatchWorkflow w) => w.RunAsync(request),
                    new WorkflowOptions("batch/" + request.RequestId, IngestionQueues.Workflows)
                );
                // Artist zero remains blocked while later artists replace completed ones.
                await activities.EleventhArtistPrepared.Task.WaitAsync(TimeSpan.FromSeconds(20));
                activities.ReleaseFetch.SetResult();
                var result = await handle.GetResultAsync();
                result.Should().Be(new ImportBatchResult(23, 0));
                var replayer = new WorkflowReplayer(
                    new WorkflowReplayerOptions().AddWorkflow<RefreshBatchWorkflow>()
                );
                await replayer.ReplayWorkflowAsync(await handle.FetchHistoryAsync());
                activities.MaxActiveFetches.Should().BeInRange(2, 10);
                return true;
            }
        );
    }

    [Test]
    public async Task WorkerRestartContinuesAnImportFromItsPendingActivity()
    {
        var request = new ArtistImportRequest("artist", Guid.NewGuid().ToString());
        var options = new WorkflowOptions(
            "restart/" + request.RequestId,
            IngestionQueues.Workflows
        );
        var handle = await environment.Client.StartWorkflowAsync(
            (ArtistRefreshWorkflow w) => w.RunAsync(request, null),
            options
        );
        // The first worker resolves the artist, but has no provider poller.
        // Pending discovery must survive replacing that worker process.
        var first = new RecordingActivities(2);
        using (
            var workflowWorker = new TemporalWorker(
                environment.Client,
                new TemporalWorkerOptions(
                    IngestionQueues.Workflows
                ).AddWorkflow<ArtistRefreshWorkflow>()
            )
        )
        using (
            var dbWorker = new TemporalWorker(
                environment.Client,
                new TemporalWorkerOptions(IngestionQueues.Database).AddAllActivities(first)
            )
        )
        {
            await workflowWorker.ExecuteAsync(() =>
                dbWorker.ExecuteAsync(async () =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    while (
                        !(await handle.FetchHistoryAsync()).Events.Any(e =>
                            e.ActivityTaskScheduledEventAttributes?.ActivityType.Name
                            == "Ingestion.Discover"
                        )
                    )
                        await Task.Delay(25, timeout.Token);
                })
            );
        }
        var restarted = new RecordingActivities(2);
        await WithWorkers(
            restarted,
            async () =>
            {
                var result = await handle.GetResultAsync();
                result.Stats.Created.Should().Be(2);
                restarted.Applied.Should().Equal(0, 1);
                restarted.Preparations.Should().Be(0);
                return true;
            }
        );
    }

    private async Task<ImportResult> Run(RecordingActivities activities, bool singleItem)
    {
        var request = new ArtistImportRequest(
            "artist",
            Guid.NewGuid().ToString(),
            Item: singleItem ? "one" : null
        );
        return await WithWorkers(
            activities,
            async () =>
            {
                var options = new WorkflowOptions(
                    "test/" + request.RequestId,
                    IngestionQueues.Workflows
                );
                var result = singleItem
                    ? await environment.Client.ExecuteWorkflowAsync(
                        (ReimportItemWorkflow w) => w.RunAsync(request, null),
                        options
                    )
                    : await environment.Client.ExecuteWorkflowAsync(
                        (ArtistRefreshWorkflow w) => w.RunAsync(request, null),
                        options
                    );
                var history = await environment
                    .Client.GetWorkflowHandle(options.Id!)
                    .FetchHistoryAsync();
                var replayer = new WorkflowReplayer(
                    new WorkflowReplayerOptions()
                        .AddWorkflow<ArtistRefreshWorkflow>()
                        .AddWorkflow<ReimportItemWorkflow>()
                );
                await replayer.ReplayWorkflowAsync(history);
                return result;
            }
        );
    }

    private async Task<T> WithWorkers<T>(RecordingActivities activities, Func<Task<T>> run)
    {
        using var workflowWorker = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Workflows)
                .AddWorkflow<ArtistRefreshWorkflow>()
                .AddWorkflow<ReimportItemWorkflow>()
                .AddWorkflow<RefreshBatchWorkflow>()
        );
        using var dbWorker = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Database).AddAllActivities(activities)
        );
        using var fetchWorker = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Providers["archive.org"]).AddAllActivities(
                activities
            )
        );
        return await workflowWorker.ExecuteAsync(() =>
            dbWorker.ExecuteAsync(() => fetchWorker.ExecuteAsync(run))
        );
    }

    // These activities model observable provider behavior. They do not mock workflow internals.
    public class RecordingActivities(int count, int? failedIndex = null) : IIngestionActivities
    {
        public List<(int, int)> Fetches { get; } = new();
        public List<int> Applied { get; } = new();
        public List<int> Failures { get; } = new();
        public int Preparations { get; private set; }
        public bool Finished { get; private set; }
        public bool FailFirstFetch { get; set; }
        public bool HoldFetch { get; init; }
        public bool FetchDelay { get; init; }
        public bool HoldFirstArtist { get; init; }
        public int BatchArtists { get; init; }
        public int MaxActiveFetches { get; private set; }
        private int activeFetches;
        private readonly object sync = new();
        public TaskCompletionSource EleventhArtistPrepared { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FetchEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFetch { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Activity("Ingestion.ListArtists")]
        public Task<ArtistBatchPage> ListArtists(ImportBatchRequest request) =>
            Task.FromResult(
                new ArtistBatchPage(
                    Enumerable
                        .Range(0, BatchArtists)
                        .Select(i => request.RequestId + "/" + i)
                        .ToArray(),
                    null
                )
            );

        [Activity("Ingestion.Prepare")]
        public Task<ArtistImportPlan> Prepare(ArtistImportRequest request)
        {
            lock (sync)
            {
                Preparations++;
                if (Preparations == 11)
                    EleventhArtistPrepared.TrySetResult();
            }
            return Task.FromResult(new ArtistImportPlan("artist", ["archive.org"]));
        }

        [Activity("Ingestion.Discover")]
        public Task<ImportPage> Discover(
            ArtistImportRequest request,
            string provider,
            string? cursor
        ) => Task.FromResult(new ImportPage("page", count, null));

        [Activity("Ingestion.Fetch")]
        public async Task<bool> Fetch(ImportItemRef item, int part)
        {
            lock (sync)
            {
                Fetches.Add((item.Index, part));
                MaxActiveFetches = Math.Max(MaxActiveFetches, ++activeFetches);
            }
            try
            {
                FetchEntered.TrySetResult();
                if (HoldFetch || (HoldFirstArtist && item.Request.Artist.EndsWith("/0")))
                    await ReleaseFetch.Task;
                if (FetchDelay)
                    await Task.Delay(100);
                if (FailFirstFetch)
                {
                    FailFirstFetch = false;
                    throw new ApplicationFailureException("Temporary upstream error");
                }
                if (item.Index == failedIndex)
                    throw new ApplicationFailureException(
                        "Invalid provider metadata",
                        nonRetryable: true
                    );
                return part == 1;
            }
            finally
            {
                lock (sync)
                    activeFetches--;
            }
        }

        [Activity("Ingestion.Apply")]
        public Task<ImportStats> Apply(ImportItemRef item)
        {
            lock (sync)
            {
                Fetches.Should().Contain((item.Index, 1));
                Applied.Add(item.Index);
            }
            return Task.FromResult(new ImportStats { Created = 1 });
        }

        [Activity("Ingestion.RecordFailure")]
        public Task RecordFailure(ImportItemRef item, string reason)
        {
            Failures.Add(item.Index);
            return Task.CompletedTask;
        }

        [Activity("Ingestion.Candidates")]
        public Task<string[]> Candidates(ArtistImportRequest request, string provider) =>
            Task.FromResult(Array.Empty<string>());

        [Activity("Ingestion.Verify")]
        public Task Verify(ArtistImportRequest request, string provider, string key) =>
            Task.CompletedTask;

        [Activity("Ingestion.FinishProvider")]
        public Task<ImportStats> FinishProvider(ArtistImportRequest request, string provider) =>
            Task.FromResult(new ImportStats());

        [Activity("Ingestion.FinishArtist")]
        public Task<ImportResult> FinishArtist(ArtistImportRequest request, ImportResult result)
        {
            Finished = true;
            return Task.FromResult(result);
        }
    }
}
