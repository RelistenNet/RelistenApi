using FluentAssertions;
using Relisten.Ingestion;
using Relisten.Ingestion.Workflows;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace RelistenApiTests.Ingestion;

/// <summary>Exercises batch rollover at the artist child contract without performing provider I/O.</summary>
[TestFixture, NonParallelizable]
public class TestBatchContinuation
{
    [Test]
    public async Task RolloverDrainsChildrenAndRetainsSelectionAndAllOutcomeCounts()
    {
        await using var environment = await WorkflowEnvironment.StartLocalAsync();
        var selection = new Selection();
        using var workflows = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Workflows)
                .AddWorkflow<RefreshBatchWorkflow>()
                .AddWorkflow<ArtistOutcomeWorkflow>()
        );
        using var database = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Database).AddAllActivities(selection)
        );
        await workflows.ExecuteAsync(() =>
            database.ExecuteAsync(async () =>
            {
                var handle = await environment.Client.StartWorkflowAsync(
                    (RefreshBatchWorkflow workflow) =>
                        workflow.RunAsync(new RefreshCatalog(), null, null),
                    new WorkflowOptions(
                        "batch-rollover/" + Guid.NewGuid(),
                        IngestionQueues.Workflows
                    )
                );
                var result = await handle.GetResultAsync();
                result.Should().Be(new ImportBatchResult(249, 1, 1, 0));
                selection.Calls.Should().Be(1);

                var history = await environment
                    .Client.GetWorkflowHandle(handle.Id, handle.ResultRunId)
                    .FetchHistoryAsync();
                var started = 0;
                var closed = 0;
                var maximumActive = 0;
                foreach (var entry in history.Events)
                {
                    if (entry.ChildWorkflowExecutionStartedEventAttributes != null)
                        started++;
                    if (
                        entry.ChildWorkflowExecutionCompletedEventAttributes != null
                        || entry.ChildWorkflowExecutionFailedEventAttributes != null
                    )
                        closed++;
                    maximumActive = Math.Max(maximumActive, started - closed);
                    if (entry.WorkflowExecutionContinuedAsNewEventAttributes != null)
                        closed
                            .Should()
                            .Be(started, "a continuation must have no live artist children");
                }
                started.Should().Be(250);
                closed.Should().Be(250);
                maximumActive.Should().BeInRange(1, 10);
                history
                    .Events.Should()
                    .Contain(entry => entry.WorkflowExecutionContinuedAsNewEventAttributes != null);
                await new WorkflowReplayer(
                    new WorkflowReplayerOptions().AddWorkflow<RefreshBatchWorkflow>()
                ).ReplayWorkflowAsync(history);
            })
        );
    }

    [Test]
    public async Task CancellationWaitsForEveryOwnedChildToFinishCanceling()
    {
        await using var environment = await WorkflowEnvironment.StartLocalAsync();
        var selection = new Selection { Count = 2 };
        var blocking = new BlockingActivities();
        using var workflows = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Workflows)
                .AddWorkflow<RefreshBatchWorkflow>()
                .AddWorkflow<BlockingArtistWorkflow>()
        );
        using var database = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Database)
                .AddAllActivities(selection)
                .AddAllActivities(blocking)
        );
        await workflows.ExecuteAsync(() =>
            database.ExecuteAsync(async () =>
            {
                var handle = await environment.Client.StartWorkflowAsync(
                    (RefreshBatchWorkflow workflow) =>
                        workflow.RunAsync(new RefreshCatalog(), null, null),
                    new WorkflowOptions("batch-cancel/" + Guid.NewGuid(), IngestionQueues.Workflows)
                );
                await Task.WhenAll(blocking.FastEntered.Task, blocking.SlowEntered.Task)
                    .WaitAsync(TimeSpan.FromSeconds(20));
                await handle.CancelAsync();
                await Task.WhenAll(blocking.FastCanceled.Task, blocking.SlowCanceled.Task)
                    .WaitAsync(TimeSpan.FromSeconds(20));
                try
                {
                    var fast = environment.Client.GetWorkflowHandle(
                        "ingest/artist/00000000-0000-0000-0000-000000000001"
                    );
                    Func<Task> fastResult = async () =>
                        await fast.GetResultAsync<ArtistImportResult>();
                    await fastResult.Should().ThrowAsync<WorkflowFailedException>();

                    // The slow activity has acknowledged cancellation but has not released
                    // its resources. A finished sibling must not let the batch close yet.
                    var result = handle.GetResultAsync();
                    (await Task.WhenAny(result, Task.Delay(TimeSpan.FromSeconds(1))))
                        .Should()
                        .NotBe(result);
                    (await handle.DescribeAsync())
                        .Status.Should()
                        .Be(Temporalio.Api.Enums.V1.WorkflowExecutionStatus.Running);
                }
                finally
                {
                    blocking.ReleaseSlowCancellation.TrySetResult();
                }
                Func<Task> canceled = async () => await handle.GetResultAsync();
                var failure = await canceled.Should().ThrowAsync<WorkflowFailedException>();
                TemporalException
                    .IsCanceledException(failure.Which.InnerException!)
                    .Should()
                    .BeTrue();
                blocking.SlowExited.Task.IsCompleted.Should().BeTrue();
            })
        );
    }

    public class BlockingActivities
    {
        public TaskCompletionSource FastEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SlowEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FastCanceled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SlowCanceled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SlowExited { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSlowCancellation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Activity("Tests.HoldArtist")]
        public async Task HoldArtist(ArtistId artist)
        {
            var slow = artist.Value == Guid.Parse("00000000-0000-0000-0000-000000000002");
            (slow ? SlowEntered : FastEntered).TrySetResult();
            var activity = ActivityExecutionContext.Current;
            try
            {
                while (!activity.CancellationToken.IsCancellationRequested)
                {
                    activity.Heartbeat();
                    await Task.Delay(25);
                }
                (slow ? SlowCanceled : FastCanceled).TrySetResult();
                if (slow)
                    await ReleaseSlowCancellation.Task;
                activity.CancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                if (slow)
                    SlowExited.TrySetResult();
            }
        }
    }

    [Workflow("ArtistRefreshWorkflow")]
    public class BlockingArtistWorkflow
    {
        [WorkflowRun]
        public async Task<ArtistImportResult> RunAsync(
            ArtistRefreshCommand request,
            IngestionExecution? execution = null
        )
        {
            await Workflow.ExecuteActivityAsync(
                (BlockingActivities activities) => activities.HoldArtist(request.Artist),
                new ActivityOptions
                {
                    TaskQueue = IngestionQueues.Database,
                    StartToCloseTimeout = TimeSpan.FromMinutes(1),
                    HeartbeatTimeout = TimeSpan.FromSeconds(2),
                    CancellationType = ActivityCancellationType.WaitCancellationCompleted,
                }
            );
            return new(request.Artist, 1, 0, 0);
        }
    }

    public class Selection
    {
        public int Calls { get; private set; }
        public int Count { get; init; } = 251;

        [Activity("Ingestion.SelectArtists")]
        public ArtistId[] SelectArtists(CatalogBatchCommand command, IngestionExecution execution)
        {
            Calls++;
            return Enumerable
                .Range(1, Count)
                .Select(index => new ArtistId(
                    Guid.Parse($"00000000-0000-0000-0000-{index:000000000000}")
                ))
                .ToArray();
        }
    }

    // The batch owns scheduling and counting. This child deliberately supplies only
    // the artist result contract so the test can force a full batch rollover cheaply.
    [Workflow("ArtistRefreshWorkflow")]
    public class ArtistOutcomeWorkflow
    {
        [WorkflowRun]
        public Task<ArtistImportResult> RunAsync(
            ArtistRefreshCommand request,
            IngestionExecution? execution = null
        )
        {
            if (request.Artist.Value == Guid.Parse("00000000-0000-0000-0000-000000000001"))
                return Task.FromResult(new ArtistImportResult(request.Artist, 1, 0, 1));
            if (request.Artist.Value == Guid.Parse("00000000-0000-0000-0000-000000000002"))
                throw new ApplicationFailureException(
                    "Required catalog unavailable",
                    "UpstreamUnavailable",
                    nonRetryable: true
                );
            return Task.FromResult(new ArtistImportResult(request.Artist, 1, 0, 0));
        }
    }
}
