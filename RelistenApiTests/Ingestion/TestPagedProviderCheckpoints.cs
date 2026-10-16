using System.Collections.Concurrent;
using FluentAssertions;
using Relisten.Api.Models;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.PhantasyTour;
using Relisten.Ingestion.Providers.PhishIn;
using Relisten.Vendor.PhantasyTour;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;

namespace RelistenApiTests.Ingestion;

/// <summary>Checks provider-specific state that must survive a page-boundary continuation.</summary>
[TestFixture, NonParallelizable]
public class TestPagedProviderCheckpoints
{
    [Test]
    public async Task PhishInPreparesCatalogsOnceAndRetainsThemAcrossPagesAndRollover()
    {
        await using var environment = await WorkflowEnvironment.StartLocalAsync();
        var activities = new PhishInActivitiesFixture();
        using var workflows = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Workflows).AddWorkflow<PhishInWorkflow>()
        );
        using var provider = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(
                IngestionQueues.Providers[UpstreamProvider.Phishin]
            ).AddAllActivities(activities)
        );

        await workflows.ExecuteAsync(() =>
            provider.ExecuteAsync(async () =>
            {
                var input = new ProviderRequest<RefreshPhishInArtist>(
                    new(new ArtistId(Guid.NewGuid())),
                    Execution()
                );
                var handle = await environment.Client.StartWorkflowAsync(
                    (PhishInWorkflow workflow) => workflow.RunAsync(input, null, null),
                    new WorkflowOptions("phish-pages/" + Guid.NewGuid(), IngestionQueues.Workflows)
                );
                var result = await handle.GetResultAsync();

                result.CompletedItems.Should().Be(120);
                activities.Preparations.Should().Be(1);
                activities.ReadPages.Should().Equal(Enumerable.Range(1, 6));
                activities.Imported.Should().BeEquivalentTo(Enumerable.Range(1, 120));
                var history = await environment
                    .Client.GetWorkflowHandle(handle.Id, handle.ResultRunId)
                    .FetchHistoryAsync();
                history
                    .Events.Should()
                    .Contain(e => e.WorkflowExecutionContinuedAsNewEventAttributes != null);
                await new WorkflowReplayer(
                    new WorkflowReplayerOptions().AddWorkflow<PhishInWorkflow>()
                ).ReplayWorkflowAsync(history);
            })
        );
    }

    [Test]
    public async Task PhantasyTourRetainsLatestListedDatesAcrossRolloverAndRepairsOnceAfterTheLastPage()
    {
        await using var environment = await WorkflowEnvironment.StartLocalAsync();
        var activities = new PhantasyTourActivitiesFixture();
        using var workflows = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Workflows).AddWorkflow<PhantasyTourWorkflow>()
        );
        using var provider = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(
                IngestionQueues.Providers[UpstreamProvider.PhantasyTour]
            ).AddAllActivities(activities)
        );

        await workflows.ExecuteAsync(() =>
            provider.ExecuteAsync(async () =>
            {
                var input = new ProviderRequest<RefreshPhantasyTourArtist>(
                    new(new ArtistId(Guid.NewGuid())),
                    Execution()
                );
                var handle = await environment.Client.StartWorkflowAsync(
                    (PhantasyTourWorkflow workflow) => workflow.RunAsync(input, null),
                    new WorkflowOptions("tour-pages/" + Guid.NewGuid(), IngestionQueues.Workflows)
                );
                var result = await handle.GetResultAsync();

                result.CompletedItems.Should().Be(100);
                activities.ReadPages.Should().Equal(1, 2);
                activities.Imported.Should().BeEquivalentTo(Enumerable.Range(1, 100));
                activities.Repairs.Should().Be(1);
                activities.RepairedDates.Should().HaveCount(100);
                activities
                    .RepairedDates.Single(entry => entry.Id.Value == 1)
                    .Date.Should()
                    .Be(new DateTime(2000, 1, 2));
                var history = await environment
                    .Client.GetWorkflowHandle(handle.Id, handle.ResultRunId)
                    .FetchHistoryAsync();
                history
                    .Events.Should()
                    .Contain(e => e.WorkflowExecutionContinuedAsNewEventAttributes != null);
                await new WorkflowReplayer(
                    new WorkflowReplayerOptions().AddWorkflow<PhantasyTourWorkflow>()
                ).ReplayWorkflowAsync(history);
            })
        );
    }

    private static IngestionExecution Execution() =>
        new(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddHours(1));

    public class PhishInActivitiesFixture
    {
        public int Preparations { get; private set; }
        public List<int> ReadPages { get; } = [];
        public ConcurrentQueue<int> Imported { get; } = new();

        [Activity("PhishIn.PrepareCatalogs")]
        public PhishInCatalogs PrepareCatalogsAsync(ProviderWorkflowContext context)
        {
            Preparations++;
            return new(new Dictionary<string, IList<string>>(), [new(42, "prepared-song")]);
        }

        [Activity("PhishIn.ReadShowPage")]
        public Page<DiscoveredItem<PhishInShowId, PhishInShowInput>, PhishInPage> ReadShowPageAsync(
            ProviderRequest<RefreshPhishInArtist> input,
            PhishInPage? cursor
        )
        {
            var page = cursor?.Number ?? 1;
            ReadPages.Add(page);
            return new(
                Enumerable
                    .Range((page - 1) * 20 + 1, 20)
                    .Select(id => new DiscoveredItem<PhishInShowId, PhishInShowInput>(
                        new(id),
                        new("2000-01-01", false, false, 1, 1, null, DateTime.UnixEpoch, [], [])
                    ))
                    .ToArray(),
                page < 6 ? new(page + 1) : null
            );
        }

        [Activity("PhishIn.ImportShow")]
        public PhishInItemResult ImportShowAsync(
            ProviderWorkflowContext context,
            PhishInCatalogs catalogs,
            DiscoveredItem<PhishInShowId, PhishInShowInput> item
        )
        {
            catalogs.Songs.Should().Equal(new PhishInSongMapping(42, "prepared-song"));
            Imported.Enqueue(item.Id.Value);
            return new(item.Id, ItemDisposition.Applied, new(item.Id.Value), new(item.Id.Value));
        }
    }

    public class PhantasyTourActivitiesFixture
    {
        public List<int> ReadPages { get; } = [];
        public ConcurrentQueue<int> Imported { get; } = new();
        public int Repairs { get; private set; }
        public PhantasyTourListedDate[] RepairedDates { get; private set; } = [];

        [Activity("PhantasyTour.ReadShowPage")]
        public PhantasyTourDiscovery ReadShowPageAsync(
            ProviderWorkflowContext context,
            PhantasyTourPage? cursor
        )
        {
            var page = cursor?.Number ?? 1;
            ReadPages.Add((int)page);
            if (page == 2)
            {
                return new([], null, [new(new(1), new DateTime(2000, 1, 2))]);
            }

            return new(
                Enumerable
                    .Range(1, 100)
                    .Select(id => new DiscoveredItem<PhantasyTourShowId, PhantasyTourShowListing>(
                        new(id),
                        new() { id = id }
                    ))
                    .ToArray(),
                new(2),
                Enumerable
                    .Range(1, 100)
                    .Select(id => new PhantasyTourListedDate(new(id), new DateTime(2000, 1, 1)))
                    .ToArray()
            );
        }

        [Activity("PhantasyTour.ImportShow")]
        public PhantasyTourItemResult ImportShowAsync(
            ProviderWorkflowContext context,
            DiscoveredItem<PhantasyTourShowId, PhantasyTourShowListing> item
        )
        {
            Imported.Enqueue(item.Id.Value);
            return new(item.Id, new(item.Id.Value), ItemDisposition.Applied);
        }

        [Activity("PhantasyTour.RepairListedDates")]
        public int RepairListedDatesAsync(
            ProviderWorkflowContext context,
            PhantasyTourListedDate[] dates
        )
        {
            ReadPages.Should().Equal(1, 2);
            Repairs++;
            RepairedDates = dates;
            return dates.Length;
        }
    }
}
