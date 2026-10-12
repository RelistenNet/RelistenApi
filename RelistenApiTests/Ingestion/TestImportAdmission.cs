using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Relisten.Api.Models;
using Relisten.Ingestion;
using Temporalio.Testing;

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
        var owner = await client.RefreshArtistAsync(new RefreshArtist(artist));
        try
        {
            Func<Task> targeted = async () =>
                await client.Archive.ImportItemAsync(
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
    public async Task SimultaneousStartsAdmitExactlyOneOperation()
    {
        var artist = new ArtistId(Guid.NewGuid());
        var client = Client(new Catalog([UpstreamProvider.ArchiveOrg]));
        var starts = new[]
        {
            Capture(() => client.RefreshArtistAsync(new RefreshArtist(artist))),
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
        var first = await client.RefreshArtistAsync(new RefreshArtist(artist));
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
            await Client(new Catalog(null)).RefreshArtistAsync(new RefreshArtist(artist));
        Func<Task> unsupported = async () =>
            await Client(new Catalog([UpstreamProvider.Local]))
                .Archive.ImportItemAsync(new ImportArchiveItem(artist, new ArchiveItemId("one")));
        await missing.Should().ThrowAsync<ArtistNotFound>();
        await unsupported.Should().ThrowAsync<UnsupportedImportOperation>();
    }

    [TestCase(0)]
    [TestCase(10000)]
    public async Task InvalidYearsAreRejectedBeforeAdmission(int year)
    {
        var client = Client(new Catalog([UpstreamProvider.ArchiveOrg]));
        Func<Task> start = async () =>
            await client.Archive.RefreshAsync(
                new RefreshArchiveArtist(new ArtistId(Guid.NewGuid()), year)
            );
        await start.Should().ThrowAsync<InvalidImportRequest>();
    }

    [TestCase("")]
    [TestCase(" ")]
    public async Task MissingTargetedItemNeverStartsAFullRefresh(string item)
    {
        var client = Client(new Catalog([UpstreamProvider.ArchiveOrg]));
        Func<Task> start = async () =>
            await client.Archive.ImportItemAsync(
                new ImportArchiveItem(new ArtistId(Guid.NewGuid()), new ArchiveItemId(item))
            );
        await start.Should().ThrowAsync<InvalidImportRequest>();
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

    private sealed class Catalog(UpstreamProvider[]? providers) : IImportAdmissionCatalog
    {
        public Task<UpstreamProvider[]?> ProvidersForArtistAsync(ArtistId artist) =>
            Task.FromResult(providers);
    }
}
