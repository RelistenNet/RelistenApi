using FluentAssertions;
using Relisten.Api.Models;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.SetlistFm;
using Relisten.Vendor.SetlistFm;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;

namespace RelistenApiTests.Ingestion;

/// <summary>Verifies the saved page and cursor contract through a concrete paged provider.</summary>
[TestFixture, NonParallelizable]
public class TestPagedProviderWorkflow
{
    [Test]
    public async Task RolloverRetainsTheCurrentPageAndEmptyPagesDoNotEndEnumeration()
    {
        await using var environment = await WorkflowEnvironment.StartLocalAsync();
        var activities = new PagedActivities();
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
                    (SetlistFmWorkflow workflow) => workflow.RunAsync(input, null, null),
                    new WorkflowOptions("pages/" + Guid.NewGuid(), IngestionQueues.Workflows)
                );
                var result = await handle.GetResultAsync();

                result.CompletedItems.Should().Be(122);
                activities.ReadPages.Should().Equal(1, 2, 3);
                activities.Imported.Should().Equal(Enumerable.Range(0, 122));
                var firstHistory = await environment
                    .Client.GetWorkflowHandle(handle.Id, handle.ResultRunId)
                    .FetchHistoryAsync();
                firstHistory
                    .Events.Should()
                    .Contain(e => e.WorkflowExecutionContinuedAsNewEventAttributes != null);
                await new WorkflowReplayer(
                    new WorkflowReplayerOptions().AddWorkflow<SetlistFmWorkflow>()
                ).ReplayWorkflowAsync(firstHistory);
            })
        );
    }

    public class PagedActivities
    {
        public List<int> ReadPages { get; } = [];
        public List<int> Imported { get; } = [];

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
        public SetlistFmItemResult ImportSetlistAsync(
            ProviderWorkflowContext context,
            DiscoveredItem<SetlistFmId, Setlist> item
        )
        {
            var index = int.Parse(item.Id.Value);
            Imported.Add(index);
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
