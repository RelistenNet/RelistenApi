using FluentAssertions;
using Relisten.Normalization.Temporal;
using Relisten.Normalization.Venues;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace RelistenApiTests.Normalization;

/// A stand-in for the venue run, under its workflow type: it holds the one venue-run id until
/// told to finish.
[Workflow("VenueCatalogueWorkflow")]
public class HeldVenueRun
{
    private bool done;

    [WorkflowRun]
    public async Task<VenueCatalogueResult> RunAsync(BuildVenueCatalogue request)
    {
        await Workflow.WaitConditionAsync(() => done);
        return new VenueCatalogueResult(0, 0, 0, 0, 0, 0);
    }

    [WorkflowSignal]
    public Task FinishAsync()
    {
        done = true;
        return Task.CompletedTask;
    }
}

[TestFixture, NonParallelizable]
public class TestNightlyVenueRun
{
    private WorkflowEnvironment environment = null!;

    [OneTimeSetUp]
    public async Task StartServer() => environment = await WorkflowEnvironment.StartLocalAsync();

    [OneTimeTearDown]
    public async Task StopServer() => await environment.DisposeAsync();

    [Test]
    public async Task TheNightlyRunGivesWayToAManualRun()
    {
        using var worker = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(NormalizationQueues.Workflows)
                .AddWorkflow<HeldVenueRun>()
                .AddWorkflow<NightlyVenueCatalogueWorkflow>()
        );
        await worker.ExecuteAsync(async () =>
        {
            var manual = await environment.Client.StartWorkflowAsync(
                (HeldVenueRun run) => run.RunAsync(new BuildVenueCatalogue()),
                new WorkflowOptions(NormalizationClient.VenueRunId, NormalizationQueues.Workflows)
            );

            var request = new BuildVenueCatalogue(null, null, true);
            var nightly = await environment.Client.ExecuteWorkflowAsync(
                (NightlyVenueCatalogueWorkflow run) => run.RunAsync(request),
                new WorkflowOptions("norm/venues/nightly-test", NormalizationQueues.Workflows)
            );

            nightly.Should().StartWith("skipped");
            await manual.SignalAsync(run => run.FinishAsync());
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task VenueRunRefreshesSearchOnlyWhenArtistsAreKnown(bool flaggedOnly)
    {
        var artists = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var activities = new VenueSearchActivities(artists);
        using var workflows = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(
                NormalizationQueues.Workflows
            ).AddWorkflow<VenueCatalogueWorkflow>()
        );
        using var database = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(NormalizationQueues.Database).AddAllActivities(activities)
        );
        var request = new BuildVenueCatalogue(
            MaxChunks: 0,
            FlaggedOnly: flaggedOnly,
            HistoryMinShows: 0
        );

        await workflows.ExecuteAsync(() =>
            database.ExecuteAsync(() =>
                environment.Client.ExecuteWorkflowAsync(
                    (VenueCatalogueWorkflow workflow) => workflow.RunAsync(request),
                    new WorkflowOptions(
                        "test/venue-search/" + Guid.NewGuid(),
                        NormalizationQueues.Workflows
                    )
                )
            )
        );

        activities.SearchRefreshes.Should().Equal(flaggedOnly ? artists : []);
    }

    public sealed class VenueSearchActivities(Guid[] artists)
    {
        public List<Guid> SearchRefreshes { get; } = [];

        [Activity("Venues.FlaggedArtists")]
        public Guid[] FlaggedArtists() => artists;

        [Activity("Venues.Match")]
        public VenueMatchResult Match(IReadOnlyList<Guid>? selected) => new(0, 0, 0, 0);

        [Activity(SearchReindexStep.ActivityName)]
        public void RefreshSearch(Guid artist) => SearchRefreshes.Add(artist);
    }
}
