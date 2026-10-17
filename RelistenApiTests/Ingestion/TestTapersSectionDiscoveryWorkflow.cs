using System.Collections.Concurrent;
using FluentAssertions;
using Relisten.Api.Models;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.TapersSection;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace RelistenApiTests.Ingestion;

/// <summary>Checks durable discovery progress through activity contracts, including a mid-page continuation.</summary>
[TestFixture, NonParallelizable]
public sealed class TestTapersSectionDiscoveryWorkflow
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ContinuationObservesThePersistedPageBeforeFetchingTheNextCursor(
        bool recoverRetainedItem
    )
    {
        await using var environment = await WorkflowEnvironment.StartLocalAsync();
        var activities = new DiscoveryActivities(recoverRetainedItem);
        using var worker = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(
                IngestionQueues.Workflows
            ).AddWorkflow<TapersSectionDiscoveryWorkflow>()
        );
        using var database = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Database).AddAllActivities(activities)
        );
        using var provider = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(
                IngestionQueues.Providers[UpstreamProvider.ArchiveOrg]
            ).AddAllActivities(activities)
        );
        await worker.ExecuteAsync(() =>
            database.ExecuteAsync(() =>
                provider.ExecuteAsync(async () =>
                {
                    var handle = await environment.Client.StartWorkflowAsync(
                        (TapersSectionDiscoveryWorkflow w) => w.RunAsync(new(), null),
                        new("intake-discovery/" + Guid.NewGuid(), IngestionQueues.Workflows)
                    );
                    var result = await handle.GetResultAsync();
                    result.Discovered.Should().Be(126);
                    result.Observed.Should().Be(recoverRetainedItem ? 126 : 125);
                    result.NeedsReview.Should().Be(recoverRetainedItem ? 126 : 125);
                    result.Failed.Should().Be(1);
                    result.Limited.Should().BeFalse();
                    activities.Pages.Should().Equal("first", "empty", "last");
                    activities
                        .Observed.Should()
                        .HaveCount(recoverRetainedItem ? 127 : 126)
                        .And.OnlyHaveUniqueItems();
                    activities.Persisted.Should().HaveCount(recoverRetainedItem ? 127 : 126);
                    var history = await environment
                        .Client.GetWorkflowHandle(handle.Id, handle.ResultRunId)
                        .FetchHistoryAsync();
                    history
                        .Events.Should()
                        .Contain(e => e.WorkflowExecutionContinuedAsNewEventAttributes != null);
                    await new WorkflowReplayer(
                        new WorkflowReplayerOptions().AddWorkflow<TapersSectionDiscoveryWorkflow>()
                    ).ReplayWorkflowAsync(history);
                })
            )
        );
    }

    [Test]
    public async Task PilotLimitDoesNotObserveOrAdvancePastUnpersistedItems()
    {
        await using var environment = await WorkflowEnvironment.StartLocalAsync();
        var activities = new DiscoveryActivities();
        using var worker = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(
                IngestionQueues.Workflows
            ).AddWorkflow<TapersSectionDiscoveryWorkflow>()
        );
        using var database = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Database).AddAllActivities(activities)
        );
        using var provider = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(
                IngestionQueues.Providers[UpstreamProvider.ArchiveOrg]
            ).AddAllActivities(activities)
        );
        await worker.ExecuteAsync(() =>
            database.ExecuteAsync(() =>
                provider.ExecuteAsync(async () =>
                {
                    var result = await environment.Client.ExecuteWorkflowAsync(
                        (TapersSectionDiscoveryWorkflow w) => w.RunAsync(new(3), null),
                        new("intake-pilot/" + Guid.NewGuid(), IngestionQueues.Workflows)
                    );
                    result.Discovered.Should().Be(3);
                    result.Limited.Should().BeTrue();
                    activities.Persisted.Should().HaveCount(3);
                    activities.Observed.Should().HaveCount(3);
                    activities.Pages.Should().Equal("first");
                })
            )
        );
    }

    public sealed class DiscoveryActivities(bool recoverRetainedItem = false)
    {
        public List<string> Pages { get; } = [];
        public ConcurrentDictionary<string, bool> Persisted { get; } = new();
        public ConcurrentBag<string> Observed { get; } = [];

        [Activity("TapersSection.SelectPendingObservations")]
        public ArchiveItemId[] SelectPendingObservations(IngestionExecution execution)
        {
            if (!recoverRetainedItem)
            {
                return [];
            }

            Persisted.TryAdd("retained-outside-collection", true);
            return [new("retained-outside-collection")];
        }

        [Activity("TapersSection.FetchPage")]
        public TapersSectionSearchPage FetchPage(IngestionExecution execution, string? cursor)
        {
            if (cursor != null)
            {
                Observed.Count.Should().Be(recoverRetainedItem ? 126 : 125);
            }

            Pages.Add(cursor ?? "first");
            return cursor switch
            {
                null => new(
                    Enumerable.Range(0, 125).Select(i => new ArchiveItemId("item-" + i)).ToArray(),
                    "empty",
                    126
                ),
                "empty" => new([], "last", 1),
                _ => new([new("final")], null, 1),
            };
        }

        [Activity("TapersSection.PersistDiscovery")]
        public ArchiveItemId[] PersistDiscovery(IngestionExecution execution, ArchiveItemId[] items)
        {
            foreach (var item in items)
            {
                Persisted.TryAdd(item.Value, true);
            }
            return items;
        }

        [Activity("TapersSection.ObserveItem")]
        public TapersSectionItemResult ObserveItem(IngestionExecution execution, ArchiveItemId item)
        {
            Persisted.Should().ContainKey(item.Value);
            Observed.Add(item.Value);
            if (item.Value == "final")
            {
                throw new ApplicationFailureException(
                    "Synthetic unavailable transport",
                    "FixtureFailure",
                    nonRetryable: true
                );
            }
            return new(
                item,
                TapersSectionItemOutcome.NeedsReview,
                Reason: TapersSectionReason.MissingDate
            );
        }
    }
}
