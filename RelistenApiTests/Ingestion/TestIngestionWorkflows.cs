using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Relisten.Api.Models;
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
    public async Task FullImportCommitsSuccessfulItemsAndReportsFailures()
    {
        var activities = new RecordingActivities(3, failedIndex: 1);
        var result = await Run(activities, false);
        result.Stats.Created.Should().Be(2);
        result.FailedItems.Should().Be(1);
        activities.Applied.Should().Equal(0, 2);
        activities.Finished.Should().BeTrue();
    }

    [Test]
    public async Task ManualItemUsesTheSameActivityComposition()
    {
        var activities = new RecordingActivities(1);
        var result = await Run(activities, true);
        result.Stats.Created.Should().Be(1);
        activities.Discoveries.Should().Be(0);
        activities.Reconciliations.Should().Be(0);
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
        activities.Preparations.Should().Be(1);
    }

    [Test]
    public async Task RetryDoesNotApplyAnItemUntilItsDependenciesSucceed()
    {
        var activities = new RecordingActivities(1) { FailFirstFetch = true };
        var result = await Run(activities, false);
        result.FailedItems.Should().Be(0);
        activities.Applied.Should().Equal(0);
        activities.Attempts.Should().Equal(0, 0);
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
                await activities.FetchCanceled.Task.WaitAsync(TimeSpan.FromSeconds(20));
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
                            == "Ingestion.PrepareProvider"
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

    [Test]
    public async Task FailedPrerequisiteStopsBeforeDiscoveryAndReconciliation()
    {
        var activities = new RecordingActivities(3) { FailPreparation = true };
        Func<Task> run = async () => await Run(activities, false);
        await run.Should().ThrowAsync<WorkflowFailedException>();
        activities.Discoveries.Should().Be(0);
        activities.Applied.Should().BeEmpty();
        activities.Reconciliations.Should().Be(0);
        activities.Finished.Should().BeFalse();
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
                        (ReimportItemWorkflow w) => w.RunAsync(request),
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
            new TemporalWorkerOptions(
                IngestionQueues.Providers[UpstreamProvider.ArchiveOrg]
            ).AddAllActivities(activities)
        );
        return await workflowWorker.ExecuteAsync(() =>
            dbWorker.ExecuteAsync(() => fetchWorker.ExecuteAsync(run))
        );
    }

    // These activities model provider behavior at the actual Temporal activity boundary.
    public class RecordingActivities(int count, int? failedIndex = null) : IIngestionActivities
    {
        public List<int> Attempts { get; } = new();
        public List<int> Applied { get; } = new();
        public int Preparations { get; private set; }
        public int Discoveries { get; private set; }
        public int Reconciliations { get; private set; }
        public bool Finished { get; private set; }
        public bool FailFirstFetch { get; set; }
        public bool FailPreparation { get; init; }
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
        public TaskCompletionSource FetchCanceled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFetch { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Activity("Ingestion.ListArtists")]
        public Task<ArtistBatchPage> ListArtists(ImportBatchRequest request) =>
            Task.FromResult(
                new ArtistBatchPage(
                    Enumerable
                        .Range(0, BatchArtists)
                        .Select(index => request.RequestId + "/" + index)
                        .ToArray(),
                    null
                )
            );

        [Activity("Ingestion.PrepareArtist")]
        public Task<ArtistImportPlan> PrepareArtist(ArtistImportRequest request)
        {
            lock (sync)
            {
                Preparations++;
                if (Preparations == 11)
                {
                    EleventhArtistPrepared.TrySetResult();
                }
            }

            return Task.FromResult(new ArtistImportPlan("artist", [UpstreamProvider.ArchiveOrg]));
        }

        [Activity("Ingestion.PrepareProvider")]
        public Task<ImportStats> PrepareProvider(
            ArtistImportRequest request,
            UpstreamProvider provider
        )
        {
            if (FailPreparation)
            {
                throw new ApplicationFailureException(
                    "Required catalog unavailable",
                    nonRetryable: true
                );
            }

            return Task.FromResult(new ImportStats());
        }

        [Activity("Ingestion.DiscoverItems")]
        public Task<ImportPage> DiscoverItems(
            ArtistImportRequest request,
            UpstreamProvider provider,
            DiscoveryCursor? cursor
        )
        {
            lock (sync)
            {
                Discoveries++;
            }

            var offset = cursor?.Offset ?? 0;
            var items = Enumerable
                .Range(offset, Math.Min(40, count - offset))
                .Select(index => new ImportItem(index.ToString(), "fixture", index))
                .ToArray();
            var next =
                offset + items.Length < count
                    ? new DiscoveryCursor(Offset: offset + items.Length)
                    : null;
            return Task.FromResult(new ImportPage(items, next));
        }

        [Activity("Ingestion.ImportItem")]
        public async Task<ItemImportResult> ImportItem(
            ArtistImportRequest request,
            UpstreamProvider provider,
            ImportItem? item
        )
        {
            var index = item?.Index ?? 0;
            lock (sync)
            {
                Attempts.Add(index);
                MaxActiveFetches = Math.Max(MaxActiveFetches, ++activeFetches);
            }

            try
            {
                var activity = ActivityExecutionContext.Current;
                using var registration = activity.CancellationToken.Register(() =>
                    FetchCanceled.TrySetResult()
                );
                FetchEntered.TrySetResult();
                if (HoldFetch || (HoldFirstArtist && request.Artist.EndsWith("/0")))
                {
                    while (!ReleaseFetch.Task.IsCompleted)
                    {
                        activity.Heartbeat();
                        await Task.WhenAny(ReleaseFetch.Task, Task.Delay(50));
                        activity.CancellationToken.ThrowIfCancellationRequested();
                    }
                }

                if (FetchDelay)
                {
                    await Task.Delay(100);
                }

                if (FailFirstFetch)
                {
                    FailFirstFetch = false;
                    throw new ApplicationFailureException("Temporary upstream error");
                }

                if (index == failedIndex)
                {
                    throw new ApplicationFailureException(
                        "Invalid provider metadata",
                        nonRetryable: true
                    );
                }

                lock (sync)
                {
                    Applied.Add(index);
                }

                return new ItemImportResult(new ImportStats { Created = 1 });
            }
            finally
            {
                lock (sync)
                {
                    activeFetches--;
                }
            }
        }

        [Activity("Ingestion.ReconcileProvider")]
        public Task<ProviderResult> ReconcileProvider(
            ArtistImportRequest request,
            UpstreamProvider provider,
            string[] ineligibleItems,
            bool hasFailures
        )
        {
            Reconciliations++;
            return Task.FromResult(new ProviderResult(new ImportStats()));
        }

        [Activity("Ingestion.FinishArtist")]
        public Task<ImportResult> FinishArtist(ArtistImportRequest request, ImportResult result)
        {
            Finished = true;
            return Task.FromResult(result);
        }
    }
}
