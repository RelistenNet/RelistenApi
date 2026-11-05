using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Relisten;
using Relisten.Catalog;

namespace RelistenApiTests.V4;

/// <summary>
/// Verifies snapshot and connection ownership against the process-owned disposable
/// catalog. These tests never use the application's configured database.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestCatalogReadSession
{
    private DbService db = null!;
    private string previousPrimary = null!;
    private string previousReplica = null!;
    private static readonly Guid Track = Guid.Parse("00000000-0000-0000-0000-000000000044");

    [OneTimeSetUp]
    public async Task Start()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        await using var connection = await MigratedTestDatabase.OpenAsync();
        await V4CatalogFixture.EnsureLoaded(connection);
        previousPrimary = DbService.ConnStr;
        previousReplica = DbService.ReadOnlyConnStr;
        db = new DbService(
            LocalTestDatabase.DatabaseUrl,
            null,
            null,
            new HostingEnvironment { EnvironmentName = Environments.Production },
            NullLogger<DbService>.Instance
        );
        // A second session cannot open if cancellation/disposal leaked the first lease.
        DbService.ConnStr += ";Maximum Pool Size=1";
    }

    [OneTimeTearDown]
    public void Stop()
    {
        DbService.ConnStr = previousPrimary;
        DbService.ReadOnlyConnStr = previousReplica;
    }

    [Test]
    public async Task One_response_keeps_its_snapshot_after_a_concurrent_recording_update()
    {
        await using var writer = await MigratedTestDatabase.OpenAsync();
        var original = await writer.QuerySingleAsync<string>(
            "SELECT title FROM source_tracks WHERE uuid=@Track",
            new { Track }
        );
        try
        {
            await using (var snapshot = await CatalogReadSession.Open(db, CancellationToken.None))
            {
                var first = await Title(snapshot);
                await writer.ExecuteAsync(
                    "UPDATE source_tracks SET title='Changed after snapshot' WHERE uuid=@Track",
                    new { Track }
                );
                (await Title(snapshot)).Should().Be(first);
            }
            await using var next = await CatalogReadSession.Open(db, CancellationToken.None);
            (await Title(next)).Should().Be("Changed after snapshot");
        }
        finally
        {
            await writer.ExecuteAsync(
                "UPDATE source_tracks SET title=@original WHERE uuid=@Track",
                new { original, Track }
            );
        }
    }

    [Test]
    public async Task Read_session_rejects_writes_and_disposal_releases_the_connection()
    {
        await using (var snapshot = await CatalogReadSession.Open(db, CancellationToken.None))
        {
            var write = async () =>
                await snapshot.Connection.ExecuteAsync(
                    snapshot.Command(
                        "UPDATE source_tracks SET title='Must not persist' WHERE uuid=@Track",
                        new { Track },
                        CancellationToken.None
                    )
                );
            (await write.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should()
                .Be(PostgresErrorCodes.ReadOnlySqlTransaction);
        }
        await AssertNextSessionCanRead();
    }

    [Test]
    public async Task Canceled_query_can_be_disposed_and_the_pool_lease_reused()
    {
        await using (var snapshot = await CatalogReadSession.Open(db, CancellationToken.None))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var read = async () =>
                await snapshot.Connection.ExecuteAsync(
                    snapshot.Command("SELECT pg_sleep(30)", null, cancellation.Token)
                );
            await read.Should().ThrowAsync<OperationCanceledException>();
        }
        await AssertNextSessionCanRead();
    }

    private static Task<string> Title(CatalogReadSession session) =>
        session.Connection.QuerySingleAsync<string>(
            session.Command(
                "SELECT title FROM source_tracks WHERE uuid=@Track",
                new { Track },
                CancellationToken.None
            )
        );

    private async Task AssertNextSessionCanRead()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var next = await CatalogReadSession.Open(db, deadline.Token);
        var value = await next.Connection.QuerySingleAsync<int>(
            next.Command("SELECT 1", null, deadline.Token)
        );
        value.Should().Be(1);
    }
}
