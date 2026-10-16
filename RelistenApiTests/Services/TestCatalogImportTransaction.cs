using System.Transactions;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Relisten;
using Relisten.Api.Models;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>
/// Exercises the real Npgsql ambient transaction and public catalog services against
/// an isolated schema. The test setup creates its database on the normal local PostgreSQL server.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("LocalIngestion")]
public class TestCatalogImportTransaction
{
    private readonly string schema = $"catalog_transaction_test_{Guid.NewGuid():N}";
    private string adminConnectionString = "";
    private string connectionString = "";
    private string previousWrite = "";
    private string previousRead = "";
    private bool schemaCreated;
    private DbService db = null!;
    private CatalogImportTransaction transactions = null!;

    [OneTimeSetUp]
    public async Task CreateIsolatedSchema()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
            Assert.Ignore("Run ./test-local.sh to include local integration tests.");

        adminConnectionString = LocalTestDatabase.ConnectionString;
        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync();
        SqlMapper.AddTypeHandler(new PersistentIdentifierHandler());
        SqlMapper.AddTypeHandler(new DateTimeHandler());

        previousWrite = DbService.ConnStr;
        previousRead = DbService.ReadOnlyConnStr;
        await admin.ExecuteAsync($"CREATE SCHEMA {schema}");
        schemaCreated = true;
        connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            SearchPath = schema,
            ApplicationName = schema,
        }.ConnectionString;
        db = new DbService(
            LocalTestDatabase.DatabaseUrl,
            null,
            null,
            new ProductionEnvironment(),
            NullLogger<DbService>.Instance
        );
        DbService.ConnStr = connectionString;
        // Ambient reads must use the primary even when the replica cannot be reached.
        DbService.ReadOnlyConnStr = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Port = 1,
            Timeout = 1,
        }.ConnectionString;
        transactions = new CatalogImportTransaction(db);
        await using var connection = await OpenConnection();
        // These tables reproduce the columns and constraints used by the public services
        // below, including the provider identity used by the setlist-show upsert.
        await connection.ExecuteAsync(
            """
            CREATE TABLE setlist_shows (
                id serial PRIMARY KEY, artist_id integer NOT NULL, venue_id integer NOT NULL,
                tour_id integer, date date NOT NULL, upstream_identifier text NOT NULL,
                updated_at timestamptz NOT NULL, uuid uuid NOT NULL UNIQUE,
                UNIQUE (artist_id, upstream_identifier)
            );
            CREATE TABLE setlist_songs_plays (
                played_setlist_song_id integer NOT NULL,
                played_setlist_show_id integer NOT NULL REFERENCES setlist_shows(id),
                CONSTRAINT setlist_songs_plays_song_id_show_id_key
                    UNIQUE (played_setlist_song_id, played_setlist_show_id)
            );
            """
        );
    }

    [SetUp]
    public async Task ClearRows()
    {
        await using var connection = await OpenConnection();
        await connection.ExecuteAsync(
            "TRUNCATE setlist_songs_plays, setlist_shows RESTART IDENTITY"
        );
    }

    [OneTimeTearDown]
    public async Task DropIsolatedSchema()
    {
        if (!schemaCreated)
            return;

        DbService.ConnStr = previousWrite;
        DbService.ReadOnlyConnStr = previousRead;
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
    }

    [Test]
    public async Task SequentialServiceCallsReuseTheTransactionalPrimaryConnection()
    {
        var shows = new SetlistShowService(db);
        var artist = new Artist { id = 42 };
        var saved = await transactions.RunAsync(
            FutureDeadline,
            CancellationToken.None,
            async () =>
            {
                var firstPid = await db.WithConnection(c =>
                    c.QuerySingleAsync<int>("SELECT pg_backend_pid()")
                );
                var show = await shows.Save(NewShow());
                var listed = await shows.AllForArtist(artist);
                listed.Single().id.Should().Be(show.id);
                var secondPid = await db.WithConnection(c =>
                    c.QuerySingleAsync<int>("SELECT pg_backend_pid()")
                );
                secondPid.Should().Be(firstPid);
                Transaction
                    .Current!.TransactionInformation.DistributedIdentifier.Should()
                    .Be(Guid.Empty);
                return show;
            }
        );

        await using var connection = await OpenConnection();
        (await connection.QuerySingleAsync<int>("SELECT id FROM setlist_shows"))
            .Should()
            .Be(saved.id);
    }

    [Test]
    public async Task FailedCompoundWriteRollsBackAllCatalogChanges()
    {
        var shows = new SetlistShowService(db);
        Func<Task> apply = () =>
            transactions.RunAsync<int>(
                FutureDeadline,
                CancellationToken.None,
                async () =>
                {
                    var show = await shows.Save(NewShow());
                    await shows.UpdateSongPlays(show, [new SetlistSong { id = 17 }]);
                    throw new InvalidOperationException("A later catalog write failed.");
                }
            );

        await apply.Should().ThrowAsync<InvalidOperationException>();
        await AssertCatalogEmpty();
    }

    [Test]
    public async Task CancellationDuringApplyPreventsCommit()
    {
        using var cancellation = new CancellationTokenSource();
        Func<Task> apply = () =>
            transactions.RunAsync(
                FutureDeadline,
                cancellation.Token,
                async () =>
                {
                    var saved = await new SetlistShowService(db).Save(NewShow());
                    cancellation.Cancel();
                    return saved;
                }
            );

        await apply.Should().ThrowAsync<OperationCanceledException>();
        await AssertCatalogEmpty();
    }

    [Test]
    public async Task ExpirationDuringApplyPreventsCommit()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(1);
        Func<Task> apply = () =>
            transactions.RunAsync(
                deadline,
                CancellationToken.None,
                async () =>
                {
                    var saved = await new SetlistShowService(db).Save(NewShow());
                    await Task.Delay(TimeSpan.FromSeconds(1.1));
                    return saved;
                }
            );

        await apply.Should().ThrowAsync<CatalogImportExpiredException>();
        await AssertCatalogEmpty();
    }

    [Test]
    public async Task DifferentShowsForOneArtistCanCommitConcurrently()
    {
        var shows = new SetlistShowService(db);
        var firstInserted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var first = transactions.RunAsync(
            FutureDeadline,
            CancellationToken.None,
            async () =>
            {
                var saved = await shows.Save(NewShow());
                firstInserted.SetResult();
                await releaseFirst.Task;
                return saved;
            }
        );
        await firstInserted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var differentShow = NewShow();
        differentShow.upstream_identifier = "show-456";
        try
        {
            // Commit a different show while the first transaction is still open. An
            // artist-wide lock would block this write until the first callback returns.
            var second = await transactions
                .RunAsync(FutureDeadline, CancellationToken.None, () => shows.Save(differentShow))
                .WaitAsync(TimeSpan.FromSeconds(5));
            first.IsCompleted.Should().BeFalse();

            await using var connection = await OpenConnection();
            (await connection.QuerySingleAsync<int>("SELECT id FROM setlist_shows"))
                .Should()
                .Be(second.id);
        }
        finally
        {
            releaseFirst.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await using var committed = await OpenConnection();
        (await committed.QuerySingleAsync<int>("SELECT count(*) FROM setlist_shows"))
            .Should()
            .Be(2);
    }

    [Test]
    public async Task CollectionPagesSerializeWithoutAnArtistId()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = transactions.RunCollectionPageAsync(
            FutureDeadline,
            CancellationToken.None,
            async () =>
            {
                entered.SetResult();
                await release.Task;
                return 1;
            }
        );
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = transactions.RunCollectionPageAsync(
            FutureDeadline,
            CancellationToken.None,
            () => Task.FromResult(2)
        );
        try
        {
            await WaitForDatabaseLockWait("advisory");
            second.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
        }
        (await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5))).Should().Equal(1, 2);
    }

    [Test]
    public async Task CancellationWhileWaitingForCollectionLockNeverAppliesWrites()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = transactions.RunCollectionPageAsync(
            FutureDeadline,
            CancellationToken.None,
            async () =>
            {
                entered.SetResult();
                await release.Task;
                return 1;
            }
        );
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var callbackEntered = false;
        var second = transactions.RunCollectionPageAsync(
            FutureDeadline,
            cancellation.Token,
            async () =>
            {
                callbackEntered = true;
                return await new SetlistShowService(db).Save(NewShow());
            }
        );
        try
        {
            await WaitForDatabaseLockWait("advisory");
            cancellation.Cancel();
            Func<Task> waitForSecond = async () => await second.WaitAsync(TimeSpan.FromSeconds(5));
            await waitForSecond.Should().ThrowAsync<OperationCanceledException>();
            callbackEntered.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await AssertCatalogEmpty();
    }

    [Test]
    public async Task RepeatedSetlistInsertPreservesIdentityAndPerformanceLinks()
    {
        var shows = new SetlistShowService(db);
        var original = await transactions.RunAsync(
            FutureDeadline,
            CancellationToken.None,
            async () =>
            {
                var saved = await shows.Save(NewShow());
                await shows.UpdateSongPlays(saved, [new SetlistSong { id = 17 }]);
                return saved;
            }
        );
        var repeated = NewShow();
        repeated.date = new DateTime(2024, 7, 21);
        repeated.venue_id = 8;
        var updated = await transactions.RunAsync(
            FutureDeadline,
            CancellationToken.None,
            () => shows.Save(repeated)
        );
        updated.id.Should().Be(original.id);
        updated.uuid.Should().Be(original.uuid);
        updated.date.Should().Be(repeated.date);
        updated.venue_id.Should().Be(8);
        await using var connection = await OpenConnection();
        (
            await connection.QuerySingleAsync<int>(
                "SELECT played_setlist_show_id FROM setlist_songs_plays"
            )
        )
            .Should()
            .Be(original.id);
        (await connection.QuerySingleAsync<int>("SELECT count(*) FROM setlist_shows"))
            .Should()
            .Be(1);
    }

    [Test]
    public async Task OverlappingShowImportsPreserveIdentityAndReplaceSongPlaysTogether()
    {
        var shows = new SetlistShowService(db);
        var inserted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = transactions.RunAsync(
            FutureDeadline,
            CancellationToken.None,
            async () =>
            {
                var saved = await shows.Save(NewShow());
                await shows.UpdateSongPlays(saved, [new SetlistSong { id = 17 }]);
                inserted.SetResult();
                await release.Task;
                return saved;
            }
        );
        await inserted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Both attempts enter Save with id=0. The second must resolve the row the
        // first committed, even when its caller prepared an insert before waiting.
        var repeatedInsert = NewShow();
        var second = transactions.RunAsync(
            FutureDeadline,
            CancellationToken.None,
            async () =>
            {
                var saved = await shows.Save(repeatedInsert);
                await shows.UpdateSongPlays(saved, [new SetlistSong { id = 23 }]);
                return saved;
            }
        );
        try
        {
            await WaitForDatabaseLockWait("transactionid");
            second.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
        }
        var saved = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        saved[1].id.Should().Be(saved[0].id);
        saved[1].uuid.Should().Be(saved[0].uuid);
        await using var connection = await OpenConnection();
        (await connection.QuerySingleAsync<int>("SELECT count(*) FROM setlist_shows"))
            .Should()
            .Be(1);
        (
            await connection.QuerySingleAsync<int>(
                "SELECT played_setlist_song_id FROM setlist_songs_plays"
            )
        )
            .Should()
            .Be(23);
    }

    private static DateTimeOffset FutureDeadline => DateTimeOffset.UtcNow.AddMinutes(1);

    private static SetlistShow NewShow() =>
        new()
        {
            artist_id = 42,
            venue_id = 7,
            upstream_identifier = "show-123",
            date = new DateTime(2024, 7, 20),
            updated_at = DateTime.UtcNow,
        };

    private async Task AssertCatalogEmpty()
    {
        await using var connection = await OpenConnection();
        (await connection.QuerySingleAsync<int>("SELECT count(*) FROM setlist_shows"))
            .Should()
            .Be(0);
        (await connection.QuerySingleAsync<int>("SELECT count(*) FROM setlist_songs_plays"))
            .Should()
            .Be(0);
    }

    private async Task WaitForDatabaseLockWait(string waitEvent)
    {
        await using var connection = await OpenConnection();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (
            !await connection.QuerySingleAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE application_name = @schema AND wait_event = @waitEvent)",
                new { schema, waitEvent }
            )
        )
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private async Task<NpgsqlConnection> OpenConnection()
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "RelistenApiTests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
