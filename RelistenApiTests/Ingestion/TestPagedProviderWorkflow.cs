using FluentAssertions;
using Relisten.Api.Models;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.SetlistFm;
using Relisten.Vendor.SetlistFm;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace RelistenApiTests.Ingestion;

/// <summary>Verifies page-boundary continuation, partial failure, and cursor behavior through a concrete provider.</summary>
[TestFixture, NonParallelizable]
public class TestPagedProviderWorkflow
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task RolloverFinishesTheCurrentPageAndEmptyPagesDoNotEndEnumeration(bool failItem)
    {
        await using var environment = await WorkflowEnvironment.StartLocalAsync();
        var activities = new PagedActivities(failItem, holdFirstItem: true);
        using var workflows = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Workflows).AddWorkflow<SetlistFmWorkflow>()
        );
        using var provider = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(
                IngestionQueues.Providers[UpstreamProvider.SetlistFm]
            ).AddAllActivities(activities)
        );
        await workflows.ExecuteAsync(() =>
            provider.ExecuteAsync(async () =>
            {
                var input = new ProviderRequest<RefreshSetlistFmArtist>(
                    new(new ArtistId(Guid.NewGuid())),
                    new(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddHours(1))
                );
                var handle = await environment.Client.StartWorkflowAsync(
                    (SetlistFmWorkflow workflow) => workflow.RunAsync(input, null),
                    new WorkflowOptions("pages/" + Guid.NewGuid(), IngestionQueues.Workflows)
                );
                // Keep the first item pending while later slots are reused. The
                // page must finish before the workflow can continue to page two.
                await activities.LastPageItemEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                activities.MaxActive.Should().BeInRange(2, 10);
                activities.ReadPages.Should().Equal(1);
                activities.FinalizationCalls.Should().Be(0);
                activities.ReleaseFirstItem.TrySetResult();
                var result = await handle.GetResultAsync();

                result.CompletedItems.Should().Be(failItem ? 121 : 122);
                result.FailedItems.Should().Be(failItem ? 1 : 0);
                activities.ReadPages.Should().Equal(1, 2, 3);
                activities.FinalizationCalls.Should().Be(1);
                activities.ActiveDuringFinalization.Should().Be(0);
                activities.PagesAtFinalization.Should().Equal(1, 2, 3);
                activities.Imported.Should().BeEquivalentTo(Enumerable.Range(0, 122));
                var firstHistory = await environment
                    .Client.GetWorkflowHandle(handle.Id, handle.ResultRunId)
                    .FetchHistoryAsync();
                firstHistory
                    .Events.Should()
                    .Contain(e => e.WorkflowExecutionContinuedAsNewEventAttributes != null);
                // The first run finishes the entire 120-item page before continuing.
                firstHistory
                    .Events.Count(e =>
                        e.ActivityTaskScheduledEventAttributes?.ActivityType.Name
                        == "SetlistFm.ImportSetlist"
                    )
                    .Should()
                    .Be(120);
                await new WorkflowReplayer(
                    new WorkflowReplayerOptions().AddWorkflow<SetlistFmWorkflow>()
                ).ReplayWorkflowAsync(firstHistory);
            })
        );
    }

    [Test]
    public async Task FailedListingAfterRolloverDoesNotFinalizeTourDates()
    {
        await using var environment = await WorkflowEnvironment.StartLocalAsync();
        var activities = new PagedActivities(false, failListing: true);
        using var workflows = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Workflows).AddWorkflow<SetlistFmWorkflow>()
        );
        using var provider = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(
                IngestionQueues.Providers[UpstreamProvider.SetlistFm]
            ).AddAllActivities(activities)
        );

        await workflows.ExecuteAsync(() =>
            provider.ExecuteAsync(async () =>
            {
                var input = new ProviderRequest<RefreshSetlistFmArtist>(
                    new(new ArtistId(Guid.NewGuid())),
                    new(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddHours(1))
                );
                var handle = await environment.Client.StartWorkflowAsync(
                    (SetlistFmWorkflow workflow) => workflow.RunAsync(input, null),
                    new WorkflowOptions("failed-page/" + Guid.NewGuid(), IngestionQueues.Workflows)
                );

                Func<Task> result = async () => await handle.GetResultAsync();
                await result.Should().ThrowAsync<WorkflowFailedException>();

                activities.ReadPages.Should().Equal(1, 2);
                activities.Imported.Should().BeEquivalentTo(Enumerable.Range(0, 120));
                activities.FinalizationCalls.Should().Be(0);
            })
        );
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CancellationOrExpiryDrainsStartedItemsAndStopsFurtherAdmission(
        bool expireItem
    )
    {
        await using var environment = await WorkflowEnvironment.StartLocalAsync();
        var activities = new PagedActivities(false, holdAllItems: true, expireItem: expireItem);
        using var workflows = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Workflows).AddWorkflow<SetlistFmWorkflow>()
        );
        using var provider = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(
                IngestionQueues.Providers[UpstreamProvider.SetlistFm]
            ).AddAllActivities(activities)
        );

        await workflows.ExecuteAsync(() =>
            provider.ExecuteAsync(async () =>
            {
                var input = new ProviderRequest<RefreshSetlistFmArtist>(
                    new(new ArtistId(Guid.NewGuid())),
                    new(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddHours(1))
                );
                var handle = await environment.Client.StartWorkflowAsync(
                    (SetlistFmWorkflow workflow) => workflow.RunAsync(input, null),
                    new WorkflowOptions("cancel-page/" + Guid.NewGuid(), IngestionQueues.Workflows)
                );
                await activities.FirstWindowEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                if (expireItem)
                {
                    activities.ReleaseExpiration.TrySetResult();
                    await activities.ExpirationReported.Task.WaitAsync(TimeSpan.FromSeconds(20));
                }
                else
                {
                    await handle.CancelAsync();
                    await activities.AllItemsCanceled.Task.WaitAsync(TimeSpan.FromSeconds(20));
                }

                // Expiry leaves siblings running; cancellation deliberately holds
                // one acknowledgement. Neither may close the provider prematurely.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var history = await handle.FetchHistoryAsync();
                while (true)
                {
                    var settledItem = history.Events.FirstOrDefault(e =>
                        e.ActivityTaskCanceledEventAttributes != null
                        || e.ActivityTaskFailedEventAttributes != null
                    );
                    if (
                        settledItem != null
                        && history.Events.Any(e =>
                            e.WorkflowTaskCompletedEventAttributes != null
                            && e.EventId > settledItem.EventId
                        )
                    )
                    {
                        break;
                    }

                    await Task.Delay(20, timeout.Token);
                    history = await handle.FetchHistoryAsync();
                }
                history
                    .Events.Should()
                    .NotContain(e =>
                        e.WorkflowExecutionCanceledEventAttributes != null
                        || e.WorkflowExecutionFailedEventAttributes != null
                    );
                activities.ReleaseFirstItem.TrySetResult();
                activities.ReleaseCancellation.TrySetResult();

                Func<Task> result = async () => await handle.GetResultAsync();
                var failure = await result.Should().ThrowAsync<WorkflowFailedException>();
                if (expireItem)
                {
                    failure
                        .Which.InnerException.Should()
                        .BeOfType<ActivityFailureException>()
                        .Which.InnerException.Should()
                        .BeOfType<ApplicationFailureException>()
                        .Which.ErrorType.Should()
                        .Be("ImportExpired");
                }
                else
                {
                    TemporalException
                        .IsCanceledException(failure.Which.InnerException!)
                        .Should()
                        .BeTrue(failure.Which.ToString());
                }
                activities.ReadPages.Should().Equal(1);
                activities.Imported.Should().BeEquivalentTo(Enumerable.Range(0, 10));
                activities.MaxActive.Should().Be(10);
                activities.Active.Should().Be(0);
                activities.FinalizationCalls.Should().Be(0);
            })
        );
    }

    public class PagedActivities(
        bool failItem,
        bool holdFirstItem = false,
        bool holdAllItems = false,
        bool expireItem = false,
        bool failListing = false
    )
    {
        public List<int> ReadPages { get; } = [];
        public List<int> Imported { get; } = [];
        public int FinalizationCalls { get; private set; }
        public int ActiveDuringFinalization { get; private set; }
        public int[] PagesAtFinalization { get; private set; } = [];
        private readonly object sync = new();
        public int Active { get; private set; }
        public int MaxActive { get; private set; }
        private int canceled;
        public TaskCompletionSource ReleaseExpiration { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ExpirationReported { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstWindowEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LastPageItemEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstItem { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllItemsCanceled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCancellation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Activity("SetlistFm.ReadSetlistPage")]
        public Page<DiscoveredItem<SetlistFmId, Setlist>, SetlistFmPage> ReadSetlistPageAsync(
            ProviderWorkflowContext context,
            SetlistFmPage? cursor
        )
        {
            var page = cursor?.Number ?? 1;
            ReadPages.Add(page);
            if (failListing && page == 2)
            {
                throw new ApplicationFailureException(
                    "Fixture listing is unavailable.",
                    "UnavailableFixturePage",
                    nonRetryable: true
                );
            }

            return page switch
            {
                1 => new(Items(0, 120), new(2)),
                2 => new([], new(3)),
                3 => new(Items(120, 2), null),
                _ => throw new InvalidOperationException("Unexpected cursor"),
            };
        }

        [Activity("SetlistFm.RefreshTourDates")]
        public int RefreshTourDatesAsync(ProviderWorkflowContext context)
        {
            FinalizationCalls++;
            ActiveDuringFinalization = Active;
            PagesAtFinalization = ReadPages.ToArray();
            return 0;
        }

        [Activity("SetlistFm.ImportSetlist")]
        public async Task<SetlistFmItemResult> ImportSetlistAsync(
            ProviderWorkflowContext context,
            DiscoveredItem<SetlistFmId, Setlist> item
        )
        {
            var index = int.Parse(item.Id.Value);
            lock (sync)
            {
                Imported.Add(index);
                MaxActive = Math.Max(MaxActive, ++Active);
                if (Active == 10)
                {
                    FirstWindowEntered.TrySetResult();
                }
                if (index == 119)
                {
                    LastPageItemEntered.TrySetResult();
                }
            }

            try
            {
                var activity = ActivityExecutionContext.Current;
                if (expireItem && index == 0)
                {
                    await ReleaseExpiration.Task;
                    ExpirationReported.TrySetResult();
                    throw new ApplicationFailureException(
                        "Import expired.",
                        "ImportExpired",
                        nonRetryable: true
                    );
                }

                if (holdAllItems || holdFirstItem && index == 0)
                {
                    try
                    {
                        while (!ReleaseFirstItem.Task.IsCompleted)
                        {
                            activity.Heartbeat();
                            await Task.Delay(50, activity.CancellationToken);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        lock (sync)
                        {
                            if (++canceled == 10)
                            {
                                AllItemsCanceled.TrySetResult();
                            }
                        }

                        if (index == 0)
                        {
                            await ReleaseCancellation.Task;
                        }
                        throw;
                    }
                }
                else
                {
                    await Task.Delay(20, activity.CancellationToken);
                }

                if (failItem && index == 50)
                {
                    throw new ApplicationFailureException(
                        "Fixture item cannot be imported.",
                        "InvalidFixtureItem",
                        nonRetryable: true
                    );
                }

                return new(item.Id, new(index + 1), ItemDisposition.Applied);
            }
            finally
            {
                lock (sync)
                {
                    Active--;
                }
            }
        }

        private static DiscoveredItem<SetlistFmId, Setlist>[] Items(int first, int count) =>
            Enumerable
                .Range(first, count)
                .Select(index => new DiscoveredItem<SetlistFmId, Setlist>(
                    new(index.ToString()),
                    new Setlist()
                ))
                .ToArray();
    }
}
