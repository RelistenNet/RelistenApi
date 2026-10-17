using System.Collections.Concurrent;
using FluentAssertions;
using Relisten.Api.Models;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.Archive;
using Relisten.Ingestion.Providers.ArchiveCollections;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace RelistenApiTests.Ingestion;

/// <summary>Verifies bounded scans, durable resumption and partial failures through the activity contracts.</summary>
[TestFixture, NonParallelizable]
public sealed class TestArchiveCollectionDiscoveryWorkflow
{
    [Test]
    public async Task BoundedScanSettlesItsPageThenResumesWithoutReimportingItsAcknowledgedItems()
    {
        await using var environment = await WorkflowEnvironment.StartLocalAsync();
        var activities = new DiscoveryActivities();
        using var worker = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(
                IngestionQueues.Workflows
            ).AddWorkflow<ArchiveCollectionDiscoveryWorkflow>()
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
                    var first = await environment.Client.ExecuteWorkflowAsync(
                        (ArchiveCollectionDiscoveryWorkflow w) =>
                            w.RunAsync(new(activities.Selector, 1), null),
                        new("collections-bounded/" + Guid.NewGuid(), IngestionQueues.Workflows)
                    );

                    first.Discovered.Should().Be(125);
                    first.Observed.Should().Be(125);
                    first.Linked.Should().Be(124);
                    first.Unresolved.Should().Be(1);
                    first.Limited.Should().BeTrue();
                    first.Completed.Should().BeFalse();
                    activities.Pages.Should().Equal("first");
                    activities.Checkpoint.PendingItems.Should().BeEmpty();
                    activities
                        .Begins.Should()
                        .Be(
                            1,
                            "a workflow continuation must reuse its scan, rather than begin another one"
                        );

                    var resumed = await environment.Client.ExecuteWorkflowAsync(
                        (ArchiveCollectionDiscoveryWorkflow w) =>
                            w.RunAsync(new(activities.Selector), null),
                        new("collections-resumed/" + Guid.NewGuid(), IngestionQueues.Workflows)
                    );

                    resumed.Completed.Should().BeTrue();
                    resumed.Limited.Should().BeFalse();
                    resumed.Discovered.Should().Be(1);
                    resumed.Failed.Should().Be(1);
                    resumed
                        .Failures.Should()
                        .ContainSingle()
                        .Which.Should()
                        .BeOfType<ItemFailure<ArchiveItemId>>()
                        .Which.Key.Value.Should()
                        .Be("failed-final");
                    activities.Pages.Should().Equal("first", "empty", "terminal");
                    activities.Attempted.Should().HaveCount(126).And.OnlyHaveUniqueItems();
                    activities.Checkpoint.PendingItems.Should().BeEmpty();
                    activities.Checkpoint.Completed.Should().BeTrue();
                })
            )
        );
    }

    [Test]
    public async Task RetainedPendingItemsSettleBeforeTheNextPageAndDisabledSelectorsDoNotFetch()
    {
        await using var environment = await WorkflowEnvironment.StartLocalAsync();
        var activities = new DiscoveryActivities();
        activities.Checkpoint = activities.Checkpoint with
        {
            Cursor = "empty",
            PendingItems = ["retained-before-history-loss"],
            Discovered = 1,
        };
        using var worker = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(
                IngestionQueues.Workflows
            ).AddWorkflow<ArchiveCollectionDiscoveryWorkflow>()
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
                    var recovered = await environment.Client.ExecuteWorkflowAsync(
                        (ArchiveCollectionDiscoveryWorkflow w) =>
                            w.RunAsync(new(activities.Selector), null),
                        new(
                            "collections-history-recovery/" + Guid.NewGuid(),
                            IngestionQueues.Workflows
                        )
                    );
                    recovered.Observed.Should().Be(1);
                    recovered.Failed.Should().Be(1);
                    recovered.Completed.Should().BeTrue();
                    activities.Attempted.Should().Contain("retained-before-history-loss");

                    activities.Checkpoint = activities.Checkpoint with { Enabled = false };
                    var before = activities.Pages.Count;
                    var paused = await environment.Client.ExecuteWorkflowAsync(
                        (ArchiveCollectionDiscoveryWorkflow w) =>
                            w.RunAsync(new(activities.Selector), null),
                        new("collections-disabled/" + Guid.NewGuid(), IngestionQueues.Workflows)
                    );
                    paused.Paused.Should().BeTrue();
                    activities.Pages.Should().HaveCount(before);
                })
            )
        );
    }

    public sealed class DiscoveryActivities
    {
        public Guid Selector { get; } = Guid.NewGuid();
        public List<string> Pages { get; } = [];
        public ConcurrentBag<string> Attempted { get; } = [];
        public int Begins { get; private set; }
        public ArchiveCollectionDiscoveryCheckpoint Checkpoint { get; set; }

        public DiscoveryActivities() =>
            Checkpoint = new(Selector, Guid.NewGuid(), "fixture", true, null, [], 0, false, false);

        [Activity("ArchiveCollections.BeginOrResume")]
        public ArchiveCollectionDiscoveryCheckpoint Begin(
            IngestionExecution execution,
            Guid selector
        )
        {
            selector.Should().Be(Selector);
            Begins++;
            return Checkpoint;
        }

        [Activity("ArchiveCollections.FetchPage")]
        public ArchiveCollectionSearchPage Fetch(
            IngestionExecution execution,
            ArchiveCollectionDiscoveryCheckpoint checkpoint
        )
        {
            checkpoint
                .PendingItems.Should()
                .BeEmpty("persisted items must settle before the next cursor is fetched");
            Checkpoint.PendingItems.Should().BeEmpty();
            Pages.Add(checkpoint.Cursor ?? "first");
            return checkpoint.Cursor switch
            {
                null => new(
                    Enumerable
                        .Range(0, 125)
                        .Select(index => new ArchiveItemId("item-" + index))
                        .ToArray(),
                    "empty",
                    126
                ),
                "empty" => new([], "terminal", 1),
                _ => new([new("failed-final")], null, 1),
            };
        }

        [Activity("ArchiveCollections.ApplyPage")]
        public ArchiveCollectionDiscoveryCheckpoint Apply(
            IngestionExecution execution,
            ArchiveCollectionDiscoveryCheckpoint expected,
            ArchiveCollectionSearchPage page
        )
        {
            Checkpoint = Checkpoint with
            {
                Cursor = page.Cursor,
                PendingItems = page.Items.Select(item => item.Value).ToArray(),
                Discovered = Checkpoint.Discovered + page.Items.Length,
                SearchComplete = page.Cursor == null,
            };
            return Checkpoint;
        }

        [Activity("ArchiveCollections.ObserveItem")]
        public ArchiveCollectionObservationResult Observe(
            IngestionExecution execution,
            ArchiveItemId item
        )
        {
            Checkpoint.PendingItems.Should().Contain(item.Value);
            Attempted.Add(item.Value);
            if (item.Value == "failed-final")
            {
                throw new ApplicationFailureException(
                    "Synthetic metadata failure",
                    "FixtureFailure",
                    nonRetryable: true
                );
            }
            return new(item, item.Value != "item-0");
        }

        [Activity("ArchiveCollections.AcknowledgeItems")]
        public ArchiveCollectionDiscoveryCheckpoint Acknowledge(
            IngestionExecution execution,
            ArchiveCollectionDiscoveryCheckpoint expected,
            string[] items
        )
        {
            items.Should().OnlyContain(item => Attempted.Contains(item));
            Checkpoint.PendingItems.Take(items.Length).Should().Equal(items);
            Checkpoint = Checkpoint with
            {
                PendingItems = Checkpoint.PendingItems.Skip(items.Length).ToArray(),
            };
            return Checkpoint;
        }

        [Activity("ArchiveCollections.Complete")]
        public ArchiveCollectionDiscoveryCheckpoint Complete(
            IngestionExecution execution,
            ArchiveCollectionDiscoveryCheckpoint checkpoint
        )
        {
            Checkpoint.SearchComplete.Should().BeTrue();
            Checkpoint.PendingItems.Should().BeEmpty();
            Checkpoint = Checkpoint with { Completed = true };
            return Checkpoint;
        }
    }
}
