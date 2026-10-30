using System.Diagnostics;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

/// <summary>Canonical ID lifetime through migration, ordinary upserts, retirement, and human restore.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizedPublicIds
{
    private MigrationTestDatabase database = null!;
    private string historyBefore = null!;
    private string historyAfterMigration = null!;
    private NpgsqlConnection Db => database.Connection;

    [OneTimeSetUp]
    public async Task CreateDatabase()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        database = await MigrationTestDatabase.CreateAsync(16);
        await using (var tx = await Db.BeginTransactionAsync())
        {
            var store = await NormStore.BeginAsync(Db, tx, AuthorTypes.Importer, "public-id-test");
            await store.CreateVenuesAsync([
                Venue("venue_00000001", "Original venue"),
                Venue("venue_00000002", "Past venue"),
            ]);
            await store.WriteSongsAsync([
                new(SongId.Parse("song_00000001"), Guid.NewGuid(), "Original song"),
            ]);
            await Db.ExecuteAsync(
                "DELETE FROM norm.venues WHERE id='venue_00000002'",
                transaction: tx
            );
            await tx.CommitAsync();
        }
        historyBefore = await History();
        database.Migrator.MigrateTo(17);
        historyAfterMigration = await History();
    }

    [OneTimeTearDown]
    public async Task DropDatabase()
    {
        if (database is not null)
        {
            await database.DisposeAsync();
        }
    }

    [Test]
    public async Task MigrationReservesActiveAndHistoricallyDeletedIdsWithoutChangingHistory()
    {
        var rows = (
            await Db.QueryAsync<Reservation>("SELECT * FROM norm.public_ids ORDER BY id")
        ).ToArray();
        rows.Select(row => row.id)
            .Should()
            .Equal("song_00000001", "venue_00000001", "venue_00000002");
        rows.Take(2).Should().OnlyContain(row => row.retired_at == null);
        rows[2].retired_at.Should().NotBeNull();
        rows[2].last_name.Should().Be("Past venue");
        historyAfterMigration.Should().Be(historyBefore);
    }

    [Test]
    public async Task UpsertsRetainIdentityAndRolledBackRetirementRestoresBothRowsAndReservations()
    {
        await using (var tx = await Db.BeginTransactionAsync())
        {
            var store = await NormStore.BeginAsync(Db, tx, AuthorTypes.Importer, "public-id-test");
            await store.WriteVenuesAsync([Venue("venue_00000001", "Corrected venue")]);
            var artist = await Db.QuerySingleAsync<Guid>(
                "SELECT artist_uuid FROM norm.songs WHERE id='song_00000001'",
                transaction: tx
            );
            await store.WriteSongsAsync([
                new(SongId.Parse("song_00000001"), artist, "Corrected song"),
            ]);
            await Db.ExecuteAsync(
                "DELETE FROM norm.venues WHERE id='venue_00000001'; DELETE FROM norm.songs WHERE id='song_00000001'",
                transaction: tx
            );
            var retired = (
                await Db.QueryAsync<Reservation>(
                    "SELECT * FROM norm.public_ids WHERE id<>'venue_00000002' ORDER BY id",
                    transaction: tx
                )
            ).ToArray();
            retired.Should().OnlyContain(row => row.retired_at != null);
            retired
                .Select(row => row.last_name)
                .Should()
                .Equal("Corrected song", "Corrected venue");
            await tx.RollbackAsync();
        }

        (
            await Db.QuerySingleAsync<string>(
                "SELECT name FROM norm.venues WHERE id='venue_00000001'"
            )
        )
            .Should()
            .Be("Original venue");
        (
            await Db.QuerySingleAsync<int>(
                "SELECT count(*)::int FROM norm.public_ids WHERE retired_at IS NULL"
            )
        )
            .Should()
            .Be(2);

        await using var edit = await Db.BeginTransactionAsync();
        await NormStore.BeginAsync(Db, edit, AuthorTypes.Importer, "public-id-test");
        var changeId = () =>
            Db.ExecuteAsync(
                "UPDATE norm.venues SET id='venue_00000009' WHERE id='venue_00000001'",
                transaction: edit
            );
        await changeId.Should().ThrowAsync<PostgresException>();
        await edit.RollbackAsync();
        (
            await Db.QuerySingleAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM norm.venues WHERE id='venue_00000001') AND NOT EXISTS(SELECT 1 FROM norm.public_ids WHERE id='venue_00000009')"
            )
        )
            .Should()
            .BeTrue();
    }

    [Test]
    public async Task OnlyAHumanRestoreCanReactivateARetiredIdentity()
    {
        var remint = () => CreatePastVenue(AuthorTypes.Importer, false);
        (await remint.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should()
            .Be(PostgresErrorCodes.UniqueViolation);
        var humanRemint = () => CreatePastVenue(AuthorTypes.Moderator, false);
        (await humanRemint.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should()
            .Be(PostgresErrorCodes.UniqueViolation);
        var machineRestore = () => CreatePastVenue(AuthorTypes.Importer, true);
        (await machineRestore.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should()
            .Be(PostgresErrorCodes.InsufficientPrivilege);

        await using var tx = await Db.BeginTransactionAsync();
        var store = await NormStore.BeginAsync(Db, tx, AuthorTypes.Moderator, "public-id-test");
        await Db.ExecuteAsync("SET LOCAL norm.restoring='on'", transaction: tx);
        await store.CreateVenuesAsync([Venue("venue_00000002", "Past venue")]);
        (
            await Db.QuerySingleAsync<bool>(
                "SELECT retired_at IS NULL FROM norm.public_ids WHERE id='venue_00000002'",
                transaction: tx
            )
        )
            .Should()
            .BeTrue();
        // Keep the shared fixture retired for the migration and refusal checks.
        await tx.RollbackAsync();
    }

    [Test]
    public async Task ARemintWaitingForDeletionFailsAfterThatDeletionCommits()
    {
        await using var deleting = await Db.BeginTransactionAsync();
        var deletion = await NormStore.BeginAsync(
            Db,
            deleting,
            AuthorTypes.Importer,
            "public-id-test"
        );
        await Db.ExecuteAsync(
            "DELETE FROM norm.venues WHERE id='venue_00000001'",
            transaction: deleting
        );
        await using var other = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(LocalTestDatabase.ConnectionString)
            {
                Database = Db.Database,
            }.ConnectionString
        );
        await other.OpenAsync();
        await using var remint = await other.BeginTransactionAsync();
        var store = await NormStore.BeginAsync(
            other,
            remint,
            AuthorTypes.Importer,
            "public-id-test"
        );
        var writing = store.CreateVenuesAsync([Venue("venue_00000001", "Replacement venue")]);
        try
        {
            var wait = Stopwatch.StartNew();
            while (
                !await Db.QuerySingleAsync<bool>(
                    "SELECT @deleting=ANY(pg_blocking_pids(@waiting))",
                    new { deleting = Db.ProcessID, waiting = other.ProcessID },
                    deleting
                )
            )
            {
                wait.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
                await Task.Delay(10);
            }
            await deleting.CommitAsync();
            var finish = async () => await writing;
            (await finish.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should()
                .Be(PostgresErrorCodes.UniqueViolation);
            (
                await Db.QuerySingleAsync<bool>(
                    "SELECT retired_at IS NOT NULL FROM norm.public_ids WHERE id='venue_00000001'"
                )
            )
                .Should()
                .BeTrue();
        }
        finally
        {
            // Release the blocker before disposing the writer if an assertion fails.
            await deleting.DisposeAsync();
            try
            {
                await writing;
            }
            catch (PostgresException) { }
            await remint.DisposeAsync();
            await using var restore = await Db.BeginTransactionAsync();
            var human = await NormStore.BeginAsync(
                Db,
                restore,
                AuthorTypes.Moderator,
                "public-id-test"
            );
            await human.RevertRunAsync(deletion.Run);
            await restore.CommitAsync();
        }
    }

    private async Task CreatePastVenue(string author, bool restoring)
    {
        await using var tx = await Db.BeginTransactionAsync();
        var store = await NormStore.BeginAsync(Db, tx, author, "public-id-test");
        if (restoring)
        {
            await Db.ExecuteAsync("SET LOCAL norm.restoring='on'", transaction: tx);
        }
        await store.CreateVenuesAsync([Venue("venue_00000002", "Replacement venue")]);
    }

    private Task<string> History() =>
        Db.QuerySingleAsync<string>(
            "SELECT md5(string_agg(row_to_json(c)::text, '' ORDER BY id)) FROM norm.changes c"
        );

    private static VenueRecord Venue(string id, string name) => new(VenueId.Parse(id), name);

    private sealed class Reservation
    {
        public string id { get; set; } = null!;
        public string last_name { get; set; } = null!;
        public DateTime? retired_at { get; set; }
    }
}
