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
using RelistenUserService.Playlists;
using RelistenUserService.Playlists.Entities;
using RelistenUserService.Search;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;

namespace RelistenUserServiceTests;

[TestFixture, NonParallelizable]
public sealed class TestPlaylistSearchIndex
{
    [Test]
    public async Task SignalsCoalesceByPlaylistAndPurgeRetriesAfterCommit()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Requires local Typesense and PostgreSQL.");
        }

        var database = new PostgresIntegrationDatabase();
        await database.StartAsync();
        var primary = DbService.ConnStr;
        var replica = DbService.ReadOnlyConnStr;
        var alias = "test_playlists_" + Guid.NewGuid().ToString("N");
        var beforeImport = new BeforeImport();
        using var http = new HttpClient(beforeImport);
        TypesenseClient.Configure(http, "http://127.0.0.1:18108", "local_dev_typesense");
        var typesense = new TypesenseClient(http);
        try
        {
            var config = new ConfigurationBuilder()
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
            var index = new PlaylistSearchIndex(
                new(database.ConnectionString, catalog),
                typesense,
                config
            );
            var owner = await database.CreateUserAsync("curator");
            var first = await Seed(database, owner, "Before the edit");
            var second = await Seed(database, owner, "Public playlist");
            await index.Rebuild(CancellationToken.None);
            var erasedOwner = await database.CreateUserAsync("erasedcurator");
            var erased = await Seed(database, erasedOwner, "Purge during indexing");
            beforeImport.OnImport = () =>
                new AccountPurge(
                    database.ConnectionString,
                    (id, token) => PlaylistSearchIndex.Purge(typesense, alias, id, token)
                ).RunForUserAsync(erasedOwner.Value, CancellationToken.None);
            await index.Sync(erased.Value, CancellationToken.None);
            (await Read(http, alias, erased)).Should().BeNull();

            await RunEditBurst(database, index, first, second);
            (await Read(http, alias, first))!
                .Value.GetProperty("name")
                .GetString()
                .Should()
                .Be("Final edit");
            var moderated = (await Read(http, alias, second))!.Value;
            moderated.GetProperty("taken_down").GetBoolean().Should().BeTrue();
            moderated.GetProperty("curator").GetString().Should().BeEmpty();

            await PurgeAfterFailedIndexCleanup(database, typesense, alias, owner);
            (await Read(http, alias, first)).Should().BeNull();
            (await Read(http, alias, second)).Should().BeNull();
        }
        finally
        {
            DbService.ConnStr = primary;
            DbService.ReadOnlyConnStr = replica;
            await typesense.DeleteAlias(alias, CancellationToken.None);
            foreach (
                var name in (await typesense.ReadCollectionNames(CancellationToken.None)).Where(
                    name => name.StartsWith(alias + "_", StringComparison.Ordinal)
                )
            )
            {
                await typesense.DeleteCollection(name, CancellationToken.None);
            }
            await database.StopAsync();
        }
    }

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

    private static async Task PurgeAfterFailedIndexCleanup(
        PostgresIntegrationDatabase database,
        TypesenseClient typesense,
        string alias,
        UserId owner
    )
    {
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
        await new AccountPurge(
            database.ConnectionString,
            (id, token) => PlaylistSearchIndex.Purge(typesense, alias, id, token)
        ).RunJobAsync(job, CancellationToken.None);
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
