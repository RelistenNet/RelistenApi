using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Relisten;
using Relisten.Accounts.Contracts.Deletion;
using Relisten.Accounts.Contracts.Identifiers;
using Relisten.Accounts.Contracts.Search;
using Relisten.Search;
using RelistenIngestionWorker.AccountDeletion;
using RelistenIngestionWorker.PlaylistSearch;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Library;
using RelistenUserService.Persistence;
using RelistenUserService.Playlists;
using RelistenUserService.Playlists.Entities;
using RelistenUserService.Search;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;

namespace RelistenUserServiceTests;

[TestFixture, NonParallelizable, FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class TestPlaylistSearchIndex
{
    private PostgresIntegrationDatabase database = null!;
    private string primary = null!;
    private string replica = null!;
    private string alias = null!;
    private BeforeImport beforeImport = null!;
    private HttpClient http = null!;
    private TypesenseClient typesense = null!;
    private IConfiguration configuration = null!;
    private PlaylistSearchIndex index = null!;

    [SetUp]
    public async Task SetUp()
    {
        primary = DbService.ConnStr;
        replica = DbService.ReadOnlyConnStr;
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Requires local Typesense and PostgreSQL.");
        }

        database = new PostgresIntegrationDatabase();
        await database.StartAsync();
        await database.LoadCatalogSchemaAsync();
        alias = "test_playlists_" + Guid.NewGuid().ToString("N");
        beforeImport = new BeforeImport();
        http = new HttpClient(beforeImport);
        TypesenseClient.Configure(http, "http://127.0.0.1:18108", "local_dev_typesense");
        typesense = new TypesenseClient(http);
        configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["Search:PlaylistAlias"] = alias }
            )
            .Build();
        var catalog = new DbService(
            "postgresql://relisten:local_dev_password@127.0.0.1:15432/relisten_db",
            null,
            null,
            new HostingEnvironment { EnvironmentName = Environments.Production },
            NullLogger<DbService>.Instance
        );
        index = new PlaylistSearchIndex(
            new(database.ConnectionString, catalog),
            typesense,
            configuration
        );
    }

    [Test]
    public async Task FullBuildRanksYearlyPlaysAndRenamePreservesThePercentile()
    {
        var owner = await database.CreateUserAsync("percentileowner");
        var follower = await database.CreateUserAsync("percentilefollower");
        var popular = await Seed(database, owner, "Popular playlist");
        var quiet = await Seed(database, owner, "Quiet playlist");

        await using var db = database.CreateContext();
        // The quiet playlist sorts first in a 30-day directory and has more followers.
        db.PlaylistFollows.Add(new PlaylistFollow { UserId = follower, PlaylistId = quiet });
        await db.SaveChangesAsync();
        await SeedPopularity(db, popular, yearly: 100, monthly: 1);
        await SeedPopularity(db, quiet, yearly: 40, monthly: 40);
        await index.Rebuild(CancellationToken.None);

        var percentile = (await Read(http, alias, popular))!
            .Value.GetProperty("popularity_percentile")
            .GetDouble();
        var quietPercentile = (await Read(http, alias, quiet))!
            .Value.GetProperty("popularity_percentile")
            .GetDouble();
        percentile.Should().BeGreaterThan(quietPercentile);
        var finder = new PlaylistSearchCandidates(http, configuration);
        var matches = await finder.Find(
            new MergedSearchRequest { Q = "playlist", Scope = "playlists" },
            null,
            CancellationToken.None
        );
        matches.Select(hit => hit.Id).Should().ContainInOrder(popular.Value, quiet.Value);

        var renamed = await db.Playlists.SingleAsync(row => row.Id == popular);
        renamed.Name = "Renamed playlist";
        renamed.Revision++;
        await db.SaveChangesAsync();
        await index.Sync(popular.Value, CancellationToken.None);

        (await Read(http, alias, popular))!
            .Value.GetProperty("popularity_percentile")
            .GetDouble()
            .Should()
            .Be(percentile);
    }

    [Test]
    public async Task PlaylistEditedDuringRebuildRemainsSearchableAfterTheSwap()
    {
        var owner = await database.CreateUserAsync("curator");
        var playlist = await Seed(database, owner, "Before the rebuild");
        await index.Rebuild(CancellationToken.None);

        // The new collection has read the old revision. Complete an incremental
        // sync while the alias still points at the old collection.
        const string title = "Moonlight Reels";
        beforeImport.OnImport = async () =>
        {
            await using var db = database.CreateContext();
            var edited = await db.Playlists.SingleAsync(row => row.Id == playlist);
            edited.Name = title;
            edited.Revision++;
            await db.SaveChangesAsync();
            await index.Sync(playlist.Value, CancellationToken.None);
        };

        await using var environment = await WorkflowEnvironment.StartLocalAsync();
        using var worker = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(PlaylistSearchJob.Queue)
                .AddWorkflow<PlaylistSearchRebuildWorkflow>()
                .AddAllActivities(new PlaylistSearchActivities(index))
        );
        await worker.ExecuteAsync(() =>
            environment.Client.ExecuteWorkflowAsync(
                (PlaylistSearchRebuildWorkflow workflow) => workflow.RunAsync(),
                new WorkflowOptions(PlaylistSearchJob.RebuildId, PlaylistSearchJob.Queue)
            )
        );

        await using var searchDb = database.CreateContext();
        var candidates = await new PlaylistSearchCandidates(http, configuration).Find(
            new MergedSearchRequest { Q = title, Scope = "playlists" },
            null,
            CancellationToken.None
        );
        var cards = await new PlaylistSearchCards(searchDb).Read(
            candidates,
            null,
            CancellationToken.None
        );
        cards.Should().ContainSingle(card => card.Id == playlist && card.Label == title);
    }

    [TearDown]
    public async Task TearDown()
    {
        DbService.ConnStr = primary;
        DbService.ReadOnlyConnStr = replica;
        if (database is null)
        {
            return;
        }

        try
        {
            if (typesense is null)
            {
                return;
            }

            await typesense.DeleteAlias(alias, CancellationToken.None);
            foreach (
                var name in (await typesense.ReadCollectionNames(CancellationToken.None)).Where(
                    name => name.StartsWith(alias + "_", StringComparison.Ordinal)
                )
            )
            {
                await typesense.DeleteCollection(name, CancellationToken.None);
            }
        }
        finally
        {
            http?.Dispose();
            await database.StopAsync();
        }
    }

    [Test]
    public async Task APlaylistUpsertCannotRestoreAPurgedAccount()
    {
        var owner = await database.CreateUserAsync("erasedcurator");
        var playlist = await Seed(database, owner, "Purge during indexing");
        await index.Rebuild(CancellationToken.None);
        (await Read(http, alias, playlist)).Should().NotBeNull();

        // The indexer has already read the document when purge commits.
        beforeImport.OnImport = () => Purge().RunForUserAsync(owner.Value, CancellationToken.None);
        await index.Sync(playlist.Value, CancellationToken.None);

        (await Read(http, alias, playlist)).Should().BeNull();
    }

    [Test]
    public async Task RetryingACommittedPurgeRemovesItsSearchDocument()
    {
        var owner = await database.CreateUserAsync("removedcurator");
        var playlist = await Seed(database, owner, "Retry an index deletion");
        await index.Rebuild(CancellationToken.None);

        var job = new AccountDeletionInput(Guid.CreateVersion7(), owner.Value);
        await using var db = database.CreateContext();
        db.AccountDeletionJobs.Add(
            new AccountDeletionJob
            {
                Id = job.JobId,
                UserId = owner,
                RequestedAt = DateTimeOffset.UtcNow,
            }
        );
        await db.SaveChangesAsync();
        var failedPurge = new AccountPurge(
            database.ConnectionString,
            (_, _) => throw new HttpRequestException()
        );
        Assert.ThrowsAsync<HttpRequestException>(() =>
            failedPurge.RunJobAsync(job, CancellationToken.None)
        );
        (await db.Playlists.AnyAsync(row => row.OwnerId == owner)).Should().BeFalse();
        (await Read(http, alias, playlist)).Should().NotBeNull();

        await Purge().RunJobAsync(job, CancellationToken.None);

        (await Read(http, alias, playlist)).Should().BeNull();
    }

    [Test]
    public async Task EditsCoalesceSeparatelyForEachPlaylist()
    {
        var owner = await database.CreateUserAsync("curator");
        var first = await Seed(database, owner, "Before the edit");
        var second = await Seed(database, owner, "Public playlist");
        await index.Rebuild(CancellationToken.None);

        await RunEditBurst(database, index, first, second);

        (await Read(http, alias, first))!
            .Value.GetProperty("name")
            .GetString()
            .Should()
            .Be("Final edit");
        var moderated = (await Read(http, alias, second))!.Value;
        moderated.GetProperty("taken_down").GetBoolean().Should().BeTrue();
        moderated.GetProperty("curator").GetString().Should().BeEmpty();
    }

    private AccountPurge Purge() =>
        new(
            database.ConnectionString,
            (id, token) => PlaylistSearchIndex.Purge(typesense, alias, id, token)
        );

    private static async Task RunEditBurst(
        PostgresIntegrationDatabase database,
        PlaylistSearchIndex index,
        PlaylistId first,
        PlaylistId second
    )
    {
        await using var environment = await WorkflowEnvironment.StartLocalAsync();
        using var worker = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(PlaylistSearchJob.Queue)
                .AddWorkflow<PlaylistSearchSyncWorkflow>()
                .AddAllActivities(new PlaylistSearchActivities(index))
        );
        await worker.ExecuteAsync(async () =>
        {
            var notifier = new PlaylistSearchNotifier(
                environment.Client,
                NullLogger<PlaylistSearchNotifier>.Instance
            );
            await using var db = database.CreateContext();
            var edited = await db.Playlists.SingleAsync(row => row.Id == first);
            edited.Name = "Intermediate edit";
            await db.SaveChangesAsync();
            await notifier.Changed(first);
            await Task.Delay(TimeSpan.FromSeconds(2));
            edited.Name = "Final edit";
            edited.Revision++;
            await db.SaveChangesAsync();
            await notifier.Changed(first);

            var moderator = new PlaylistModerationService(
                db,
                new LibraryStateStore(db),
                TimeProvider.System,
                notifier
            );
            (await moderator.SetTakedownAsync(second, true, CancellationToken.None))
                .Should()
                .BeTrue();

            var firstRun = environment.Client.GetWorkflowHandle(
                PlaylistSearchJob.SyncId(first.Value)
            );
            var secondRun = environment.Client.GetWorkflowHandle(
                PlaylistSearchJob.SyncId(second.Value)
            );
            await Task.WhenAll(firstRun.GetResultAsync(), secondRun.GetResultAsync())
                .WaitAsync(TimeSpan.FromSeconds(85));
            foreach (var run in new[] { firstRun, secondRun })
            {
                var events = (await run.FetchHistoryAsync()).Events;
                var reindex = events
                    .Should()
                    .ContainSingle(e => e.ActivityTaskScheduledEventAttributes != null)
                    .Subject;
                var lastEdit = events.Last(e =>
                    e.WorkflowExecutionSignaledEventAttributes is not null
                );
                (reindex.EventTime.ToDateTime() - lastEdit.EventTime.ToDateTime())
                    .Should()
                    .BeGreaterThan(TimeSpan.FromSeconds(59));
            }
        });
    }

    private static Task SeedPopularity(
        AccountsDbContext db,
        PlaylistId playlist,
        int yearly,
        int monthly
    )
    {
        var empty = new PopularityWindowResource(0, 0, 0);
        var resource = new PopularityResource(
            0,
            0,
            new(empty, empty, new(monthly, 0, 0), new(yearly, 0, 0))
        );
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO public.playlist_popularity (playlist_id, as_of, popularity)
            VALUES ({playlist.Value}, now(), {JsonSerializer.Serialize(resource)}::jsonb)
            """
        );
    }

    private static async Task<PlaylistId> Seed(
        PostgresIntegrationDatabase database,
        UserId owner,
        string name
    )
    {
        await using var db = database.CreateContext();
        var id = PlaylistId.New();
        var now = DateTimeOffset.UtcNow;
        db.Playlists.Add(
            new Playlist
            {
                Id = id,
                OwnerId = owner,
                Name = name,
                Revision = 1,
                CreatedAt = now,
                UpdatedAt = now,
                PublicCode = Guid.NewGuid().ToString("N"),
                Published = true,
            }
        );
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<JsonElement?> Read(HttpClient http, string alias, PlaylistId id)
    {
        using var response = await http.GetAsync($"collections/{alias}/documents/{id}");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        using var result = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        return result.RootElement.Clone();
    }

    private sealed class BeforeImport() : DelegatingHandler(new HttpClientHandler())
    {
        public Func<Task>? OnImport { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (
                OnImport is { } run
                && request.RequestUri!.AbsolutePath.EndsWith(
                    "/documents/import",
                    StringComparison.Ordinal
                )
            )
            {
                OnImport = null;
                await run();
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
