using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Normalization;
using Relisten.Normalization.Venues;

namespace RelistenApiTests.Normalization;

/// <summary>Derived counts, identity, and refresh coordination against real disposable source rows.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestArtistVenueCounts
{
    private NpgsqlConnection db = null!;
    private NpgsqlTransaction tx = null!;
    private Fixture data = null!;

    [SetUp]
    public async Task Open()
    {
        db = null!;
        tx = null!;
        data = null!;
        MigratedTestDatabase.RequireLocalDatabase();
        db = await MigratedTestDatabase.OpenAsync();
        // Commit fixture facts so service calls and concurrent sessions see the same rows.
        tx = await db.BeginTransactionAsync();
        data = await Seed(db, tx);
        await Commit();
    }

    [TearDown]
    public async Task Close()
    {
        if (db is null)
        {
            return;
        }
        try
        {
            if (tx is not null)
            {
                await tx.DisposeAsync();
            }
            if (data is null)
            {
                return;
            }
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
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Test]
    public async Task RefreshCountsAllArtistsAndPreservesUnchangedRows()
    {
        var first = await Refresh();
        first.Should().Be(new RefreshResult(2, 2));
        var before = await Rows();
        // One artist is approved and the other is not; storage is independent of rollout.
        before.Select(row => row.Count).Should().BeEquivalentTo(new[] { 2, 1 });
        (await Refresh()).RelationshipsChanged.Should().Be(0);
        (await Rows()).Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task SourceChangesReachZeroAndReturningShowsReuseTheRelationship()
    {
        await Refresh();
        var original = (await Rows()).Single(row => row.Artist == data.Artists[0]);
        await MoveShow(data.Shows[0], data.Other);
        (await Rows()).Single(row => row.Id == original.Id).Count.Should().Be(2);
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
        await InsertShow(db, tx, data.ArtistIds[0], data.Shows[1], 2);
        await Refresh();
        var restored = (await Rows()).Single(row => row.Id == original.Id);
        restored.Count.Should().Be(1);
        restored.CreatedAt.Should().Be(original.CreatedAt);
    }

    [Test]
    public async Task ArtistReassignmentRefreshesBothOwners()
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
    public async Task ScopedServiceAndFullRepairRespectTheirMembership()
    {
        var service = new ArtistVenueCounts(new TestDatabase());
        await service.RefreshArtistsAsync([data.Artists[0]]);
        var first = (await Rows()).Should().ContainSingle().Subject;
        first.Artist.Should().Be(data.Artists[0]);
        await service.RefreshAllAsync();
        var original = await Rows();
        original.Select(row => row.Count).Should().BeEquivalentTo(new[] { 2, 1 });

        await MoveShow(data.Shows[0], data.Other);
        await db.ExecuteAsync(
            "DELETE FROM norm.show_venues WHERE show_uuid=@show",
            new { show = data.Shows[2] },
            tx
        );
        await Commit();
        (await QueryRefresh(db, [], tx)).Should().Be(new RefreshResult(0, 0));
        (await Rows()).Should().BeEquivalentTo(original);
        await service.RefreshArtistsAsync([data.Artists[0]]);
        (await Rows()).Single(row => row.Artist == data.Artists[1]).Count.Should().Be(1);
        await service.RefreshAllAsync();
        var repaired = await Rows();
        repaired
            .Where(row => row.Artist == data.Artists[0])
            .Select(row => row.Count)
            .Should()
            .Equal(1, 1);
        var vanished = repaired.Single(row => row.Artist == data.Artists[1]);
        vanished.Id.Should().Be(original.Single(row => row.Artist == data.Artists[1]).Id);
        vanished.Count.Should().Be(0);
    }

    [Test]
    public async Task MergeAndUnmergePreserveIdsAndAllowRetirementBeforeRecount()
    {
        await MoveShow(data.Shows[0], data.Other);
        await Refresh();
        var before = await Rows();
        var target = before.Single(row =>
            row.Artist == data.Artists[0] && row.Venue == data.Other.Value
        );
        await db.ExecuteAsync(
            "UPDATE norm.venues SET merged_into=@other WHERE id=@home",
            new { home = data.Home, other = data.Other },
            tx
        );
        await Refresh();
        var merged = await Rows();
        merged.Single(row => row.Id == target.Id).Count.Should().Be(2);
        merged
            .Single(row => row.Artist == data.Artists[1] && row.Venue == data.Other.Value)
            .Count.Should()
            .Be(1);
        merged
            .Where(row => row.Venue == data.Home.Value)
            .Should()
            .OnlyContain(row => row.Count == 0);

        // A stale derived group must not prevent deleting a venue after unmerge.
        await db.ExecuteAsync(
            "UPDATE norm.venues SET merged_into=NULL WHERE id=@home",
            new { home = data.Home },
            tx
        );
        await MoveShow(data.Shows[0], data.Home);
        await db.ExecuteAsync(
            "DELETE FROM norm.venues WHERE id=@other",
            new { other = data.Other },
            tx
        );
        (await Rows()).Should().NotContain(row => row.Venue == data.Other.Value);
        await Refresh();
        var restored = await Rows();
        restored.Select(row => row.Count).Should().BeEquivalentTo(new[] { 2, 1 });
        restored
            .Select(row => (row.Id, row.CreatedAt))
            .Should()
            .BeEquivalentTo(
                before
                    .Where(row => row.Venue == data.Home.Value)
                    .Select(row => (row.Id, row.CreatedAt))
            );
    }

    [Test]
    public async Task ParentDeletionCascadesOnlyItsRelationships()
    {
        await MoveShow(data.Shows[0], data.Other);
        await Refresh();
        var retained = (await Rows()).Single(row => row.Venue == data.Other.Value);
        await db.ExecuteAsync(
            "DELETE FROM norm.show_venues WHERE venue_id=@home; DELETE FROM norm.venues WHERE id=@home;",
            new { home = data.Home },
            tx
        );
        (await Rows()).Should().ContainSingle().Which.Should().Be(retained);
        await db.ExecuteAsync(
            "DELETE FROM artists WHERE uuid=@artist",
            new { artist = data.Artists[0] },
            tx
        );
        (await Rows()).Should().BeEmpty();
    }

    [Test]
    public async Task FailedAtomicEditRollsBackSourceAndCountsBeforeRetry()
    {
        await Refresh();
        var before = await Rows();
        await tx.SaveAsync("failed_edit");
        await MoveShow(data.Shows[0], data.Other);
        await db.ExecuteAsync(
            $"ALTER TABLE norm.artist_venues ADD CONSTRAINT test_refresh_failure CHECK (venue_id <> '{data.Other.Value}') NOT VALID",
            transaction: tx
        );
        var attempt = () => Refresh();
        await attempt
            .Should()
            .ThrowAsync<PostgresException>()
            .Where(error => error.SqlState == PostgresErrorCodes.CheckViolation);
        await tx.RollbackAsync("failed_edit");
        (await Rows()).Should().BeEquivalentTo(before);
        var source = await db.QuerySingleAsync<string>(
            "SELECT venue_id FROM norm.show_venues WHERE show_uuid=@show",
            new { show = data.Shows[0] },
            tx
        );
        source.Should().Be(data.Home.Value);
        await MoveShow(data.Shows[0], data.Other);
        await Refresh();
        (await Rows())
            .Where(row => row.Artist == data.Artists[0])
            .Select(row => row.Count)
            .Should()
            .Equal(1, 1);
    }

    [Test]
    public async Task MaintenanceRoleRefreshesWithoutDirectCounterWrites()
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
        (await Refresh()).Should().Be(new RefreshResult(2, 2));
        var write = () =>
            db.ExecuteAsync("UPDATE norm.artist_venues SET artist_show_count=0", transaction: tx);
        await write
            .Should()
            .ThrowAsync<PostgresException>()
            .Where(error => error.SqlState == PostgresErrorCodes.InsufficientPrivilege);
    }

    [Test]
    public async Task DisjointRefreshesAndReadersProceedWhileOverlappingRefreshWaitsForFreshData()
    {
        await Refresh();
        await Commit();
        await MoveShow(data.Shows[0], data.Other);
        await QueryRefresh(db, [data.Artists[0]], tx);
        var service = new ArtistVenueCounts(new TestDatabase());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.RefreshArtistsAsync([data.Artists[1]], timeout.Token);

        await using var waiting = await MigratedTestDatabase.OpenAsync();
        var pending = QueryRefresh(waiting, data.Artists, cancellationToken: timeout.Token);
        try
        {
            await WaitUntilBlocked(waiting.ProcessID, db.ProcessID);
            await using var reader = await MigratedTestDatabase.OpenAsync();
            var committedCount = await reader.QuerySingleAsync<int>(
                new CommandDefinition(
                    "SELECT artist_show_count FROM norm.artist_venues WHERE artist_uuid=@artist AND venue_id=@venue",
                    new { artist = data.Artists[0], venue = data.Home },
                    cancellationToken: timeout.Token
                )
            );
            committedCount.Should().Be(2);
        }
        finally
        {
            await Commit();
        }
        await pending;
        (await Rows())
            .Where(row => row.Artist == data.Artists[0])
            .Select(row => row.Count)
            .Should()
            .Equal(1, 1);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FullAndScopedRefreshesExcludeEachOther(bool fullFirst)
    {
        await Refresh();
        await Commit();
        await QueryRefresh(db, fullFirst ? null : [data.Artists[0]], tx);
        await using var waiting = await MigratedTestDatabase.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = QueryRefresh(
            waiting,
            fullFirst ? [data.Artists[1]] : null,
            cancellationToken: timeout.Token
        );
        try
        {
            await WaitUntilBlocked(waiting.ProcessID, db.ProcessID);
        }
        finally
        {
            await Commit();
        }
        await pending;
        (await Rows()).Select(row => row.Count).Should().BeEquivalentTo(new[] { 2, 1 });
    }

    [Test]
    public async Task ReversedArtistOrdersCompleteWithoutDeadlocking()
    {
        await Refresh();
        await Commit();
        await QueryRefresh(db, [data.Artists[0]], tx);
        await using var first = await MigratedTestDatabase.OpenAsync();
        await using var second = await MigratedTestDatabase.OpenAsync();
        await using var firstTx = await first.BeginTransactionAsync();
        await using var secondTx = await second.BeginTransactionAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var firstRefresh = QueryRefresh(first, data.Artists, firstTx, timeout.Token);
        Task<RefreshResult>? secondRefresh = null;
        try
        {
            await WaitUntilBlocked(first.ProcessID, db.ProcessID);
            secondRefresh = QueryRefresh(
                second,
                data.Artists.Reverse().ToArray(),
                secondTx,
                timeout.Token
            );
            await WaitUntilBlocked(second.ProcessID, db.ProcessID, first.ProcessID);
        }
        finally
        {
            await Commit();
        }
        (await firstRefresh).Should().Be(new RefreshResult(2, 0));
        await firstTx.CommitAsync();
        (await secondRefresh!).Should().Be(new RefreshResult(2, 0));
        await secondTx.CommitAsync();
    }

    [Test]
    public async Task MigrationBackfillsExistingAssignments()
    {
        await using var database = await MigrationTestDatabase.CreateAsync(14);
        var catalog = database.Connection;
        Fixture seeded;
        await using (var seed = await catalog.BeginTransactionAsync())
        {
            seeded = await Seed(catalog, seed);
            await seed.CommitAsync();
        }
        database.Migrator.MigrateTo(15);
        var rows = (
            await catalog.QueryAsync<Relationship>(RowSql, new { artists = seeded.Artists })
        ).ToArray();
        rows.Select(row => row.Count).Should().BeEquivalentTo(new[] { 2, 1 });
        var sourceIds = await catalog.QueryAsync<string>(
            "SELECT venue_id FROM norm.show_venues ORDER BY show_uuid"
        );
        sourceIds.Should().OnlyContain(id => id == seeded.Home.Value);
    }

    private Task<RefreshResult> Refresh() => QueryRefresh(db, data.Artists, tx);

    private Task MoveShow(Guid show, VenueId venue) =>
        db.ExecuteAsync(
            "UPDATE norm.show_venues SET venue_id=@venue WHERE show_uuid=@show",
            new { show, venue },
            tx
        );

    private async Task BeginTransaction()
    {
        tx = await db.BeginTransactionAsync();
        await NormStore.BeginAsync(db, tx, AuthorTypes.Moderator, "artist-venue-count-test");
    }

    private async Task Commit()
    {
        await tx.CommitAsync();
        await tx.DisposeAsync();
        await BeginTransaction();
    }

    private static Task<RefreshResult> QueryRefresh(
        NpgsqlConnection connection,
        Guid[]? artists,
        NpgsqlTransaction? transaction = null,
        CancellationToken cancellationToken = default
    ) =>
        connection.QuerySingleAsync<RefreshResult>(
            new CommandDefinition(
                RefreshSql,
                new { artists },
                transaction,
                cancellationToken: cancellationToken
            )
        );

    private async Task<Relationship[]> Rows() =>
        (await db.QueryAsync<Relationship>(RowSql, new { artists = data.Artists }, tx)).ToArray();

    private async Task WaitUntilBlocked(int waitingPid, params int[] blockingPids)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (
                await db.QuerySingleAsync<bool>(
                    "SELECT pg_blocking_pids(@waitingPid) && @blockingPids",
                    new { blockingPids, waitingPid },
                    tx
                )
            )
            {
                return;
            }
            await Task.Delay(20);
        }
        Assert.Fail("The concurrent operation did not wait for an active transaction.");
    }

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
        await connection.ExecuteAsync(
            "INSERT INTO features(artist_id,norm_catalogue) VALUES (@first,true),(@second,false)",
            new { first = ids[0], second = ids[1] },
            transaction
        );
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
        DateTime UpdatedAt
    );

    private sealed class TestDatabase : INormalizationDatabase
    {
        public NpgsqlConnection CreateWriteConnection() => new(LocalTestDatabase.ConnectionString);
    }
}
