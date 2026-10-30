using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Normalization;
using SimpleMigrations;
using SimpleMigrations.DatabaseProvider;

namespace RelistenApiTests.Normalization;

/// <summary>
/// Exercises eventual counts against real source-table writes. Each test owns its artist
/// scope; queue requests and refreshes stay inside the disposable process catalog.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestArtistVenueCounts
{
    private NpgsqlConnection db = null!;
    private NpgsqlTransaction tx = null!;
    private Fixture data = null!;
    private bool committedFixture;

    [SetUp]
    public async Task Open()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        db = await MigratedTestDatabase.OpenAsync();
        tx = await db.BeginTransactionAsync();
        data = await Seed(db, tx);
        committedFixture = false;
    }

    [TearDown]
    public async Task Close()
    {
        if (db is null)
        {
            return;
        }

        await tx.DisposeAsync();
        if (committedFixture)
        {
            await using var cleanup = await db.BeginTransactionAsync();
            await NormStore.BeginAsync(
                db,
                cleanup,
                AuthorTypes.Moderator,
                "artist-venue-test-cleanup"
            );
            await db.ExecuteAsync(
                """
                DELETE FROM norm.show_venues WHERE show_uuid=ANY(@shows);
                DELETE FROM shows WHERE uuid=ANY(@shows);
                DELETE FROM norm.artist_venues WHERE artist_uuid=ANY(@artists);
                DELETE FROM norm.venues WHERE id=ANY(@venues);
                DELETE FROM artists WHERE uuid=ANY(@artists);
                DELETE FROM norm.artist_venue_refresh_queue WHERE artist_uuid=ANY(@artists);
                """,
                new
                {
                    shows = data.Shows,
                    artists = data.Artists,
                    venues = new[] { data.Home.Value, data.Other.Value },
                },
                cleanup
            );
            await cleanup.CommitAsync();
        }
        await db.DisposeAsync();
    }

    [Test]
    public async Task RefreshCreatesStableTypedRelationshipsAndDoesNotRewriteUnchangedRows()
    {
        var first = await Refresh();
        first.ArtistsRefreshed.Should().Be(2);
        first.RelationshipsChanged.Should().Be(2);
        var before = await Rows();
        before.Select(row => row.Count).Should().BeEquivalentTo(new[] { 2, 1 });
        before.Should().OnlyContain(row => ArtistVenueId.Parse(row.Id).Value == row.Id);
        before.Should().OnlyContain(row => row.CreatedAt <= row.UpdatedAt);

        await Request();
        var second = await Refresh();
        second.RelationshipsChanged.Should().Be(0);
        (await Rows()).Should().BeEquivalentTo(before);
        (await Refresh()).ArtistsRefreshed.Should().Be(0);
    }

    [Test]
    public async Task AssignmentChangesAndShowDeletionConvergeWithoutLosingZeroCountIdentity()
    {
        await Refresh();
        var original = (await Rows()).Single(row => row.Artist == data.Artists[0]);
        await db.ExecuteAsync(
            "UPDATE norm.show_venues SET venue_id=@other WHERE show_uuid=@show",
            new { other = data.Other, show = data.Shows[0] },
            tx
        );
        // Source writes only queue work: stale counts are allowed until the refresh.
        (await Rows())
            .Single(row => row.Id == original.Id)
            .Count.Should()
            .Be(2);
        await Refresh();
        (await Rows()).Single(row => row.Id == original.Id).Count.Should().Be(1);

        await db.ExecuteAsync(
            "DELETE FROM shows WHERE uuid=@show",
            new { show = data.Shows[1] },
            tx
        );
        await Refresh();
        (await Rows()).Single(row => row.Id == original.Id).Count.Should().Be(0);
        (
            await db.QuerySingleAsync<int>(
                "SELECT count(*)::int FROM norm.show_venues WHERE show_uuid=@show",
                new { show = data.Shows[1] },
                tx
            )
        )
            .Should()
            .Be(1); // An orphaned normalization link is not a catalog show.

        await InsertShow(db, tx, data.ArtistIds[0], data.Shows[1], 2);
        await Refresh();
        var restored = (await Rows()).Single(row => row.Id == original.Id);
        restored.Count.Should().Be(1);
        restored.CreatedAt.Should().Be(original.CreatedAt);
    }

    [Test]
    public async Task ReassigningAShowToAnotherArtistRefreshesBothGroups()
    {
        await Refresh();
        await db.ExecuteAsync(
            "UPDATE shows SET artist_id=@artist WHERE uuid=@show",
            new { artist = data.ArtistIds[1], show = data.Shows[0] },
            tx
        );
        await Refresh();
        var rows = await Rows();
        rows.Single(row => row.Artist == data.Artists[0]).Count.Should().Be(1);
        rows.Single(row => row.Artist == data.Artists[1]).Count.Should().Be(2);
    }

    [Test]
    public async Task CountsIgnoreApprovalFlagsAndDeletedAssignments()
    {
        await db.ExecuteAsync(
            "INSERT INTO features(artist_id,norm_catalogue) VALUES (@first,true),(@second,false)",
            new { first = data.ArtistIds[0], second = data.ArtistIds[1] },
            tx
        );
        await Refresh();
        (await Rows()).Select(row => row.Count).Should().BeEquivalentTo(new[] { 2, 1 });
        await db.ExecuteAsync(
            "DELETE FROM norm.show_venues WHERE show_uuid=@show",
            new { show = data.Shows[2] },
            tx
        );
        await Refresh();
        (await Rows()).Single(row => row.Artist == data.Artists[1]).Count.Should().Be(0);
    }

    [Test]
    public async Task ArtistUuidChangesRefreshBothOldAndNewIdentities()
    {
        await Refresh();
        var original = (await Rows()).Single(row => row.Artist == data.Artists[0]);
        var replacement = Guid.NewGuid();
        await db.ExecuteAsync(
            "UPDATE artists SET uuid=@replacement WHERE uuid=@original",
            new { replacement, original = data.Artists[0] },
            tx
        );
        var artists = new[] { data.Artists[0], replacement };
        await db.QuerySingleAsync<RefreshResult>(
            "SELECT artists_refreshed ArtistsRefreshed,relationships_changed RelationshipsChanged FROM norm.refresh_artist_venues(@artists,100)",
            new { artists },
            tx
        );
        var rows = (await db.QueryAsync<Relationship>(RowSql, new { artists }, tx)).ToArray();
        rows.Single(row => row.Id == original.Id).Count.Should().Be(0);
        var current = rows.Single(row => row.Artist == replacement);
        current.Count.Should().Be(2);
        current.Id.Should().NotBe(original.Id);
    }

    [Test]
    public async Task ReconciliationFindsSourceChangesWhosePendingRequestWasLost()
    {
        await Refresh();
        await db.ExecuteAsync(
            "UPDATE norm.show_venues SET venue_id=@other WHERE show_uuid=@show; DELETE FROM norm.artist_venue_refresh_queue WHERE artist_uuid=@artist;",
            new
            {
                other = data.Other,
                show = data.Shows[0],
                artist = data.Artists[0],
            },
            tx
        );
        (await Pending(data.Artists[0])).Should().BeFalse();
        await db.ExecuteAsync("SELECT norm.request_artist_venue_refresh(NULL)", transaction: tx);
        await Refresh();
        (await Rows())
            .Where(row => row.Artist == data.Artists[0])
            .Select(row => row.Count)
            .Should()
            .Equal(1, 1);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MergeRedirectsHistoricalPairsAndUnmergeRestoresTheirOriginalIds(
        bool targetExists
    )
    {
        if (targetExists)
        {
            await db.ExecuteAsync(
                "UPDATE norm.show_venues SET venue_id=@other WHERE show_uuid=@show",
                new { other = data.Other, show = data.Shows[0] },
                tx
            );
        }
        await Refresh();
        var before = await Rows();
        var old = before.Single(row =>
            row.Artist == data.Artists[0] && row.Venue == data.Home.Value
        );
        await db.ExecuteAsync(
            "UPDATE norm.venues SET merged_into=@other WHERE id=@home",
            new { other = data.Other, home = data.Home },
            tx
        );
        await Refresh();
        var merged = await Rows();
        var target = merged.Single(row =>
            row.Artist == data.Artists[0] && row.Venue == data.Other.Value
        );
        target.Count.Should().Be(2);
        merged
            .Single(row => row.Id == old.Id)
            .Should()
            .Match<Relationship>(row => row.Count == 0 && row.MergedInto == target.Id);
        var secondTarget = merged.Single(row =>
            row.Artist == data.Artists[1] && row.Venue == data.Other.Value
        );
        secondTarget.Count.Should().Be(1);
        merged
            .Single(row => row.Artist == data.Artists[1] && row.Venue == data.Home.Value)
            .MergedInto.Should()
            .Be(secondTarget.Id);
        if (targetExists)
        {
            target
                .Id.Should()
                .Be(
                    before
                        .Single(row =>
                            row.Artist == data.Artists[0] && row.Venue == data.Other.Value
                        )
                        .Id
                );
        }

        await db.ExecuteAsync(
            "UPDATE norm.venues SET merged_into=NULL WHERE id=@home",
            new { home = data.Home },
            tx
        );
        await Refresh();
        var unmerged = (await Rows()).Single(row => row.Id == old.Id);
        unmerged.MergedInto.Should().BeNull();
        unmerged.Count.Should().Be(targetExists ? 1 : 2);
        unmerged.CreatedAt.Should().Be(old.CreatedAt);
    }

    [Test]
    public async Task RetiringAnUnusedVenueDoesNotDeleteItsRelationshipIdentity()
    {
        await Refresh();
        var before = await Rows();
        await db.ExecuteAsync(
            "DELETE FROM norm.show_venues WHERE show_uuid=ANY(@shows); DELETE FROM norm.venues WHERE id=@home;",
            new { shows = data.Shows, home = data.Home },
            tx
        );
        await Refresh();
        var after = await Rows();
        after.Select(row => row.Id).Should().BeEquivalentTo(before.Select(row => row.Id));
        after.Should().OnlyContain(row => row.Count == 0 && row.MergedInto == null);
    }

    [Test]
    public async Task RestoringARetiredVenueAsAMergeRequeuesItsHistoricalRelationships()
    {
        await Refresh();
        var original = await Rows();
        await db.ExecuteAsync(
            "DELETE FROM norm.show_venues WHERE show_uuid=ANY(@shows); DELETE FROM norm.venues WHERE id=@home;",
            new { shows = data.Shows, home = data.Home },
            tx
        );
        await Refresh();
        (await Pending(data.Artists[0])).Should().BeFalse();
        (await Pending(data.Artists[1])).Should().BeFalse();

        // A moderator can restore a retired identity directly as a merge. No show
        // assignment is rewritten, so the venue INSERT itself must queue the refresh.
        await db.ExecuteAsync(
            """
            INSERT INTO norm.venues(id,name,slug,merged_into,author_type,author_ref,run_uuid)
            SELECT @home,'Restored Count Hall',@slug,id,author_type,author_ref,run_uuid
            FROM norm.venues WHERE id=@other
            """,
            new
            {
                home = data.Home,
                other = data.Other,
                slug = "restored-" + data.Home.Value,
            },
            tx
        );
        (await Pending(data.Artists[0])).Should().BeTrue();
        (await Pending(data.Artists[1])).Should().BeTrue();
        await Refresh();

        var restored = await Rows();
        foreach (var historical in original)
        {
            var target = restored.Single(row =>
                row.Artist == historical.Artist && row.Venue == data.Other.Value
            );
            target.Count.Should().Be(0);
            var redirect = restored.Single(row => row.Id == historical.Id);
            redirect.Count.Should().Be(0);
            redirect.MergedInto.Should().Be(target.Id);
            redirect.CreatedAt.Should().Be(historical.CreatedAt);
        }
    }

    [Test]
    public async Task FailedRefreshPreservesPendingWorkAndCanBeRetried()
    {
        await Refresh();
        await db.ExecuteAsync(
            "UPDATE norm.show_venues SET venue_id=@other WHERE show_uuid=@show",
            new { other = data.Other, show = data.Shows[0] },
            tx
        );
        await tx.SaveAsync("failed_refresh");
        // The typed test ID is safe to place in DDL, which cannot bind a CHECK parameter.
        await db.ExecuteAsync(
            $"ALTER TABLE norm.artist_venues ADD CONSTRAINT test_refresh_failure CHECK (venue_id <> '{data.Other.Value}') NOT VALID",
            transaction: tx
        );
        var attempt = async () => await Refresh();
        await attempt
            .Should()
            .ThrowAsync<PostgresException>()
            .Where(error => error.SqlState == PostgresErrorCodes.CheckViolation);
        await tx.RollbackAsync("failed_refresh");
        (await Rows()).Single(row => row.Artist == data.Artists[0]).Count.Should().Be(2);
        (await Pending(data.Artists[0])).Should().BeTrue();
        await Refresh();
        var rows = (await Rows()).Where(row => row.Artist == data.Artists[0]).ToArray();
        rows.Select(row => row.Count).Should().Equal(1, 1);
        (await Pending(data.Artists[0])).Should().BeFalse();
    }

    [Test]
    public async Task AdminSqlCanQueueAChangeWithoutPermissionToWriteDerivedTables()
    {
        await Refresh();
        var role = "artist_venue_test_" + Guid.NewGuid().ToString("N");
        await tx.SaveAsync("admin_write");
        await db.ExecuteAsync(
            $"""
            CREATE ROLE {role} NOLOGIN;
            GRANT USAGE ON SCHEMA norm TO {role};
            GRANT SELECT, UPDATE ON norm.show_venues TO {role};
            GRANT SELECT ON norm.runs TO {role};
            GRANT INSERT ON norm.changes TO {role};
            GRANT USAGE ON ALL SEQUENCES IN SCHEMA norm TO {role};
            SET LOCAL ROLE {role};
            """,
            transaction: tx
        );
        try
        {
            await db.ExecuteAsync(
                "UPDATE norm.show_venues SET venue_id=@other WHERE show_uuid=@show",
                new { other = data.Other, show = data.Shows[0] },
                tx
            );
        }
        catch
        {
            await tx.RollbackAsync("admin_write");
            throw;
        }
        await db.ExecuteAsync("RESET ROLE", transaction: tx);
        (await Pending(data.Artists[0])).Should().BeTrue();
        await Refresh();
        (await Rows())
            .Where(row => row.Artist == data.Artists[0])
            .Select(row => row.Count)
            .Should()
            .Equal(1, 1);
    }

    [Test]
    public async Task SourceWriteBlockedByRefreshRequeuesAfterTheRefreshCommits()
    {
        await Refresh();
        await tx.CommitAsync();
        committedFixture = true;
        await tx.DisposeAsync();
        // Make the request committed before taking the refresh lock. DO NOTHING on
        // conflict would miss a source write arriving while this existing row is locked.
        await db.ExecuteAsync(
            "SELECT norm.request_artist_venue_refresh(@artists)",
            new { artists = new[] { data.Artists[0] } }
        );
        tx = await db.BeginTransactionAsync();
        await db.ExecuteAsync(
            "SELECT artist_uuid FROM norm.artist_venue_refresh_queue WHERE artist_uuid=@artist FOR UPDATE",
            new { artist = data.Artists[0] },
            tx
        );

        await using var writer = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await writer.OpenAsync();
        await using var writerTx = await writer.BeginTransactionAsync();
        await NormStore.BeginAsync(
            writer,
            writerTx,
            AuthorTypes.Moderator,
            "concurrent-artist-venue-test"
        );
        var update = writer.ExecuteAsync(
            "UPDATE norm.show_venues SET venue_id=@other WHERE show_uuid=@show",
            new { other = data.Other, show = data.Shows[0] },
            writerTx
        );
        try
        {
            await WaitUntilBlocked(writer.ProcessID, db.ProcessID);
            await Refresh();
        }
        catch
        {
            await tx.RollbackAsync();
            await update;
            throw;
        }
        await tx.CommitAsync();
        await update;
        await writerTx.CommitAsync();

        await tx.DisposeAsync();
        tx = await db.BeginTransactionAsync();
        (await Pending(data.Artists[0])).Should().BeTrue();
        await Refresh();
        (await Rows())
            .Where(row => row.Artist == data.Artists[0])
            .Select(row => row.Count)
            .Should()
            .Equal(1, 1);
        (await Pending(data.Artists[0])).Should().BeFalse();
    }

    [Test]
    public async Task MigrationBackfillsExistingAssignmentsWithoutChangingTheirIdentity()
    {
        var name = "artist_venue_migration_" + Guid.NewGuid().ToString("N");
        var builder = new NpgsqlConnectionStringBuilder(LocalTestDatabase.ConnectionString)
        {
            Database = "postgres",
        };
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE DATABASE {name} TEMPLATE template0");
        try
        {
            builder.Database = name;
            await using var catalog = new NpgsqlConnection(builder.ConnectionString);
            await catalog.OpenAsync();
            var schema = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog-schema.sql")
            );
            await catalog.ExecuteAsync(schema, commandTimeout: 120);
            var migrator = new SimpleMigrator(
                typeof(Relisten.Startup).Assembly,
                new PostgresqlDatabaseProvider(catalog)
            );
            migrator.Load();
            migrator.Baseline(12);
            migrator.MigrateTo(14);
            Fixture seeded;
            await using (var seedTx = await catalog.BeginTransactionAsync())
            {
                seeded = await Seed(catalog, seedTx);
                await seedTx.CommitAsync();
            }
            migrator.MigrateTo(15);
            var rows = (
                await catalog.QueryAsync<Relationship>(RowSql, new { artists = seeded.Artists })
            ).ToArray();
            rows.Select(row => row.Count).Should().BeEquivalentTo(new[] { 2, 1 });
            rows.Should().OnlyContain(row => ArtistVenueId.Parse(row.Id).Value == row.Id);
            (
                await catalog.QuerySingleAsync<int>(
                    "SELECT count(*)::int FROM norm.artist_venue_refresh_queue"
                )
            )
                .Should()
                .Be(0);
            var sourceIds = await catalog.QueryAsync<string>(
                "SELECT venue_id FROM norm.show_venues ORDER BY show_uuid"
            );
            sourceIds.Should().OnlyContain(id => id == seeded.Home.Value);
        }
        finally
        {
            await admin.ExecuteAsync($"DROP DATABASE {name} WITH (FORCE)");
        }
    }

    private Task<RefreshResult> Refresh() =>
        db.QuerySingleAsync<RefreshResult>(
            "SELECT artists_refreshed ArtistsRefreshed,relationships_changed RelationshipsChanged FROM norm.refresh_artist_venues(@artists,100)",
            new { artists = data.Artists },
            tx
        );

    private Task Request() =>
        db.ExecuteAsync(
            "SELECT norm.request_artist_venue_refresh(@artists)",
            new { artists = data.Artists },
            tx
        );

    private async Task<Relationship[]> Rows() =>
        (await db.QueryAsync<Relationship>(RowSql, new { artists = data.Artists }, tx)).ToArray();

    private Task<bool> Pending(Guid artist) =>
        db.QuerySingleAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM norm.artist_venue_refresh_queue WHERE artist_uuid=@artist)",
            new { artist },
            tx
        );

    private const string RowSql = """
        SELECT id Id,artist_uuid Artist,venue_id Venue,artist_show_count Count,
               created_at CreatedAt,updated_at UpdatedAt,merged_into MergedInto
        FROM norm.artist_venues WHERE artist_uuid=ANY(@artists) ORDER BY artist_uuid,venue_id
        """;

    private static async Task<Fixture> Seed(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        var artists = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var ids = new long[2];
        for (var index = 0; index < artists.Length; index++)
        {
            ids[index] = await connection.QuerySingleAsync<long>(
                "INSERT INTO artists(name,slug,uuid,updated_at) VALUES('Count Test',@slug,@uuid,now()) RETURNING id",
                new { slug = "artist-venue-count-" + artists[index], uuid = artists[index] },
                transaction
            );
        }
        var shows = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        for (var index = 0; index < shows.Length; index++)
        {
            await InsertShow(
                connection,
                transaction,
                ids[index == 2 ? 1 : 0],
                shows[index],
                index + 1
            );
        }
        var home = VenueId.New();
        var other = VenueId.New();
        var store = await NormStore.BeginAsync(
            connection,
            transaction,
            AuthorTypes.Moderator,
            "artist-venue-count-tests"
        );
        await store.CreateVenuesAsync([
            new VenueRecord(home, "Count Hall"),
            new VenueRecord(other, "Other Count Hall"),
        ]);
        await store.WriteShowVenuesAsync(
            shows.Select(show => new ShowVenueRecord(show, home)).ToArray()
        );
        await store.FinishAsync();
        return new(artists, ids, shows, home, other);
    }

    private static Task InsertShow(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long artist,
        Guid show,
        int day
    ) =>
        connection.ExecuteAsync(
            "INSERT INTO shows(artist_id,date,display_date,uuid,updated_at) VALUES(@artist,@date::date,@date,@show,now())",
            new
            {
                artist,
                show,
                date = new DateOnly(2000, 1, day).ToString("yyyy-MM-dd"),
            },
            transaction
        );

    private static async Task WaitUntilBlocked(int waitingPid, int blockingPid)
    {
        await using var monitor = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await monitor.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (
                await monitor.QuerySingleAsync<bool>(
                    "SELECT @blockingPid=ANY(pg_blocking_pids(@waitingPid))",
                    new { blockingPid, waitingPid }
                )
            )
            {
                return;
            }
            await Task.Delay(20);
        }
        Assert.Fail("The source writer did not wait for the refresh's pending-work row lock.");
    }

    private sealed record Fixture(
        Guid[] Artists,
        long[] ArtistIds,
        Guid[] Shows,
        VenueId Home,
        VenueId Other
    );

    private sealed record RefreshResult(int ArtistsRefreshed, int RelationshipsChanged);

    private sealed record Relationship(
        string Id,
        Guid Artist,
        string Venue,
        int Count,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        string? MergedInto
    );
}
