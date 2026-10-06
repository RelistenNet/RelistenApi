using FluentAssertions;
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

    private async Task<ImportResult> Run(RecordingActivities activities, bool singleItem)
    {
        using var workflowWorker = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Workflows)
                .AddWorkflow<ArtistRefreshWorkflow>()
                .AddWorkflow<ReimportItemWorkflow>()
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
        var request = new ArtistImportRequest(
            "artist",
            Guid.NewGuid().ToString(),
            Item: singleItem ? "one" : null
        );
        return await workflowWorker.ExecuteAsync(() =>
            dbWorker.ExecuteAsync(() =>
                fetchWorker.ExecuteAsync(async () =>
                {
                    var options = new WorkflowOptions(
                        "test/" + request.RequestId,
                        IngestionQueues.Workflows
                    );
                    return singleItem
                        ? await environment.Client.ExecuteWorkflowAsync(
                            (ReimportItemWorkflow w) => w.RunAsync(request, null),
                            options
                        )
                        : await environment.Client.ExecuteWorkflowAsync(
                            (ArtistRefreshWorkflow w) => w.RunAsync(request, null),
                            options
                        );
                })
            )
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

        [Activity("Ingestion.Prepare")]
        public Task<ArtistImportPlan> Prepare(ArtistImportRequest request)
        {
            Preparations++;
            return Task.FromResult(new ArtistImportPlan("artist", ["archive.org"]));
        }

        [Activity("Ingestion.Discover")]
        public Task<ImportPage> Discover(
            ArtistImportRequest request,
            string provider,
            string? cursor
        ) => Task.FromResult(new ImportPage("page", count, null));

        [Activity("Ingestion.Fetch")]
        public Task<bool> Fetch(ImportItemRef item, int part)
        {
            Fetches.Add((item.Index, part));
            if (item.Index == failedIndex)
                throw new ApplicationFailureException(
                    "Invalid provider metadata",
                    nonRetryable: true
                );
            return Task.FromResult(part == 1);
        }

        [Activity("Ingestion.Apply")]
        public Task<ImportStats> Apply(ImportItemRef item)
        {
            Fetches.Should().Contain((item.Index, 1));
            Applied.Add(item.Index);
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
        public Task FinishArtist(ArtistImportRequest request, ImportResult result)
        {
            Finished = true;
            return Task.CompletedTask;
        }
    }
}
