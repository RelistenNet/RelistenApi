using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Relisten.Api.Models;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.ArchiveCollections;
using Relisten.Ingestion.Providers.TapersSection;
using Relisten.Ingestion.Workflows;
using Temporalio.Activities;
using Temporalio.Client.Schedules;
using Temporalio.Testing;
using Temporalio.Worker;

namespace RelistenApiTests.Ingestion;

/// <summary>Exercises admission against Temporal, including races between different artist operations.</summary>
[TestFixture, NonParallelizable]
public class TestImportAdmission
{
    private WorkflowEnvironment environment = null!;

    [OneTimeSetUp]
    public async Task StartServer() => environment = await WorkflowEnvironment.StartLocalAsync();

    [OneTimeTearDown]
    public async Task StopServer() => await environment.DisposeAsync();

    [Test]
    public async Task ActiveRefreshRejectsTargetedImportAndRebuildWithTheOwningWorkflowId()
    {
        var artist = new ArtistId(Guid.NewGuid());
        var client = Client(new Catalog([UpstreamProvider.ArchiveOrg]));
        var owner = await client.RefreshAsync(new RefreshArtist(artist));
        try
        {
            Func<Task> targeted = async () =>
                await client.ImportArchiveItemAsync(
                    new ImportArchiveItem(artist, new ArchiveItemId("one"))
                );
            Func<Task> rebuild = async () =>
                await client.RebuildArtistCatalogAsync(new RebuildArtistCatalog(artist));

            var itemConflict = await targeted.Should().ThrowAsync<ImportAlreadyRunning>();
            var rebuildConflict = await rebuild.Should().ThrowAsync<ImportAlreadyRunning>();
            itemConflict.Which.WorkflowId.Should().Be(owner.WorkflowId);
            rebuildConflict.Which.WorkflowId.Should().Be(owner.WorkflowId);
        }
        finally
        {
            await environment
                .Client.GetWorkflowHandle(owner.WorkflowId)
                .TerminateAsync("Test cleanup");
        }
    }

    [Test]
    public async Task CohortAdmissionDoesNotNeedCollectionMappingAndExcludesOrdinaryArtistOperations()
    {
        var artist = new ArtistId(Guid.NewGuid());
        var client = Client(new Catalog([], cohort: true));
        var owner = await client.RefreshTapersSectionArtistAsync(new(artist));
        try
        {
            Func<Task> ordinary = async () => await client.RefreshAsync(new RefreshArtist(artist));
            var conflict = await ordinary.Should().ThrowAsync<ImportAlreadyRunning>();
            conflict.Which.WorkflowId.Should().Be(owner.WorkflowId);
        }
        finally
        {
            await environment
                .Client.GetWorkflowHandle(owner.WorkflowId)
                .TerminateAsync("Test cleanup");
        }

        Func<Task> nonCohort = async () =>
            await Client(new Catalog([])).RefreshTapersSectionArtistAsync(new(artist));
        await nonCohort.Should().ThrowAsync<UnsupportedImportOperation>();
    }

    [Test]
    public async Task CollectionSelectorsSerializeTheirOwnScansAndRemainIndependent()
    {
        var client = Client(new Catalog([]));
        var selector = Guid.NewGuid();
        var first = await client.DiscoverArchiveCollectionAsync(new(selector));
        var other = await client.DiscoverArchiveCollectionAsync(new(Guid.NewGuid(), 1));
        try
        {
            Func<Task> duplicate = async () =>
                await client.DiscoverArchiveCollectionAsync(new(selector, 2));
            var conflict = await duplicate.Should().ThrowAsync<ImportAlreadyRunning>();
            conflict.Which.WorkflowId.Should().Be(first.WorkflowId);
            other.WorkflowId.Should().NotBe(first.WorkflowId);
        }
        finally
        {
            await environment
                .Client.GetWorkflowHandle(first.WorkflowId)
                .TerminateAsync("Test cleanup");
            await environment
                .Client.GetWorkflowHandle(other.WorkflowId)
                .TerminateAsync("Test cleanup");
        }
    }

    [Test]
    public async Task InvalidCollectionScopesAreRejectedBeforeAdmission()
    {
        var client = Client(new Catalog([]));
        Func<Task> missing = async () =>
            await client.DiscoverArchiveCollectionAsync(new(Guid.Empty));
        Func<Task> invalidBudget = async () =>
            await client.DiscoverArchiveCollectionAsync(new(Guid.NewGuid(), 0));
        await missing.Should().ThrowAsync<InvalidImportRequest>();
        await invalidBudget.Should().ThrowAsync<InvalidImportRequest>();
    }

    [Test]
    public async Task IntakeSchedulesStartPausedAndRepeatRegistrationPreservesOperatorState()
    {
        var client = Client(new Catalog([]));
        var discovery = environment.Client.GetScheduleHandle(
            "relisten-ingest-taperssection-discovery"
        );
        var refresh = environment.Client.GetScheduleHandle("relisten-ingest-taperssection-refresh");
        await client.RegisterTapersSectionSchedulesAsync();
        try
        {
            (await discovery.DescribeAsync()).Schedule.State.Paused.Should().BeTrue();
            (await refresh.DescribeAsync()).Schedule.State.Paused.Should().BeTrue();
            await refresh.UnpauseAsync();
            await client.RegisterTapersSectionSchedulesAsync();
            (await discovery.DescribeAsync()).Schedule.State.Paused.Should().BeTrue();
            (await refresh.DescribeAsync()).Schedule.State.Paused.Should().BeFalse();
        }
        finally
        {
            await discovery.DeleteAsync();
            await refresh.DeleteAsync();
        }
    }

    [Test]
    public async Task SimultaneousStartsAdmitExactlyOneOperation()
    {
        var artist = new ArtistId(Guid.NewGuid());
        var client = Client(new Catalog([UpstreamProvider.ArchiveOrg]));
        var starts = new[]
        {
            Capture(() => client.RefreshAsync(new RefreshArtist(artist))),
            Capture(() => client.RebuildArtistCatalogAsync(new RebuildArtistCatalog(artist))),
        };
        var outcomes = await Task.WhenAll(starts);
        var winner = outcomes.Single(result => result.Handle != null).Handle!;
        try
        {
            outcomes.Should().ContainSingle(result => result.Error is ImportAlreadyRunning);
            ((ImportAlreadyRunning)outcomes.Single(result => result.Error != null).Error!)
                .WorkflowId.Should()
                .Be(winner.WorkflowId);
        }
        finally
        {
            await environment
                .Client.GetWorkflowHandle(winner.WorkflowId)
                .TerminateAsync("Test cleanup");
        }
    }

    [Test]
    public async Task ClosedWorkflowIdCanBeUsedForANewIntentionalOperation()
    {
        var artist = new ArtistId(Guid.NewGuid());
        var client = Client(new Catalog([UpstreamProvider.ArchiveOrg]));
        var first = await client.RefreshAsync(new RefreshArtist(artist));
        await environment
            .Client.GetWorkflowHandle(first.WorkflowId)
            .TerminateAsync("Finish first operation");
        var second = await client.RebuildArtistCatalogAsync(new RebuildArtistCatalog(artist));
        try
        {
            second.WorkflowId.Should().Be(first.WorkflowId);
            second.FirstExecutionRunId.Should().NotBe(first.FirstExecutionRunId);
            second.ResultRunId.Should().NotBe(first.ResultRunId);
            var firstDescription = await first.DescribeAsync();
            firstDescription.RunId.Should().Be(first.ResultRunId);
            var secondDescription = await second.DescribeAsync();
            secondDescription.RunId.Should().Be(second.ResultRunId);
        }
        finally
        {
            await environment
                .Client.GetWorkflowHandle(second.WorkflowId)
                .TerminateAsync("Test cleanup");
        }
    }

    [Test]
    public async Task MissingArtistAndMissingMappingHaveDifferentPublicErrors()
    {
        var artist = new ArtistId(Guid.NewGuid());
        Func<Task> missing = async () =>
            await Client(new Catalog(null)).RefreshAsync(new RefreshArtist(artist));
        Func<Task> unsupported = async () =>
            await Client(new Catalog([UpstreamProvider.Local]))
                .ImportArchiveItemAsync(new ImportArchiveItem(artist, new ArchiveItemId("one")));
        await missing.Should().ThrowAsync<ArtistNotFound>();
        await unsupported.Should().ThrowAsync<UnsupportedImportOperation>();
    }

    [TestCase(0)]
    [TestCase(10000)]
    public async Task InvalidYearsAreRejectedBeforeAdmission(int year)
    {
        var client = Client(new Catalog([UpstreamProvider.ArchiveOrg]));
        Func<Task> start = async () =>
            await client.RefreshAsync(new RefreshArchiveArtist(new ArtistId(Guid.NewGuid()), year));
        await start.Should().ThrowAsync<InvalidImportRequest>();
    }

    [TestCase("")]
    [TestCase(" ")]
    public async Task MissingTargetedItemNeverStartsAFullRefresh(string item)
    {
        var client = Client(new Catalog([UpstreamProvider.ArchiveOrg]));
        Func<Task> start = async () =>
            await client.ImportArchiveItemAsync(
                new ImportArchiveItem(new ArtistId(Guid.NewGuid()), new ArchiveItemId(item))
            );
        await start.Should().ThrowAsync<InvalidImportRequest>();
    }

    [Test]
    public async Task RegisteredScheduleExecutesThinRefreshAndRegistrationPreservesPause()
    {
        var client = Client(new Catalog([]));
        var scheduleIds = new[]
        {
            "relisten-ingest-discover",
            "relisten-ingest-full",
            "relisten-ingest-thin",
        };

        await client.RegisterSchedulesAsync();

        try
        {
            foreach (var id in scheduleIds)
            {
                var schedule = environment.Client.GetScheduleHandle(id);
                var description = await schedule.DescribeAsync();
                description.Schedule.Action.Should().BeOfType<ScheduleActionStartWorkflow>();

                // Keep the test independent of the wall-clock cron boundary.
                await schedule.PauseAsync("Test operator pause");
            }

            await client.RegisterSchedulesAsync();
            var thin = environment.Client.GetScheduleHandle("relisten-ingest-thin");
            (await thin.DescribeAsync()).Schedule.State.Paused.Should().BeTrue();

            var activities = new ScheduledBatchActivities();
            using var workflows = new TemporalWorker(
                environment.Client,
                new TemporalWorkerOptions(
                    IngestionQueues.Workflows
                ).AddWorkflow<RefreshBatchWorkflow>()
            );
            using var database = new TemporalWorker(
                environment.Client,
                new TemporalWorkerOptions(IngestionQueues.Database).AddAllActivities(activities)
            );

            await workflows.ExecuteAsync(() =>
                database.ExecuteAsync(async () =>
                {
                    // Triggering a paused schedule is an explicit operator action.
                    await thin.TriggerAsync();
                    var workflowId = await activities.Started.Task.WaitAsync(
                        TimeSpan.FromSeconds(20)
                    );
                    var result = await environment
                        .Client.GetWorkflowHandle(workflowId)
                        .GetResultAsync<ImportBatchResult>()
                        .WaitAsync(TimeSpan.FromSeconds(20));

                    activities.Request.Should().BeOfType<RefreshRecentlyActiveArtists>();
                    result.Should().Be(new ImportBatchResult());
                })
            );
        }
        finally
        {
            foreach (var id in scheduleIds)
            {
                await environment.Client.GetScheduleHandle(id).DeleteAsync();
            }
        }
    }

    public sealed class ScheduledBatchActivities
    {
        public TaskCompletionSource<string> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CatalogBatchCommand? Request { get; private set; }

        [Activity("Ingestion.SelectArtists")]
        public ArtistId[] SelectArtists(CatalogBatchCommand request, IngestionExecution execution)
        {
            Request = request;
            Started.TrySetResult(ActivityExecutionContext.Current.Info.WorkflowId!);
            return [];
        }
    }

    private TemporalIngestionClient Client(IImportAdmissionCatalog catalog) =>
        new(environment.Client, new ConfigurationBuilder().Build(), catalog);

    private static async Task<(ImportHandle<ArtistImportResult>? Handle, Exception? Error)> Capture(
        Func<Task<ImportHandle<ArtistImportResult>>> start
    )
    {
        try
        {
            return (await start(), null);
        }
        catch (Exception error)
        {
            return (null, error);
        }
    }

    private sealed class Catalog(UpstreamProvider[]? providers, bool cohort = false)
        : IImportAdmissionCatalog
    {
        public Task<bool> IsTapersSectionCohortArtistAsync(ArtistId artist) =>
            Task.FromResult(cohort);

        public Task<UpstreamProvider[]?> ProvidersForArtistAsync(ArtistId artist) =>
            Task.FromResult(providers);
    }
}
