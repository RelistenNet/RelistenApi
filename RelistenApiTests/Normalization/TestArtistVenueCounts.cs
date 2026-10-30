using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Normalization;
using Relisten.Normalization.Venues;
using SimpleMigrations;
using SimpleMigrations.DatabaseProvider;

namespace RelistenApiTests.Normalization;

/// <summary>
/// Exercises eventual counts against real source-table writes. Each test owns its artist
/// scope; refreshes stay inside the disposable process catalog.
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
                DELETE FROM norm.venues WHERE id=ANY(@venues);
                DELETE FROM artists WHERE uuid=ANY(@artists);
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

        var second = await Refresh();
        second.RelationshipsChanged.Should().Be(0);
        (await Rows()).Should().BeEquivalentTo(before);
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
        // Source writes do not recount: stale counts are allowed until the refresh.
        (await Rows())
            .Single(row => row.Id == original.Id)
            .Count.Should()
            .Be(2);
        await Refresh();
        var changed = (await Rows()).Single(row => row.Id == original.Id);
        changed.Count.Should().Be(1);
        changed.UpdatedAt.Should().BeAfter(original.UpdatedAt);

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

    [TestCase(true)]
    [TestCase(false)]
    public async Task RelationshipsRequireExistingParents(bool missingArtist)
    {
        var insert = () =>
            db.ExecuteAsync(
                "INSERT INTO norm.artist_venues(artist_uuid,venue_id) VALUES(@artist,@venue)",
                new
                {
                    artist = missingArtist ? Guid.NewGuid() : data.Artists[0],
                    venue = missingArtist ? data.Home : VenueId.New(),
                },
                tx
            );

        await insert
            .Should()
            .ThrowAsync<PostgresException>()
            .Where(error => error.SqlState == PostgresErrorCodes.ForeignKeyViolation);
    }

    [Test]
    public async Task FullReconciliationRepairsDirectSqlChangesAndZerosVanishedGroups()
    {
        await Refresh();
        var before = await Rows();
        await db.ExecuteAsync(
            """
            UPDATE norm.show_venues SET venue_id=@other WHERE show_uuid=@moved;
            DELETE FROM norm.show_venues WHERE show_uuid=@removed;
            """,
            new
            {
                other = data.Other,
                moved = data.Shows[0],
                removed = data.Shows[2],
            },
            tx
        );
        (await Rows()).Should().BeEquivalentTo(before);

        await db.QuerySingleAsync<RefreshResult>(RefreshSql, new { artists = (Guid[]?)null }, tx);

        var after = await Rows();
        after
            .Where(row => row.Artist == data.Artists[0])
            .Select(row => row.Count)
            .Should()
            .Equal(1, 1);
        var removedGroup = after.Single(row => row.Artist == data.Artists[1]);
        removedGroup.Id.Should().Be(before.Single(row => row.Artist == data.Artists[1]).Id);
        removedGroup.Count.Should().Be(0);
    }

    [Test]
    public async Task AnEmptyArtistFilterDoesNotRefreshAnyGroups()
    {
        await Refresh();
        var before = await Rows();
        await db.ExecuteAsync(
            "UPDATE norm.show_venues SET venue_id=@other WHERE show_uuid=@show",
            new { other = data.Other, show = data.Shows[0] },
            tx
        );

        var result = await db.QuerySingleAsync<RefreshResult>(
            RefreshSql,
            new { artists = Array.Empty<Guid>() },
            tx
        );

        result.Should().Be(new RefreshResult(0, 0));
        (await Rows()).Should().BeEquivalentTo(before);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task VenueMergesGroupCountsAndUnmergeKeepsOriginalIds(bool targetExists)
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
        merged.Single(row => row.Id == old.Id).Count.Should().Be(0);
        var secondTarget = merged.Single(row =>
            row.Artist == data.Artists[1] && row.Venue == data.Other.Value
        );
        secondTarget.Count.Should().Be(1);
        merged
            .Single(row => row.Artist == data.Artists[1] && row.Venue == data.Home.Value)
            .Count.Should()
            .Be(0);
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
        unmerged.Count.Should().Be(targetExists ? 1 : 2);
        unmerged.CreatedAt.Should().Be(old.CreatedAt);
    }

    [Test]
    public async Task UnmergeAllowsFormerSurvivorDeletionBeforeCountsRefresh()
    {
        await Refresh();
        var original = await Rows();
        await db.ExecuteAsync(
            "UPDATE norm.venues SET merged_into=@other WHERE id=@home",
            new { home = data.Home, other = data.Other },
            tx
        );
        await Refresh();
        (await Rows()).Should().Contain(row => row.Venue == data.Other.Value);

        // Source edits can precede the next recount. Stale derived relationships must
        // not prevent retirement of a venue that normalization no longer references.
        await db.ExecuteAsync(
            "UPDATE norm.venues SET merged_into=NULL WHERE id=@home; DELETE FROM norm.venues WHERE id=@other;",
            new { home = data.Home, other = data.Other },
            tx
        );
        (await Rows()).Should().NotContain(row => row.Venue == data.Other.Value);

        await Refresh();

        (await Rows())
            .Select(row => (row.Id, row.Count, row.CreatedAt))
            .Should()
            .BeEquivalentTo(original.Select(row => (row.Id, row.Count, row.CreatedAt)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DeletingAVenueCascadesItsRelationships(bool merged)
    {
        await Refresh();
        if (merged)
        {
            await db.ExecuteAsync(
                "UPDATE norm.venues SET merged_into=@other WHERE id=@home",
                new { home = data.Home, other = data.Other },
                tx
            );
            await Refresh();
        }
        var before = await Rows();
        await db.ExecuteAsync(
            "DELETE FROM norm.show_venues WHERE show_uuid=ANY(@shows); DELETE FROM norm.venues WHERE id=@home;",
            new { shows = data.Shows, home = data.Home },
            tx
        );

        var remaining = await Rows();
        remaining.Should().NotContain(row => row.Venue == data.Home.Value);
        remaining
            .Select(row => row.Id)
            .Should()
            .BeEquivalentTo(
                before.Where(row => row.Venue == data.Other.Value).Select(row => row.Id)
            );
        await Refresh();
        (await Rows()).Should().OnlyContain(row => row.Count == 0);
    }

    [Test]
    public async Task DeletingAnArtistCascadesItsRelationshipsAcrossMergedVenues()
    {
        await Refresh();
        await db.ExecuteAsync(
            "UPDATE norm.venues SET merged_into=@other WHERE id=@home",
            new { home = data.Home, other = data.Other },
            tx
        );
        await Refresh();
        var before = await Rows();
        before.Where(row => row.Artist == data.Artists[0]).Should().HaveCount(2);
        await db.ExecuteAsync(
            "DELETE FROM artists WHERE uuid=@artist",
            new { artist = data.Artists[0] },
            tx
        );

        var remaining = await Rows();
        remaining.Should().BeEquivalentTo(before.Where(row => row.Artist == data.Artists[1]));
    }

    [Test]
    public async Task FailedAtomicEditRollsBackItsSourceAndCountsBeforeRetry()
    {
        await Refresh();
        var before = await Rows();
        await tx.SaveAsync("failed_edit");
        await db.ExecuteAsync(
            "UPDATE norm.show_venues SET venue_id=@other WHERE show_uuid=@show",
            new { other = data.Other, show = data.Shows[0] },
            tx
        );
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
        await tx.RollbackAsync("failed_edit");
        (await Rows()).Should().BeEquivalentTo(before);
        (
            await db.QuerySingleAsync<string>(
                "SELECT venue_id FROM norm.show_venues WHERE show_uuid=@show",
                new { show = data.Shows[0] },
                tx
            )
        )
            .Should()
            .Be(data.Home.Value);

        await db.ExecuteAsync(
            "UPDATE norm.show_venues SET venue_id=@other WHERE show_uuid=@show",
            new { other = data.Other, show = data.Shows[0] },
            tx
        );
        await Refresh();
        var rows = (await Rows()).Where(row => row.Artist == data.Artists[0]).ToArray();
        rows.Select(row => row.Count).Should().Equal(1, 1);
    }

    [Test]
    public async Task MaintenanceRoleCanRefreshWithoutDirectDerivedTableWriteAccess()
    {
        var role = "artist_venue_maintenance_" + Guid.NewGuid().ToString("N");
        await db.ExecuteAsync(
            $"""
            CREATE ROLE {role} NOLOGIN;
            GRANT USAGE ON SCHEMA norm TO {role};
            GRANT EXECUTE ON FUNCTION norm.refresh_artist_venues(uuid[]) TO {role};
            SET LOCAL ROLE {role};
            """,
            transaction: tx
        );

        var refreshed = await Refresh();

        refreshed.Should().Be(new RefreshResult(2, 2));
        var write = () =>
            db.ExecuteAsync(
                "UPDATE norm.artist_venues SET artist_show_count=0 WHERE artist_uuid=@artist",
                new { artist = data.Artists[0] },
                tx
            );
        await write
            .Should()
            .ThrowAsync<PostgresException>()
            .Where(error => error.SqlState == PostgresErrorCodes.InsufficientPrivilege);
    }

    [Test]
    public async Task ReadersSeeCommittedCountsWithoutWaitingForAnUncommittedRefresh()
    {
        await Refresh();
        await tx.CommitAsync();
        committedFixture = true;
        await tx.DisposeAsync();
        tx = await db.BeginTransactionAsync();
        await NormStore.BeginAsync(
            db,
            tx,
            AuthorTypes.Moderator,
            "artist-venue-atomic-visibility-test"
        );
        await db.ExecuteAsync(
            "UPDATE norm.show_venues SET venue_id=@other WHERE show_uuid=@show",
            new { other = data.Other, show = data.Shows[0] },
            tx
        );
        await Refresh();

        await using var reader = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await reader.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = new CommandDefinition(
            "SELECT artist_show_count FROM norm.artist_venues WHERE artist_uuid=@artist AND venue_id=@venue",
            new { artist = data.Artists[0], venue = data.Home },
            cancellationToken: timeout.Token
        );

        (await reader.QuerySingleAsync<int>(read)).Should().Be(2);
        await tx.CommitAsync();
        (await reader.QuerySingleAsync<int>(read)).Should().Be(1);
    }

    [Test]
    public async Task ServiceRefreshesSelectedArtistsAndReconcilesAllOwners()
    {
        await tx.CommitAsync();
        committedFixture = true;
        await tx.DisposeAsync();
        tx = await db.BeginTransactionAsync();
        var service = new ArtistVenueCounts(new TestDatabase());

        var targeted = await service.RefreshArtistsAsync([data.Artists[0]]);

        targeted.Should().Be(new ArtistVenueRefreshResult(1, 1));
        var selected = (await Rows()).Should().ContainSingle().Subject;
        selected.Artist.Should().Be(data.Artists[0]);
        selected.Count.Should().Be(2);

        var reconciled = await service.RefreshAllAsync();

        reconciled.ArtistsRefreshed.Should().BeGreaterThanOrEqualTo(2);
        reconciled.RelationshipsChanged.Should().BeGreaterThanOrEqualTo(1);
        var all = await Rows();
        all.Select(row => row.Count).Should().BeEquivalentTo(new[] { 2, 1 });
        all.Single(row => row.Artist == selected.Artist).Id.Should().Be(selected.Id);
    }

    [Test]
    public async Task WaitingRefreshReadsCommittedChangesFromRawSqlOutsideTheVenueMutex()
    {
        await Refresh();
        await tx.CommitAsync();
        committedFixture = true;
        await tx.DisposeAsync();
        tx = await db.BeginTransactionAsync();
        await Refresh(); // Hold the shared venue mutex through this transaction.

        await using var waiting = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await waiting.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var refresh = waiting.QuerySingleAsync<RefreshResult>(
            new CommandDefinition(
                RefreshSql,
                new { artists = data.Artists },
                cancellationToken: timeout.Token
            )
        );
        try
        {
            await WaitUntilBlocked(waiting.ProcessID, db.ProcessID);
            await using var writer = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
            await writer.OpenAsync();
            await using var writerTx = await writer.BeginTransactionAsync();
            await NormStore.BeginAsync(
                writer,
                writerTx,
                AuthorTypes.Moderator,
                "concurrent-artist-venue-test"
            );
            await writer.ExecuteAsync(
                new CommandDefinition(
                    "UPDATE norm.show_venues SET venue_id=@other WHERE show_uuid=@show",
                    new { other = data.Other, show = data.Shows[0] },
                    writerTx,
                    cancellationToken: timeout.Token
                )
            );
            await writerTx.CommitAsync(timeout.Token);
        }
        finally
        {
            // This transaction only holds the venue mutex. Always release it so the
            // waiting refresh can finish, including when a test assertion fails.
            await tx.CommitAsync();
        }
        await refresh;
        await tx.DisposeAsync();
        tx = await db.BeginTransactionAsync();
        (await Rows())
            .Where(row => row.Artist == data.Artists[0])
            .Select(row => row.Count)
            .Should()
            .Equal(1, 1);
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
        db.QuerySingleAsync<RefreshResult>(RefreshSql, new { artists = data.Artists }, tx);

    private async Task<Relationship[]> Rows() =>
        (await db.QueryAsync<Relationship>(RowSql, new { artists = data.Artists }, tx)).ToArray();

    private const string RefreshSql = """
        SELECT artists_refreshed ArtistsRefreshed,relationships_changed RelationshipsChanged
        FROM norm.refresh_artist_venues(@artists::uuid[])
        """;

    private const string RowSql = """
        SELECT id Id,artist_uuid Artist,venue_id Venue,artist_show_count Count,
               created_at CreatedAt,updated_at UpdatedAt
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
        Assert.Fail("The concurrent refresh did not wait for the active refresh transaction.");
    }

    private sealed record Fixture(
        Guid[] Artists,
        long[] ArtistIds,
        Guid[] Shows,
        VenueId Home,
        VenueId Other
    );

    private sealed record RefreshResult(int ArtistsRefreshed, int RelationshipsChanged);

    private sealed class TestDatabase : INormalizationDatabase
    {
        public NpgsqlConnection CreateWriteConnection() => new(LocalTestDatabase.ConnectionString);
    }

    private sealed record Relationship(
        string Id,
        Guid Artist,
        string Venue,
        int Count,
        DateTime CreatedAt,
        DateTime UpdatedAt
    );
}
