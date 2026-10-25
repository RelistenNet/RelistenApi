using FluentAssertions;
using Relisten.Normalization.Temporal;
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
}
