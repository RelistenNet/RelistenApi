using System.Collections.Concurrent;
using FluentAssertions;
using Relisten.Api.Models;
using Relisten.Import;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.Jerry;
using Relisten.Ingestion.Workflows;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace RelistenApiTests.Ingestion;

/// <summary>Exercises repair finalization and catalog rebuilding through the production workflows.</summary>
[TestFixture, NonParallelizable]
public class TestJerryVenueRepairWorkflow
{
    private WorkflowEnvironment environment = null!;

    [OneTimeSetUp]
    public async Task StartServer() => environment = await WorkflowEnvironment.StartLocalAsync();

    [OneTimeTearDown]
    public async Task StopServer() => await environment.DisposeAsync();

    [Test]
    public async Task FailedFinalizationPublishesCommittedRepairsBeforeReportingFailure()
    {
        var activities = new RepairActivities { FinalizationFailure = "VenueCleanupUnavailable" };
        await WithWorkers(
            activities,
            async handle =>
            {
                await AssertFinalizationFailure(handle, "VenueCleanupUnavailable");

                activities.Events.Should().Equal("repair", "finish", "shows", "years", "cache");
                var replayer = new WorkflowReplayer(
                    new WorkflowReplayerOptions().AddWorkflow<JerryVenueRepairWorkflow>()
                );
                await replayer.ReplayWorkflowAsync(await handle.FetchHistoryAsync());
            }
        );
    }

    [Test]
    public async Task ExpiredFinalizationDoesNotStartRebuilding()
    {
        var activities = new RepairActivities { FinalizationFailure = "ImportExpired" };
        await WithWorkers(
            activities,
            async handle =>
            {
                await AssertFinalizationFailure(handle, "ImportExpired");

                activities.Events.Should().Equal("repair", "finish");
            }
        );
    }

    [Test]
    public async Task CancellationWaitsForFinalizationWithoutStartingRebuilding()
    {
        var activities = new RepairActivities { HoldFinalization = true };
        await WithWorkers(
            activities,
            async handle =>
            {
                await activities.FinalizationEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await handle.CancelAsync();

                Func<Task> finish = async () =>
                    await handle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(20));
                var failure = await finish.Should().ThrowAsync<WorkflowFailedException>();

                TemporalException
                    .IsCanceledException(failure.Which.InnerException!)
                    .Should()
                    .BeTrue();
                activities.Events.Should().Equal("repair", "finish");
            }
        );
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PartialRepairsPublishSuccessfulWorkAndSuppressOrphanRemoval(bool skip)
    {
        var activities = new RepairActivities
        {
            ItemCount = 2,
            SkipSecond = skip,
            FailSecond = !skip,
        };
        await WithWorkers(
            activities,
            async handle =>
            {
                var result = await handle.GetResultAsync();

                result.CompletedItems.Should().Be(1);
                result.SkippedItems.Should().Be(skip ? 1 : 0);
                result.FailedItems.Should().Be(skip ? 0 : 1);
                activities.RemoveOrphans.Should().BeFalse();
                activities.Events.Should().Equal("repair", "finish", "shows", "years", "cache");
            }
        );
    }

    [Test]
    public async Task EmptySelectionStillFinalizesAndRebuilds()
    {
        var activities = new RepairActivities { ItemCount = 0 };
        await WithWorkers(
            activities,
            async handle =>
            {
                var result = await handle.GetResultAsync();

                result.CompletedItems.Should().Be(0);
                result.Status.Should().Be(ImportStatus.Completed);
                activities.RemoveOrphans.Should().BeTrue();
                activities.Events.Should().Equal("finish", "shows", "years", "cache");
            }
        );
    }

    private static async Task AssertFinalizationFailure(
        WorkflowHandle<JerryVenueRepairWorkflow, ArtistImportResult> handle,
        string errorType
    )
    {
        Func<Task> finish = async () => await handle.GetResultAsync();
        var failure = await finish.Should().ThrowAsync<WorkflowFailedException>();

        failure
            .Which.InnerException.Should()
            .BeOfType<ChildWorkflowFailureException>()
            .Which.InnerException.Should()
            .BeOfType<ActivityFailureException>()
            .Which.InnerException.Should()
            .BeOfType<ApplicationFailureException>()
            .Which.ErrorType.Should()
            .Be(errorType);
    }

    private async Task WithWorkers(
        RepairActivities activities,
        Func<WorkflowHandle<JerryVenueRepairWorkflow, ArtistImportResult>, Task> assert
    )
    {
        using var workflows = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Workflows)
                .AddWorkflow<JerryVenueRepairWorkflow>()
                .AddWorkflow<JerryVenueRepairProviderWorkflow>()
        );
        using var provider = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(
                IngestionQueues.Providers[UpstreamProvider.JerryGarcia]
            ).AddAllActivities(activities)
        );
        using var database = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Database).AddAllActivities(activities)
        );

        await workflows.ExecuteAsync(() =>
            provider.ExecuteAsync(() =>
                database.ExecuteAsync(async () =>
                {
                    var handle = await environment.Client.StartWorkflowAsync(
                        (JerryVenueRepairWorkflow workflow) =>
                            workflow.RunAsync(
                                new RepairJerryVenues(new ArtistId(Guid.NewGuid())),
                                null
                            ),
                        new WorkflowOptions(
                            "jerry-repair-test/" + Guid.NewGuid(),
                            IngestionQueues.Workflows
                        )
                    );

                    await assert(handle);
                })
            )
        );
    }

    public class RepairActivities
    {
        public int ItemCount { get; init; } = 1;
        public bool SkipSecond { get; init; }
        public bool FailSecond { get; init; }
        public string? FinalizationFailure { get; init; }
        public bool HoldFinalization { get; init; }
        public bool? RemoveOrphans { get; private set; }
        public ConcurrentQueue<string> Events { get; } = new();
        public TaskCompletionSource FinalizationEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Activity("Jerry.SelectExistingPages")]
        public JerrySelection SelectExistingPages(ProviderWorkflowContext context) =>
            new(
                Enumerable
                    .Range(0, ItemCount)
                    .Select(index => new DiscoveredItem<
                        JerryShowId,
                        JerryGarciaComImporter.FileMetaObject
                    >(new(index.ToString()), new()))
                    .ToArray()
            );

        [Activity("Jerry.RepairVenue")]
        public JerryItemResult RepairVenue(
            ProviderWorkflowContext context,
            DiscoveredItem<JerryShowId, JerryGarciaComImporter.FileMetaObject> item
        )
        {
            if (item.Id.Value == "1" && FailSecond)
            {
                throw new ApplicationFailureException("Invalid mirror page", nonRetryable: true);
            }

            if (item.Id.Value == "1" && SkipSecond)
            {
                return new(
                    item.Id,
                    null,
                    ItemDisposition.Skipped,
                    JerrySkipReason.MissingShowDetails
                );
            }

            Events.Enqueue("repair");
            return new(item.Id, new CatalogSetlistId(1), ItemDisposition.Applied);
        }

        [Activity("Jerry.FinishVenueRepair")]
        public async Task<int> FinishVenueRepair(
            ProviderWorkflowContext context,
            bool removeOrphans
        )
        {
            Events.Enqueue("finish");
            RemoveOrphans = removeOrphans;
            FinalizationEntered.TrySetResult();

            if (HoldFinalization)
            {
                var activity = ActivityExecutionContext.Current;
                while (true)
                {
                    activity.Heartbeat();
                    await Task.Delay(50, activity.CancellationToken);
                }
            }

            if (FinalizationFailure != null)
            {
                throw new ApplicationFailureException(
                    "Venue finalization failed",
                    FinalizationFailure,
                    nonRetryable: true
                );
            }

            return 0;
        }

        [Activity("Catalog.RebuildShows")]
        public void RebuildShows(ProviderWorkflowContext context) => Events.Enqueue("shows");

        [Activity("Catalog.RebuildYears")]
        public void RebuildYears(ProviderWorkflowContext context) => Events.Enqueue("years");

        [Activity("Catalog.InvalidateArtistCache")]
        public void InvalidateArtistCache(ProviderWorkflowContext context) =>
            Events.Enqueue("cache");
    }
}
