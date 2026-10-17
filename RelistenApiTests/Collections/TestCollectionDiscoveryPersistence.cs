using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Relisten;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.Archive;
using Relisten.Ingestion.Providers.ArchiveCollections;

namespace RelistenApiTests.Collections;

/// <summary>
/// Exercises PostgreSQL checkpoints independently of workflow history. Discovery retains
/// membership and unfinished observations; it never publishes artists or recordings.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public class TestCollectionDiscoveryPersistence
{
    private DbService db = null!;
    private CollectionService collections = null!;
    private CollectionDiscoveryService discovery = null!;
    private string originalWrite = null!;
    private string originalRead = null!;
    private CollectionSelectorDefinition selector = null!;
    private static DateTimeOffset Deadline => DateTimeOffset.UtcNow.AddMinutes(1);

    [OneTimeSetUp]
    public async Task Start()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run ./test-local.sh to include local integration tests.");
        }
        originalWrite = DbService.ConnStr;
        originalRead = DbService.ReadOnlyConnStr;
        db = new DbService(
            LocalTestDatabase.DatabaseUrl,
            null,
            null,
            Host.CreateApplicationBuilder().Environment,
            NullLogger<DbService>.Instance
        );
        collections = new CollectionService(db);
        discovery = new CollectionDiscoveryService(db, new CatalogImportTransaction(db));
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "INSERT INTO upstream_sources(id,name,url) VALUES(1,'archive.org','https://archive.org') ON CONFLICT DO NOTHING"
            )
        );
    }

    [SetUp]
    public async Task CreateSelector()
    {
        var collectionId = Guid.NewGuid();
        await collections.SaveCollectionAsync(
            new(
                collectionId,
                "discovery-" + collectionId.ToString("N"),
                "Discovery fixture",
                "Checkpoint contract",
                true
            )
        );
        selector = new(Guid.NewGuid(), collectionId, 1, "selector-" + Guid.NewGuid().ToString("N"));
        await collections.SaveSelectorAsync(selector);
    }

    [OneTimeTearDown]
    public void RestoreDatabaseSettings()
    {
        if (originalWrite != null)
        {
            DbService.ConnStr = originalWrite;
            DbService.ReadOnlyConnStr = originalRead;
        }
    }

    [Test]
    public async Task WholeContinuationPageAndMembershipsSurviveLostReplyBeforeBatchAcknowledgement()
    {
        var initial = await discovery.BeginOrResumeAsync(selector.Uuid, Deadline);
        var ids = Enumerable
            .Range(0, 5000)
            .Select(index => new ArchiveItemId("large-" + selector.Uuid + "-" + index))
            .ToArray();
        var page = new ArchiveCollectionSearchPage(ids, "page-a", 10000);
        var committed = await discovery.ApplyPageAsync(initial, page, Deadline);
        committed.PendingItems.Should().Equal(ids.Select(item => item.Value));
        committed.Discovered.Should().Be(5000);
        committed.Cursor.Should().Be("page-a");
        (await collections.GetProviderMembershipAsync(selector.Uuid, ids.Last().Value))!
            .active.Should()
            .BeTrue();

        // The first response was lost. A fresh activity retries the same page and
        // reads the already committed cursor and all still-pending observations.
        var restarted = new CollectionDiscoveryService(db, new CatalogImportTransaction(db));
        var repeated = await restarted.ApplyPageAsync(initial, page, Deadline);
        repeated.Should().BeEquivalentTo(committed);
        var firstBatch = committed.PendingItems.Take(100).ToArray();
        var acknowledged = await restarted.AcknowledgeItemsAsync(committed, firstBatch, Deadline);
        acknowledged.PendingItems.Should().Equal(ids.Skip(100).Select(item => item.Value));
        (await restarted.AcknowledgeItemsAsync(committed, firstBatch, Deadline))
            .Should()
            .BeEquivalentTo(acknowledged);
        (await restarted.BeginOrResumeAsync(selector.Uuid, Deadline))
            .Should()
            .BeEquivalentTo(acknowledged);

        var prematureItem = "must-not-advance-" + selector.Uuid;
        var premature = await restarted.ApplyPageAsync(
            acknowledged,
            new([new ArchiveItemId(prematureItem)], "page-b", 1),
            Deadline
        );
        premature.Should().BeEquivalentTo(acknowledged);
        (await collections.GetProviderMembershipAsync(selector.Uuid, prematureItem))
            .Should()
            .BeNull();
        (await restarted.CompleteAsync(acknowledged, Deadline)).Completed.Should().BeFalse();
    }

    [Test]
    public async Task OverlapIsDeduplicatedAndInvalidContinuationRollsBackWithoutLosingTheCheckpoint()
    {
        var first = "first-" + selector.Uuid;
        var second = "second-" + selector.Uuid;
        var third = "third-" + selector.Uuid;
        var fourth = "fourth-" + selector.Uuid;
        var state = await discovery.BeginOrResumeAsync(selector.Uuid, Deadline);
        state = await discovery.ApplyPageAsync(state, Page([first, second], "page-a"), Deadline);
        state = await discovery.AcknowledgeItemsAsync(state, state.PendingItems, Deadline);
        state = await discovery.ApplyPageAsync(state, Page([second, third], "page-b"), Deadline);
        state.Discovered.Should().Be(3);
        state.PendingItems.Should().Equal(third);
        state = await discovery.AcknowledgeItemsAsync(state, state.PendingItems, Deadline);

        Func<Task> repeatedCursor = () =>
            discovery.ApplyPageAsync(state, Page([fourth], "page-a"), Deadline);
        Func<Task> noFreshItems = () =>
            discovery.ApplyPageAsync(state, Page([second], "page-c"), Deadline);
        await repeatedCursor.Should().ThrowAsync<InvalidDataException>();
        await noFreshItems.Should().ThrowAsync<InvalidDataException>();
        (await discovery.BeginOrResumeAsync(selector.Uuid, Deadline))
            .Should()
            .BeEquivalentTo(state);
        (await collections.GetProviderMembershipAsync(selector.Uuid, fourth)).Should().BeNull();

        state = await discovery.ApplyPageAsync(state, Page([fourth], null), Deadline);
        state.SearchComplete.Should().BeTrue();
        (await discovery.CompleteAsync(state, Deadline)).Completed.Should().BeFalse();
        state = await discovery.AcknowledgeItemsAsync(state, state.PendingItems, Deadline);
        var completed = await discovery.CompleteAsync(state, Deadline);
        completed.Completed.Should().BeTrue();
        (await discovery.CompleteAsync(state, Deadline)).Should().BeEquivalentTo(completed);

        var nextScan = await discovery.BeginOrResumeAsync(selector.Uuid, Deadline);
        nextScan.ScanId.Should().NotBe(completed.ScanId);
        nextScan.Discovered.Should().Be(0);
        var staleItem = "old-scan-reply-" + selector.Uuid;
        (await discovery.ApplyPageAsync(state, Page([staleItem], null), Deadline))
            .Should()
            .BeEquivalentTo(nextScan);
        (await collections.GetProviderMembershipAsync(selector.Uuid, staleItem)).Should().BeNull();
        foreach (var item in new[] { first, second, third, fourth })
        {
            (await collections.GetProviderMembershipAsync(selector.Uuid, item))!
                .active.Should()
                .BeTrue();
        }
    }

    [Test]
    public async Task DisabledSelectorPreservesPendingWorkAndResumesTheSameScanAfterReenable()
    {
        var item = "paused-" + selector.Uuid;
        var state = await discovery.BeginOrResumeAsync(selector.Uuid, Deadline);
        state = await discovery.ApplyPageAsync(state, Page([item], "page-a"), Deadline);
        await collections.SaveSelectorAsync(selector with { Enabled = false });
        var paused = await discovery.BeginOrResumeAsync(selector.Uuid, Deadline);
        paused.Enabled.Should().BeFalse();
        paused.ScanId.Should().Be(state.ScanId);
        paused.PendingItems.Should().Equal(item);
        var unseen = "while-paused-" + selector.Uuid;
        (await discovery.ApplyPageAsync(paused, Page([unseen], "page-b"), Deadline))
            .Should()
            .BeEquivalentTo(paused);
        (await collections.GetProviderMembershipAsync(selector.Uuid, unseen)).Should().BeNull();

        await collections.SaveSelectorAsync(selector with { Enabled = true });
        var resumed = await discovery.BeginOrResumeAsync(selector.Uuid, Deadline);
        resumed.ScanId.Should().Be(state.ScanId);
        resumed.PendingItems.Should().Equal(item);
        resumed.Cursor.Should().Be("page-a");
        resumed = await discovery.AcknowledgeItemsAsync(resumed, [item], Deadline);
        resumed = await discovery.ApplyPageAsync(resumed, Page([], null), Deadline);
        (await discovery.CompleteAsync(resumed, Deadline)).Completed.Should().BeTrue();
        (await collections.RecordingsAsync(selector.CollectionUuid))!.items.Should().BeEmpty();
    }

    [Test]
    public async Task RediscoveryPreservesOperatorRetirementAndAbsenceNeverRetiresAnotherMembership()
    {
        var retired = "operator-retired-" + selector.Uuid;
        var absent = "absent-" + selector.Uuid;
        await collections.SetProviderMembershipAsync(
            selector.Uuid,
            retired,
            false,
            "Operator retirement"
        );
        await collections.SetProviderMembershipAsync(
            selector.Uuid,
            absent,
            true,
            "Earlier supported observation"
        );
        var state = await discovery.BeginOrResumeAsync(selector.Uuid, Deadline);
        state = await discovery.ApplyPageAsync(state, Page([retired], null), Deadline);
        state = await discovery.AcknowledgeItemsAsync(state, [retired], Deadline);
        (await discovery.CompleteAsync(state, Deadline)).Completed.Should().BeTrue();

        var retained = (await collections.GetProviderMembershipAsync(selector.Uuid, retired))!;
        retained.active.Should().BeFalse();
        retained.reason.Should().Be("Operator retirement");
        (await collections.GetProviderMembershipAsync(selector.Uuid, absent))!
            .active.Should()
            .BeTrue();
        (await collections.RecordingsAsync(selector.CollectionUuid))!.items.Should().BeEmpty();
    }

    [Test]
    public async Task ScanFailureRetainsCommittedPendingItemsAndAStaleFailureCannotRewriteTheNextScan()
    {
        var item = "failed-observation-" + selector.Uuid;
        var state = await discovery.BeginOrResumeAsync(selector.Uuid, Deadline);
        state = await discovery.ApplyPageAsync(state, Page([item], "page-a"), Deadline);
        await discovery.RecordScanFailureAsync(state, "Upstream continuation expired");
        var status = (await collections.GetSelectorAsync(selector.Uuid))!;
        status.last_error.Should().Be("Upstream continuation expired");
        status.scan_state.Should().Be("running");
        status.cursor.Should().Be("page-a");
        var resumed = await discovery.BeginOrResumeAsync(selector.Uuid, Deadline);
        resumed.PendingItems.Should().Equal(item);
        resumed.ScanId.Should().Be(state.ScanId);

        resumed = await discovery.AcknowledgeItemsAsync(resumed, [item], Deadline);
        resumed = await discovery.ApplyPageAsync(resumed, Page([], null), Deadline);
        await discovery.CompleteAsync(resumed, Deadline);
        var next = await discovery.BeginOrResumeAsync(selector.Uuid, Deadline);
        await discovery.RecordScanFailureAsync(state, "Late response from previous scan");
        (await collections.GetSelectorAsync(selector.Uuid))!.last_error.Should().BeNull();
        (await discovery.BeginOrResumeAsync(selector.Uuid, Deadline))
            .ScanId.Should()
            .Be(next.ScanId);
    }

    private static ArchiveCollectionSearchPage Page(string[] items, string? cursor) =>
        new(items.Select(item => new ArchiveItemId(item)).ToArray(), cursor, items.Length);
}
