using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Relisten;
using Relisten.Api.Models;
using Relisten.Controllers;
using Relisten.Data;
using Relisten.Normalization;
using Relisten.Normalization.Songs;

namespace RelistenApiTests.Normalization;

/// <summary>
/// Stored song totals are derived from recording and setlist membership. These tests
/// write real source rows in disposable databases and exercise refresh visibility,
/// failure recovery, and the same artist lock used by normalization and admin edits.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestSongCounts
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
        tx = await db.BeginTransactionAsync();
        data = await Seed(db, tx);
        await CommitEdit();
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
            await NormStore.BeginAsync(db, cleanup, AuthorTypes.Moderator, "song-count-cleanup");
            await db.ExecuteAsync(
                """
                DELETE FROM norm.track_songs WHERE track_uuid=ANY(@tracks);
                DELETE FROM norm.songs WHERE artist_uuid=ANY(@artists);
                DELETE FROM source_tracks WHERE artist_id=ANY(@artistIds);
                DELETE FROM sources WHERE artist_id=ANY(@artistIds);
                DELETE FROM setlist_songs WHERE artist_id=ANY(@artistIds);
                DELETE FROM setlist_shows WHERE artist_id=ANY(@artistIds);
                DELETE FROM artists WHERE uuid=ANY(@artists);
                """,
                new
                {
                    tracks = data.Tracks,
                    artists = data.Artists,
                    artistIds = data.ArtistIds,
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
    public async Task RefreshCountsDistinctFilesAndShowsWithoutChangingNormalizationHistory()
    {
        var history = await db.QuerySingleAsync<int>(
            "SELECT count(*)::int FROM norm.changes",
            transaction: tx
        );
        var refreshed = await Refresh();

        refreshed.Should().Be(new RefreshResult(4, 2));
        (await Counts(data.Main)).Should().Be(new SongTotals(3, 2));
        (await Counts(data.Other)).Should().Be(new SongTotals(1, 1));
        (await Counts(data.Alias)).Should().Be(new SongTotals(0, 0));
        (await Counts(data.Zero)).Should().Be(new SongTotals(0, 0));
        (await ImportedCount(data.Imported[0])).Should().Be(2);
        (await ImportedCount(data.Imported[1])).Should().Be(0);
        var matches = (
            await db.QueryAsync<Match>(
                "SELECT track_uuid Track,show_uuid Show,matching_part_ordinals Parts FROM norm.song_recording_matches(@artist,@songs)",
                new { artist = data.Artists[0], songs = new[] { data.Main.Value } },
                tx
            )
        ).ToArray();
        matches.Select(row => row.Track).Should().BeEquivalentTo(data.Tracks.Take(3));
        matches.Select(row => row.Show).Distinct().Should().BeEquivalentTo(data.Shows.Take(2));
        matches.Single(row => row.Track == data.Tracks[0]).Parts.Should().Equal(1, 3);
        (await db.QuerySingleAsync<int>("SELECT count(*)::int FROM norm.changes", transaction: tx))
            .Should()
            .Be(history);
    }

    [Test]
    public async Task NoOpRefreshPreservesRowsWhileOrphaningFilesUpdatesCountsAndCanReachZero()
    {
        await Refresh();
        var before = await Rows();
        (await Refresh()).Should().Be(new RefreshResult(0, 0));
        (await Rows()).Should().BeEquivalentTo(before);

        await db.ExecuteAsync(
            "UPDATE source_tracks SET is_orphaned=true WHERE uuid=ANY(@tracks)",
            new { tracks = data.Tracks.Take(3).ToArray() },
            tx
        );
        (await Counts(data.Main)).Should().Be(new SongTotals(3, 2));
        await Refresh();
        (await Counts(data.Main)).Should().Be(new SongTotals(0, 0));
        var changed = (await Rows()).Single(row => row.Id == data.Main.Value);
        changed.CreatedAt.Should().Be(before.Single(row => row.Id == data.Main.Value).CreatedAt);
        changed
            .UpdatedAt.Should()
            .BeAfter(before.Single(row => row.Id == data.Main.Value).UpdatedAt);
        (await ImportedCount(data.Imported[0])).Should().Be(2);
    }

    [Test]
    public async Task BrokenMergeInvalidatesTheWholeFileAndCorrectionRecoversItsCounts()
    {
        await Refresh();
        await db.ExecuteAsync(
            "UPDATE norm.songs SET merged_into=@alias WHERE id=@main",
            new { alias = data.Alias, main = data.Main },
            tx
        );
        await Refresh();
        (await Counts(data.Main)).Should().Be(new SongTotals(0, 0));
        (await Counts(data.Other)).Should().Be(new SongTotals(0, 0));
        (await ImportedCount(data.Imported[0])).Should().Be(2);

        await db.ExecuteAsync(
            "UPDATE norm.songs SET merged_into=NULL WHERE id=@main",
            new { main = data.Main },
            tx
        );
        await Refresh();
        (await Counts(data.Main)).Should().Be(new SongTotals(3, 2));
        (await Counts(data.Other)).Should().Be(new SongTotals(1, 1));
    }

    [Test]
    public async Task SourceSetMismatchDoesNotPublishARecordingMatch()
    {
        await Refresh();
        await db.ExecuteAsync(
            "UPDATE source_tracks SET source_set_id=(SELECT source_set_id FROM source_tracks WHERE uuid=@other) WHERE uuid=@track",
            new { other = data.Tracks[2], track = data.Tracks[0] },
            tx
        );
        await Refresh();
        (await Counts(data.Main)).Should().Be(new SongTotals(2, 2));
        (await Counts(data.Other)).Should().Be(new SongTotals(0, 0));
    }

    [Test]
    public async Task DeletingImportedEvidenceClearsItsCountWithoutChangingRecordingCounts()
    {
        await Refresh();
        await db.ExecuteAsync(
            "DELETE FROM setlist_songs_plays WHERE played_setlist_song_id=@song",
            new { song = data.Imported[0] },
            tx
        );
        (await ImportedCount(data.Imported[0])).Should().Be(2);
        await Refresh();
        (await ImportedCount(data.Imported[0])).Should().Be(0);
        (await Counts(data.Main)).Should().Be(new SongTotals(3, 2));
    }

    [Test]
    public async Task ServiceScopesRefreshesAndReconcilesOwnersWithoutTouchingUnselectedArtists()
    {
        var service = new SongCounts(new TestDatabase());
        await service.RefreshArtistsAsync([data.Artists[0]]);
        (await Counts(data.Main)).Should().Be(new SongTotals(3, 2));
        (
            await db.QuerySingleAsync<int>(
                "SELECT count(*)::int FROM norm.song_counts WHERE song_id=@song",
                new { song = data.SecondArtistSong },
                tx
            )
        )
            .Should()
            .Be(0);
        var before = await Rows();
        await service.RefreshArtistsAsync([]);
        (await Rows()).Should().BeEquivalentTo(before);

        await service.RefreshAllAsync();
        (await Counts(data.SecondArtistSong)).Should().Be(new SongTotals(1, 1));
        (await Rows()).Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task RebuildCompletionKeepsCommittedImportsOnCountFailureAndRepairsThemOnRetry()
    {
        await Refresh();
        await db.ExecuteAsync(
            "UPDATE source_tracks SET is_orphaned=true WHERE uuid=@track",
            new { track = data.Tracks[0] },
            tx
        );
        await CommitEdit();
        var previousWrite = DbService.ConnStr;
        var previousRead = DbService.ReadOnlyConnStr;
        var redis = new RedisService(RedisService.BuildConfiguration("redis://127.0.0.1:16379"));
        try
        {
            var database = new DbService(
                LocalTestDatabase.DatabaseUrl,
                null,
                null,
                Host.CreateApplicationBuilder().Environment,
                NullLogger<DbService>.Instance
            );
            var artist = new Artist
            {
                id = (int)data.ArtistIds[0],
                uuid = data.Artists[0],
                features = new Features(),
            };
            var key = ArtistsController.FullArtistCacheKey(artist);
            await redis.db.StringSetAsync(key, "stale artist data");
            var failing = new CatalogRebuildService(
                database,
                redis,
                new SongCounts(new UnavailableDatabase()),
                NullLogger<CatalogRebuildService>.Instance
            );

            await failing.InvalidateArtistCache(artist);

            (await redis.db.KeyExistsAsync(key)).Should().BeFalse();
            (await Counts(data.Main)).Should().Be(new SongTotals(3, 2));
            var retry = new CatalogRebuildService(
                database,
                redis,
                new SongCounts(new TestDatabase()),
                NullLogger<CatalogRebuildService>.Instance
            );
            await retry.InvalidateArtistCache(artist);
            (await Counts(data.Main)).Should().Be(new SongTotals(2, 2));
        }
        finally
        {
            redis.connection.Dispose();
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead;
        }
    }

    [Test]
    public async Task RefreshSerializesOneArtistWhileOtherArtistsAndReadersProceed()
    {
        await Refresh();
        await CommitEdit();
        await db.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtext('norm-catalogue'),hashtext(@artist::text))",
            new { artist = data.Artists[0] },
            tx
        );
        await db.ExecuteAsync(
            "UPDATE source_tracks SET is_orphaned=true WHERE uuid=@track",
            new { track = data.Tracks[0] },
            tx
        );
        await Refresh();

        await using var waiting = await MigratedTestDatabase.OpenAsync();
        await using var reader = await MigratedTestDatabase.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var refresh = QueryRefresh(waiting, data.Artists[0], cancellationToken: timeout.Token);
        var read = new CommandDefinition(
            TotalsSql,
            new { song = data.Main.Value },
            cancellationToken: timeout.Token
        );
        try
        {
            await WaitUntilBlocked(waiting.ProcessID);
            await QueryRefresh(reader, data.Artists[1], cancellationToken: timeout.Token);
            (await reader.QuerySingleAsync<SongTotals>(read)).Should().Be(new SongTotals(3, 2));
        }
        finally
        {
            await CommitEdit();
        }
        await refresh;
        (await reader.QuerySingleAsync<SongTotals>(read)).Should().Be(new SongTotals(2, 2));
        (await Counts(data.Other)).Should().Be(new SongTotals(0, 0));
    }

    [Test]
    public async Task MaintenanceRoleCanRefreshWithoutWritingTheCounterTablesDirectly()
    {
        var role = "song_count_maintenance_" + Guid.NewGuid().ToString("N");
        await db.ExecuteAsync(
            $"""
            CREATE ROLE {role} NOLOGIN;
            GRANT USAGE ON SCHEMA norm TO {role};
            GRANT EXECUTE ON FUNCTION norm.refresh_song_counts(uuid) TO {role};
            SET LOCAL ROLE {role};
            """,
            transaction: tx
        );
        (await Refresh()).Should().Be(new RefreshResult(4, 2));
        var write = () =>
            db.ExecuteAsync("UPDATE norm.song_counts SET matched_track_count=0", transaction: tx);
        await write
            .Should()
            .ThrowAsync<PostgresException>()
            .Where(error => error.SqlState == PostgresErrorCodes.InsufficientPrivilege);
    }

    [Test]
    public async Task MigrationSeedsExistingSongsAndSetlistsWithoutRewritingTheirEvidence()
    {
        await using var database = await MigrationTestDatabase.CreateAsync(15);
        var catalog = database.Connection;
        Fixture fixture;
        await using (var seed = await catalog.BeginTransactionAsync())
        {
            fixture = await Seed(catalog, seed);
            await seed.CommitAsync();
        }
        var history = await catalog.QuerySingleAsync<int>("SELECT count(*)::int FROM norm.changes");
        database.Migrator.MigrateTo(16);
        (await catalog.QuerySingleAsync<SongTotals>(TotalsSql, new { song = fixture.Main.Value }))
            .Should()
            .Be(new SongTotals(3, 2));
        (
            await catalog.QuerySingleAsync<int>(
                "SELECT setlist_show_count FROM setlist_song_counts WHERE setlist_song_id=@song",
                new { song = fixture.Imported[0] }
            )
        )
            .Should()
            .Be(2);
        (await catalog.QuerySingleAsync<int>("SELECT count(*)::int FROM norm.changes"))
            .Should()
            .Be(history);
    }

    private Task<RefreshResult> Refresh() => QueryRefresh(db, data.Artists[0], tx);

    private static Task<RefreshResult> QueryRefresh(
        NpgsqlConnection connection,
        Guid artist,
        NpgsqlTransaction? transaction = null,
        CancellationToken cancellationToken = default
    ) =>
        connection.QuerySingleAsync<RefreshResult>(
            new CommandDefinition(
                "SELECT songs_changed SongsChanged,imported_songs_changed ImportedSongsChanged FROM norm.refresh_song_counts(@artist)",
                new { artist },
                transaction,
                cancellationToken: cancellationToken
            )
        );

    private Task<SongTotals> Counts(SongId song) =>
        db.QuerySingleAsync<SongTotals>(TotalsSql, new { song = song.Value }, tx);

    private Task<int> ImportedCount(long song) =>
        db.QuerySingleAsync<int>(
            "SELECT setlist_show_count FROM setlist_song_counts WHERE setlist_song_id=@song",
            new { song },
            tx
        );

    private async Task<CountRow[]> Rows() =>
        (
            await db.QueryAsync<CountRow>(
                "SELECT song_id Id,matched_track_count Tracks,matched_show_count Shows,created_at CreatedAt,updated_at UpdatedAt FROM norm.song_counts WHERE song_id=ANY(@songs) ORDER BY song_id",
                new
                {
                    songs = new[]
                    {
                        data.Main.Value,
                        data.Other.Value,
                        data.Alias.Value,
                        data.Zero.Value,
                    },
                },
                tx
            )
        ).ToArray();

    private async Task BeginEdit()
    {
        tx = await db.BeginTransactionAsync();
        await NormStore.BeginAsync(db, tx, AuthorTypes.Moderator, "song-count-tests");
    }

    private async Task CommitEdit()
    {
        await tx.CommitAsync();
        await tx.DisposeAsync();
        await BeginEdit();
    }

    private async Task WaitUntilBlocked(int waiting)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (
                await db.QuerySingleAsync<bool>(
                    "SELECT @blocking = ANY(pg_blocking_pids(@waiting))",
                    new { blocking = db.ProcessID, waiting },
                    tx
                )
            )
            {
                return;
            }
            await Task.Delay(20);
        }
        Assert.Fail("The refresh did not wait for the artist's normalization transaction.");
    }

    private const string TotalsSql =
        "SELECT matched_track_count Tracks,matched_show_count Shows FROM norm.song_counts WHERE song_id=@song";

    private sealed record RefreshResult(int SongsChanged, int ImportedSongsChanged);

    private sealed record SongTotals(int Tracks, int Shows);

    private sealed record CountRow(
        string Id,
        int Tracks,
        int Shows,
        DateTime CreatedAt,
        DateTime UpdatedAt
    );

    private sealed class Match
    {
        public Guid Track { get; set; }
        public Guid Show { get; set; }
        public int[] Parts { get; set; } = [];
    }

    private sealed record Fixture(
        Guid[] Artists,
        long[] ArtistIds,
        Guid[] Shows,
        Guid[] Tracks,
        SongId Main,
        SongId Other,
        SongId Alias,
        SongId Zero,
        SongId SecondArtistSong,
        long[] Imported
    );

    private sealed class TestDatabase : INormalizationDatabase
    {
        public NpgsqlConnection CreateWriteConnection() => new(LocalTestDatabase.ConnectionString);
    }

    private sealed class UnavailableDatabase : INormalizationDatabase
    {
        public NpgsqlConnection CreateWriteConnection() =>
            new("Host=127.0.0.1;Port=1;Database=unavailable;Username=relisten;Timeout=1");
    }

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
                "INSERT INTO artists(uuid,name,slug,updated_at) VALUES(@uuid,'Song Count Test',@slug,now()) RETURNING id",
                new { uuid = artists[index], slug = "song-count-" + artists[index] },
                transaction
            );
        }
        var shows = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var showIds = new long[4];
        for (var index = 0; index < shows.Length; index++)
        {
            var owner = index == 3 ? 1 : 0;
            var day = index == 3 ? 3 : index + 1;
            showIds[index] = await connection.QuerySingleAsync<long>(
                "INSERT INTO shows(uuid,artist_id,date,display_date,updated_at) VALUES(@uuid,@artist,@date::date,@date,now()) RETURNING id",
                new
                {
                    uuid = shows[index],
                    artist = ids[owner],
                    date = $"2000-01-0{day}",
                },
                transaction
            );
        }
        var tracks = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        // Each file has its own recording and set. Reusing its UUID across these
        // separate tables keeps the fixture keys simple; all IDs remain table-scoped.
        await connection.ExecuteAsync(
            """
            WITH recording AS (
                INSERT INTO sources(uuid,artist_id,show_id,is_soundboard,is_remaster,has_jamcharts,upstream_identifier,display_date,updated_at)
                VALUES(@track,@artist,@show,false,false,false,@track::text,'2000-01-01',now()) RETURNING id
            ), recording_set AS (
                INSERT INTO source_sets(uuid,source_id,index,name,updated_at)
                SELECT @track,id,1,'Set 1',now() FROM recording RETURNING id,source_id
            )
            INSERT INTO source_tracks(uuid,artist_id,source_id,source_set_id,track_position,title,slug,mp3_url,is_orphaned,updated_at)
            SELECT @track,@artist,source_id,id,1,'Imported title',@track::text,
                   'https://example.invalid/' || @track::text || '.mp3',@orphan,now()
            FROM recording_set
            """,
            tracks.Select(
                (track, index) =>
                    new
                    {
                        track,
                        artist = ids[index == 5 ? 1 : 0],
                        show = showIds[
                            index == 5 ? 3
                            : index < 2 ? 0
                            : 1
                        ],
                        orphan = index == 3,
                    }
            ),
            transaction
        );
        var main = SongId.New();
        var other = SongId.New();
        var alias = SongId.New();
        var zero = SongId.New();
        var second = SongId.New();
        var store = await NormStore.BeginAsync(
            connection,
            transaction,
            AuthorTypes.Moderator,
            "song-count-tests"
        );
        await store.CreateSongsAsync([
            new(main, artists[0], "Scarlet Begonias"),
            new(other, artists[0], "Fire on the Mountain"),
            new(alias, artists[0], "Old Scarlet"),
            new(zero, artists[0], "Unrecorded Song"),
            new(second, artists[1], "Other Artist Song"),
        ]);
        await connection.ExecuteAsync(
            "UPDATE norm.songs SET merged_into=@main WHERE id=@alias",
            new { main, alias },
            transaction
        );
        var parts = new (int Track, int Part, SongId? Song, bool Resolved)[]
        {
            (0, 1, main, true),
            (0, 2, other, true),
            (0, 3, main, true),
            (1, 1, alias, true),
            (1, 2, null, false),
            (2, 1, main, true),
            (3, 1, main, true),
            (4, 1, main, false),
            (5, 1, second, true),
        };
        await connection.ExecuteAsync(
            """
            INSERT INTO norm.track_songs(track_uuid,part,song_id,text,resolved,basis,author_type,author_ref,run_uuid)
            VALUES(@track,@part,@song,'Imported title',@resolved,'ruling','moderator','song-count-tests',current_setting('norm.run_uuid')::uuid)
            """,
            parts.Select(part => new
            {
                track = tracks[part.Track],
                part = part.Part,
                song = part.Song?.Value,
                resolved = part.Resolved,
            }),
            transaction
        );
        var imported = new long[3];
        for (var index = 0; index < imported.Length; index++)
        {
            imported[index] = await connection.QuerySingleAsync<long>(
                "INSERT INTO setlist_songs(uuid,artist_id,name,slug,upstream_identifier,updated_at) VALUES(@uuid,@artist,'Imported Song',@key,@key,now()) RETURNING id",
                new
                {
                    uuid = Guid.NewGuid(),
                    artist = ids[index == 2 ? 1 : 0],
                    key = Guid.NewGuid().ToString(),
                },
                transaction
            );
        }
        var venue = await connection.QuerySingleAsync<long>(
            "INSERT INTO venues(uuid,artist_id,name,slug,updated_at) VALUES(@uuid,@artist,'Setlist Hall',@slug,now()) RETURNING id",
            new
            {
                uuid = Guid.NewGuid(),
                artist = ids[0],
                slug = Guid.NewGuid().ToString(),
            },
            transaction
        );
        foreach (var day in new[] { 1, 1, 3, 9 })
        {
            var setlist = await connection.QuerySingleAsync<long>(
                "INSERT INTO setlist_shows(uuid,artist_id,venue_id,date,upstream_identifier,updated_at) VALUES(@uuid,@artist,@venue,@date::date,@key,now()) RETURNING id",
                new
                {
                    uuid = Guid.NewGuid(),
                    artist = ids[0],
                    venue,
                    date = $"2000-01-0{day}",
                    key = Guid.NewGuid().ToString(),
                },
                transaction
            );
            await connection.ExecuteAsync(
                "INSERT INTO setlist_songs_plays(played_setlist_song_id,played_setlist_show_id) VALUES(@song,@show)",
                new { song = imported[0], show = setlist },
                transaction
            );
        }
        await store.FinishAsync();
        return new(artists, ids, shows, tracks, main, other, alias, zero, second, imported);
    }
}
