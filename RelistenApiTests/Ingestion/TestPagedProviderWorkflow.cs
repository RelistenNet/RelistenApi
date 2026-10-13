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
        var activities = new PagedActivities(failItem);
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
                var result = await handle.GetResultAsync();

                result.CompletedItems.Should().Be(failItem ? 121 : 122);
                result.FailedItems.Should().Be(failItem ? 1 : 0);
                activities.ReadPages.Should().Equal(1, 2, 3);
                activities.Imported.Should().Equal(Enumerable.Range(0, 122));
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
    public async Task CancellationWithinAPageStopsBeforeTheNextItemOrPage()
    {
        await using var environment = await WorkflowEnvironment.StartLocalAsync();
        var activities = new PagedActivities(false, holdItem: true);
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
                await activities.ItemEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await handle.CancelAsync();

                Func<Task> result = async () => await handle.GetResultAsync();
                var failure = await result.Should().ThrowAsync<WorkflowFailedException>();
                TemporalException
                    .IsCanceledException(failure.Which.InnerException!)
                    .Should()
                    .BeTrue(failure.Which.ToString());
                activities.ReadPages.Should().Equal(1);
                activities.Imported.Should().Equal(Enumerable.Range(0, 51));
            })
        );
    }

    public class PagedActivities(bool failItem, bool holdItem = false)
    {
        public List<int> ReadPages { get; } = [];
        public List<int> Imported { get; } = [];
        public TaskCompletionSource ItemEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Activity("SetlistFm.ReadSetlistPage")]
        public Page<DiscoveredItem<SetlistFmId, Setlist>, SetlistFmPage> ReadSetlistPageAsync(
            ProviderWorkflowContext context,
            SetlistFmPage? cursor
        )
        {
            var page = cursor?.Number ?? 1;
            ReadPages.Add(page);
            return page switch
            {
                1 => new(Items(0, 120), new(2)),
                2 => new([], new(3)),
                3 => new(Items(120, 2), null),
                _ => throw new InvalidOperationException("Unexpected cursor"),
            };
        }

        [Activity("SetlistFm.ImportSetlist")]
        public async Task<SetlistFmItemResult> ImportSetlistAsync(
            ProviderWorkflowContext context,
            DiscoveredItem<SetlistFmId, Setlist> item
        )
        {
            var index = int.Parse(item.Id.Value);
            Imported.Add(index);
            if (holdItem && index == 50)
            {
                ItemEntered.TrySetResult();
                var activity = ActivityExecutionContext.Current;
                while (true)
                {
                    activity.Heartbeat();
                    await Task.Delay(100, activity.CancellationToken);
                }
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
