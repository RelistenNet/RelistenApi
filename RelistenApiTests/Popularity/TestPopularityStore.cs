using Dapper;
using FluentAssertions;
using Hangfire;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Relisten;
using Relisten.Api.Models;
using Relisten.Services.Popularity;

namespace RelistenApiTests.Popularity;

/// <summary>Real publication and calculation boundaries in the disposable catalog.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestPopularityStore
{
    private NpgsqlConnection connection = null!;
    private PopularityStore store = null!;
    private DbService db = null!;
    private string previousPrimary = null!;
    private string previousReplica = null!;
    private readonly Guid artist = Guid.NewGuid();
    private readonly Guid show = Guid.NewGuid();
    private readonly Guid emptyArtist = Guid.NewGuid();
    private readonly Guid year = Guid.NewGuid();
    private readonly Guid track = Guid.NewGuid();

    [OneTimeSetUp]
    public async Task Start()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        connection = await MigratedTestDatabase.OpenAsync();
        previousPrimary = DbService.ConnStr;
        previousReplica = DbService.ReadOnlyConnStr;
        db = new DbService(
            LocalTestDatabase.DatabaseUrl,
            null,
            null,
            new HostingEnvironment { EnvironmentName = Environments.Production },
            NullLogger<DbService>.Instance
        );
        store = new PopularityStore(db);
        await connection.ExecuteAsync(
            """
            INSERT INTO artists(uuid,name,slug,updated_at) VALUES (@artist,'Popularity fixture',@artist::text,now()),(@emptyArtist,'Empty popularity fixture',@emptyArtist::text,now());
            INSERT INTO years(uuid,artist_id,year,updated_at)
                SELECT @year,id,'1985',now() FROM artists WHERE uuid=@artist;
            INSERT INTO shows(uuid,artist_id,year_id,date,display_date,updated_at)
                SELECT @show,a.id,y.id,'1985-07-07','1985-07-07',now()
                FROM artists a JOIN years y ON y.artist_id=a.id WHERE a.uuid=@artist;
            INSERT INTO sources(uuid,artist_id,show_id,upstream_identifier,display_date,is_soundboard,is_remaster,has_jamcharts,updated_at)
                SELECT @show,a.id,s.id,@show::text,'1985-07-07',false,false,false,now()
                FROM artists a JOIN shows s ON s.artist_id=a.id WHERE a.uuid=@artist;
            INSERT INTO source_tracks(uuid,artist_id,source_id,track_position,title,slug,mp3_url,duration,updated_at)
                SELECT @track,a.id,s.id,1,'Fixture','fixture','https://example.test/file.mp3',120,now()
                FROM artists a JOIN sources s ON s.artist_id=a.id WHERE a.uuid=@artist;
            """,
            new
            {
                artist,
                show,
                year,
                track,
                emptyArtist,
            }
        );
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        if (connection is null)
        {
            return;
        }
        await connection.ExecuteAsync(
            "DELETE FROM artists WHERE uuid=ANY(@artists)",
            new { artists = new[] { artist, emptyArtist } }
        );
        await connection.DisposeAsync();
        DbService.ConnStr = previousPrimary;
        DbService.ReadOnlyConnStr = previousReplica;
    }

    [Test]
    public async Task Prepared_show_metadata_read_survives_an_added_column()
    {
        var metrics = PopularityService.CreateMetrics(12, 0, 0, 0, 12, 120, 0, 0);
        metrics.momentum_score = 0.5;
        await store.ReplaceArtistShows(
            artist,
            new Dictionary<Guid, CachedShowPopularity>
            {
                [show] = CachedShowPopularityMapper.FromPopularityMetrics(metrics),
            },
            DateTime.UtcNow
        );

        try
        {
            // Use one physical connection with the application's auto-prepare settings.
            // The other connection applies DDL as a deployment would.
            await using var reader = db.CreateConnection(longTimeout: false, readOnly: true);
            await reader.OpenAsync();
            var settings = new NpgsqlConnectionStringBuilder(reader.ConnectionString);
            for (var i = 0; i <= settings.AutoPrepareMinUsages; i++)
            {
                await PopularityStore.ReadArtistLocalShowMetrics(reader, [show]);
            }
            (await HasPreparedPopularityRead(reader)).Should().BeTrue();

            await connection.ExecuteAsync(
                "ALTER TABLE show_popularity ADD COLUMN projection_probe bigint"
            );
            try
            {
                var after = await PopularityStore.ReadArtistLocalShowMetrics(reader, [show]);
                after[show].windows.days_30d.plays.Should().Be(12);
                after[show].momentum_score.Should().Be(0.5);
            }
            finally
            {
                await connection.ExecuteAsync(
                    "ALTER TABLE show_popularity DROP COLUMN projection_probe"
                );
            }
        }
        finally
        {
            await connection.ExecuteAsync(
                "DELETE FROM show_popularity WHERE uuid=@show AND NOT is_global",
                new { show }
            );
        }
    }

    private static Task<bool> HasPreparedPopularityRead(NpgsqlConnection reader) =>
        reader.QuerySingleAsync<bool>(
            """
            SELECT EXISTS (
                SELECT FROM pg_prepared_statements
                WHERE statement ILIKE '%show_popularity%'
                    AND parameter_types = ARRAY['uuid[]'::regtype]
            )
            """
        );

    [Test]
    public async Task Replacement_preserves_other_populations_and_rejects_a_late_old_calculation()
    {
        var time = DateTime.UtcNow;
        var global = PopularityService.CreateMetrics(100, 20, 1, 10, 200, 12000, 2400, 1200);
        global.momentum_score = 0.25;
        await store.ReplaceGlobalShows(
            new Dictionary<Guid, PopularityMetrics> { [show] = global },
            time
        );
        var local = CachedShowPopularityMapper.FromPopularityMetrics(global);
        local.momentum_score = 0.9;
        local.momentum = new() { raw = 0.9, organic = 0.6 };
        await store.ReplaceArtistShows(
            artist,
            new Dictionary<Guid, CachedShowPopularity> { [show] = local },
            time
        );

        var rows = (
            await connection.QueryAsync<StoredPopularity>(
                "SELECT * FROM show_popularity WHERE uuid=@show ORDER BY is_global DESC",
                new { show }
            )
        ).ToArray();
        rows.Should().HaveCount(2);
        Newtonsoft
            .Json.JsonConvert.SerializeObject(rows[0].ToMetrics())
            .Should()
            .Be(Newtonsoft.Json.JsonConvert.SerializeObject(global));
        rows[1].ToMetrics().momentum_score.Should().Be(0.9);
        rows[1].ToMetrics(organic: true).momentum_score.Should().Be(0.6);

        await store.ReplaceArtistShows(
            artist,
            new Dictionary<Guid, CachedShowPopularity>(),
            time.AddSeconds(1)
        );
        await store.ReplaceArtistShows(
            artist,
            new Dictionary<Guid, CachedShowPopularity> { [show] = local },
            time
        );
        (
            await connection.QueryAsync<bool>(
                "SELECT is_global FROM show_popularity WHERE uuid=@show AND momentum_score IS NOT NULL",
                new { show }
            )
        )
            .Should()
            .Equal(true);
        var inactive = await connection.QuerySingleAsync<StoredPopularity>(
            "SELECT * FROM show_popularity WHERE uuid=@show AND NOT is_global",
            new { show }
        );
        inactive.plays_30d.Should().Be(0);
        inactive.organic_momentum_score.Should().BeNull();
        await connection.ExecuteAsync("DELETE FROM show_popularity WHERE uuid=@show", new { show });
    }

    [Test]
    public async Task Stale_and_missing_populations_queue_once_while_fresh_empty_results_do_not()
    {
        // Other integration fixtures may have published the global population.
        // This case specifically exercises a missing global population.
        await connection.ExecuteAsync(
            "DELETE FROM show_popularity WHERE is_global OR artist_uuid=@artist; DELETE FROM year_popularity WHERE artist_uuid=@artist AND NOT is_global",
            new { artist }
        );
        await store.ReplaceArtistShows(
            artist,
            new Dictionary<Guid, CachedShowPopularity>(),
            DateTime.UtcNow.AddHours(-2)
        );
        var jobs = new RecordingPopularityJobs();
        await connection.ExecuteAsync(
            "DELETE FROM show_popularity WHERE uuid=@show AND is_global",
            new { show }
        );
        await store.ReplaceGlobalShows(
            new Dictionary<Guid, PopularityMetrics>(),
            DateTime.UtcNow.AddHours(-2)
        );
        var request = new PopularityRefreshRequest(db, jobs);
        request.Require(PopularityPopulationKind.Show, artist);
        request.Require(PopularityPopulationKind.Show, artist);
        request.Require(PopularityPopulationKind.Year, artist);
        request.Require(PopularityPopulationKind.Show, Guid.NewGuid());
        request.Require(PopularityPopulationKind.Show, Guid.Empty);
        request.Require(PopularityPopulationKind.Show, emptyArtist);
        request.Require(PopularityPopulationKind.Year, emptyArtist);
        request.Require(PopularityPopulationKind.Show);
        await request.EnqueueStale(CancellationToken.None);
        jobs.Batches.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new[]
                {
                    new PopularityPopulation(PopularityPopulationKind.Show, artist),
                    new PopularityPopulation(PopularityPopulationKind.Year, artist),
                    new PopularityPopulation(PopularityPopulationKind.Show),
                }
            );

        await store.ReplaceArtistShows(
            artist,
            new Dictionary<Guid, CachedShowPopularity>(),
            DateTime.UtcNow
        );
        await store.ReplaceYears(
            artist,
            new Dictionary<Guid, PopularityMetrics>(),
            DateTime.UtcNow
        );
        await store.ReplaceGlobalShows(new Dictionary<Guid, PopularityMetrics>(), DateTime.UtcNow);
        await request.EnqueueStale(CancellationToken.None);
        jobs.Batches.Should().ContainSingle();

        // Catalog repair can move a show. Stale rows for its former owner must
        // not keep requesting refreshes when that owner has no targets left.
        await connection.ExecuteAsync(
            """
            UPDATE show_popularity SET generated_at=now()-interval '2 hours' WHERE uuid=@show AND NOT is_global;
            UPDATE shows SET artist_id=(SELECT id FROM artists WHERE uuid=@emptyArtist) WHERE uuid=@show;
            """,
            new { show, emptyArtist }
        );
        try
        {
            var movedJobs = new RecordingPopularityJobs();
            var moved = new PopularityRefreshRequest(db, movedJobs);
            moved.Require(PopularityPopulationKind.Show, artist);
            await moved.EnqueueStale(CancellationToken.None);
            movedJobs.Batches.Should().BeEmpty();
        }
        finally
        {
            await connection.ExecuteAsync(
                "UPDATE shows SET artist_id=(SELECT id FROM artists WHERE uuid=@artist) WHERE uuid=@show",
                new { artist, show }
            );
        }
    }

    [Test]
    public async Task Refresh_publishes_the_same_calculation_to_Postgres_and_legacy_Redis()
    {
        var options = RedisService.BuildConfiguration("redis://127.0.0.1:16379");
        options.DefaultDatabase = 14;
        var redis = new RedisService(options);
        var cache = new PopularityCacheService(redis);
        var jobs = System.Reflection.DispatchProxy.Create<IBackgroundJobClient, NoRefresh>();
        var service = new PopularityService(
            db,
            cache,
            jobs,
            NullLogger<PopularityService>.Instance
        );
        var key = $"popularity:artist:{artist}:shows:map:30d-48h";
        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO source_track_plays(created_at,source_track_uuid,app_type)
                VALUES (now()-interval '2 days',@track,0),(now()-interval '3 hours',@track,0);
                """,
                new { track }
            );
            await connection.ExecuteAsync(
                "CALL refresh_continuous_aggregate('source_track_plays_daily',NULL,NULL)"
            );
            await connection.ExecuteAsync(
                "CALL refresh_continuous_aggregate('source_track_plays_hourly',NULL,NULL)"
            );
            await service.RefreshReadModel();
            var legacy = (await cache.GetAsync<Dictionary<Guid, CachedShowPopularity>>(key))
                .Entry!
                .data[show];
            var local = await connection.QuerySingleAsync<StoredPopularity>(
                "SELECT * FROM show_popularity WHERE uuid=@show AND NOT is_global",
                new { show }
            );
            // Redis's public double converters round to four decimal places.
            Newtonsoft
                .Json.Linq.JToken.DeepEquals(
                    Newtonsoft.Json.Linq.JToken.Parse(
                        Newtonsoft.Json.JsonConvert.SerializeObject(local.ToMetrics())
                    ),
                    Newtonsoft.Json.Linq.JToken.Parse(
                        Newtonsoft.Json.JsonConvert.SerializeObject(
                            CachedShowPopularityMapper.ToPopularityMetrics(legacy)
                        )
                    )
                )
                .Should()
                .BeTrue();
            local
                .organic_momentum_score.Should()
                .Be(CachedShowPopularityMapper.GetRankingMomentumScore(legacy));
            local.plays_30d.Should().Be(2);
            (
                await connection.QueryAsync<bool>(
                    "SELECT is_global FROM year_popularity WHERE uuid=@year",
                    new { year }
                )
            )
                .Should()
                .BeEquivalentTo(new[] { true, false });

            // A failure in the second store cannot undo a committed Postgres publication.
            await connection.ExecuteAsync(
                "INSERT INTO source_track_plays(created_at,source_track_uuid,app_type) VALUES (now()-interval '4 hours',@track,0)",
                new { track }
            );
            await connection.ExecuteAsync(
                "CALL refresh_continuous_aggregate('source_track_plays_daily',NULL,NULL)"
            );
            await connection.ExecuteAsync(
                "CALL refresh_continuous_aggregate('source_track_plays_hourly',NULL,NULL)"
            );
            redis.connection.Dispose();
            await FluentActions
                .Awaiting(() => service.RefreshShowPopularityMapForArtist(artist))
                .Should()
                .ThrowAsync<ObjectDisposedException>();
            (
                await connection.QuerySingleAsync<long>(
                    "SELECT plays_30d FROM show_popularity WHERE uuid=@show AND NOT is_global",
                    new { show }
                )
            )
                .Should()
                .Be(3);

            redis = new RedisService(options);
            cache = new PopularityCacheService(redis);
            service = new PopularityService(
                db,
                cache,
                jobs,
                NullLogger<PopularityService>.Instance
            );
            await service.RefreshPopulations([new(PopularityPopulationKind.Show, artist)]);
            (await cache.GetAsync<Dictionary<Guid, CachedShowPopularity>>(key))
                .Entry!.data[show]
                .windows.days_30d.plays.Should()
                .Be(3);
        }
        finally
        {
            redis.connection.Dispose();
        }
    }

    [Test]
    public async Task Monthly_job_refreshes_the_last_day_before_publishing_a_closed_month()
    {
        var showId = Guid.NewGuid();
        var source = Guid.NewGuid();
        var trackId = Guid.NewGuid();
        var cutoff = DateTime.UtcNow.AddDays(-1);
        var end = new DateTime(cutoff.Year, cutoff.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var start = end.AddMonths(-1);
        var lastDay = end.AddDays(-1);
        var from = start.ToString("yyyy-MM-dd") + " 00:00Z";
        var to = end.ToString("yyyy-MM-dd") + " 00:00Z";
        var beforeLastDay = lastDay.ToString("yyyy-MM-dd") + " 00:00Z";
        try
        {
            await AddShowWithTracks(showId, source, (trackId, 1, 120));
            await connection.ExecuteAsync(
                """
                INSERT INTO source_track_plays(created_at,source_track_uuid,app_type)
                VALUES (@firstPlay,@trackId,0),(@lastPlay,@trackId,0);
                """,
                new
                {
                    trackId,
                    firstPlay = start.AddHours(12),
                    lastPlay = lastDay.AddHours(12),
                }
            );

            // Materialize the month before its final day has reached the daily rollup.
            await RefreshRollup("source_track_plays_daily", from, beforeLastDay);
            await RefreshRollup("source_track_plays_monthly", from, to);
            var through = DateOnly.FromDateTime(lastDay);
            (await YearlyShowTotal(showId, through))
                .Should()
                .Be(new YearlyPopularityTotal(showId, 1, 120));

            var jobId = await connection.QuerySingleAsync<int>(
                """
                SELECT job_id FROM timescaledb_information.jobs
                WHERE proc_schema='public' AND proc_name='refresh_source_track_plays_monthly'
                """
            );
            await connection.ExecuteAsync("CALL run_job(@jobId)", new { jobId });

            (await YearlyShowTotal(showId, through))
                .Should()
                .Be(new YearlyPopularityTotal(showId, 2, 240));
        }
        finally
        {
            await DeleteShowWithPlays(showId, [trackId]);
            await RefreshRollup("source_track_plays_daily", from, to);
            await RefreshRollup("source_track_plays_monthly", from, to);
        }
    }

    [Test]
    public async Task YearlyRollupSplitsUtcMonthEdgesAndCountsSourceDaysOnce()
    {
        var showId = Guid.NewGuid();
        var source = Guid.NewGuid();
        var firstTrack = Guid.NewGuid();
        var secondTrack = Guid.NewGuid();
        try
        {
            await AddShowWithTracks(showId, source, (firstTrack, 1, 120), (secondTrack, 2, 180));
            await connection.ExecuteAsync(
                """
                INSERT INTO source_track_plays(created_at,source_track_uuid,app_type)
                VALUES ('2023-03-01 23:59Z',@firstTrack,0),
                       ('2023-03-02 00:00Z',@firstTrack,0),
                       ('2023-03-02 12:00Z',@secondTrack,0),
                       ('2023-05-12 12:00Z',@firstTrack,0),
                       ('2024-02-29 12:00Z',@firstTrack,0),
                       ('2024-02-29 14:00Z',@firstTrack,0),
                       ('2024-03-01 00:00Z',@firstTrack,0);
                """,
                new { firstTrack, secondTrack }
            );
            await RefreshRollup("source_track_plays_daily", "2023-03-01", "2024-04-01");
            await RefreshRollup("source_track_plays_monthly", "2023-03-01", "2024-04-01");
            await RefreshRollup("source_plays_daily", "2023-03-01", "2024-04-01");

            // February is a whole month at leap day and a partial month the day before.
            var throughLeapDay = new DateOnly(2024, 2, 29);
            (await YearlyShowTotal(showId, throughLeapDay))
                .Should()
                .Be(new YearlyPopularityTotal(showId, 5, 660));
            (await YearlyShowTotal(showId, new DateOnly(2024, 2, 28)))
                .Should()
                .Be(new YearlyPopularityTotal(showId, 4, 540));

            // Both files played on 2023-03-02: the performance and its source each
            // count that day once.
            (
                await PlayRollupReader.ReadPerformances(
                    connection,
                    [new PerformancePlaysRequest([firstTrack, secondTrack], source)],
                    throughLeapDay,
                    null,
                    CancellationToken.None
                )
            )
                .Should()
                .Equal(
                    new PerformancePlays(Index: 0, Plays: 5, Days: 3, SourcePlays: 5, SourceDays: 3)
                );
            await AssertMonthTurnoverReadsDailyTail(showId, firstTrack);
        }
        finally
        {
            await DeleteShowWithPlays(showId, [firstTrack, secondTrack]);
            await RefreshRollup("source_track_plays_daily", "2023-03-01", "2024-04-01");
            await RefreshRollup("source_track_plays_monthly", "2023-03-01", "2024-04-01");
            await RefreshRollup("source_plays_daily", "2023-03-01", "2024-04-01");
        }
    }

    private async Task AssertMonthTurnoverReadsDailyTail(Guid showId, Guid trackId)
    {
        // Earlier tests may have refreshed through the current month. Use the next
        // month to test a full bucket beyond the monthly materialization watermark.
        var today = DateTime.UtcNow;
        var monthEnd = new DateOnly(today.Year, today.Month, 1).AddMonths(2);
        var lastDay = monthEnd.AddDays(-1);
        var playAt = lastDay.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);
        var from = lastDay.ToString("yyyy-MM-dd") + " 00:00Z";
        var to = monthEnd.ToString("yyyy-MM-dd") + " 00:00Z";
        try
        {
            await connection.ExecuteAsync(
                "INSERT INTO source_track_plays(created_at,source_track_uuid,app_type) VALUES (@playAt,@trackId,0)",
                new { playAt, trackId }
            );
            await RefreshRollup("source_track_plays_daily", from, to);

            // The upper rollup has never materialized this month. At the first
            // instant of the next month, yearly reads choose it as a whole bucket.
            (await YearlyShowTotal(showId, lastDay))
                .Should()
                .Be(new YearlyPopularityTotal(showId, 1, 120));
        }
        finally
        {
            await connection.ExecuteAsync(
                "DELETE FROM source_track_plays WHERE source_track_uuid=@trackId AND created_at=@playAt",
                new { playAt, trackId }
            );
            await RefreshRollup("source_track_plays_daily", from, to);
        }
    }

    // Other tests leave recent plays on the shared fixture show. A dedicated show
    // keeps these exact yearly totals independent of them.
    private Task AddShowWithTracks(
        Guid showId,
        Guid source,
        params (Guid Uuid, int Position, int Duration)[] tracks
    ) =>
        connection.ExecuteAsync(
            """
            INSERT INTO shows(uuid,artist_id,year_id,date,display_date,updated_at)
            SELECT @showId,artist_id,id,'1985-07-08','1985-07-08',now() FROM years WHERE uuid=@year;
            INSERT INTO sources(uuid,artist_id,show_id,upstream_identifier,display_date,is_soundboard,is_remaster,has_jamcharts,updated_at)
            SELECT @source,artist_id,id,@source::text,display_date,false,false,false,now() FROM shows WHERE uuid=@showId;
            INSERT INTO source_tracks(uuid,artist_id,source_id,track_position,title,slug,mp3_url,duration,updated_at)
            SELECT t.uuid,s.artist_id,s.id,t.position,t.uuid::text,t.uuid::text,'https://example.test/'||t.uuid||'.mp3',t.duration,now()
            FROM sources s CROSS JOIN unnest(@uuids::uuid[],@positions::int[],@durations::int[]) t(uuid,position,duration)
            WHERE s.uuid=@source;
            """,
            new
            {
                showId,
                year,
                source,
                uuids = tracks.Select(track => track.Uuid).ToArray(),
                positions = tracks.Select(track => track.Position).ToArray(),
                durations = tracks.Select(track => track.Duration).ToArray(),
            }
        );

    private Task DeleteShowWithPlays(Guid showId, Guid[] tracks) =>
        connection.ExecuteAsync(
            """
            DELETE FROM source_track_plays WHERE source_track_uuid=ANY(@tracks);
            DELETE FROM shows WHERE uuid=@showId;
            """,
            new { showId, tracks }
        );

    private async Task<YearlyPopularityTotal?> YearlyShowTotal(Guid showId, DateOnly through)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        var totals = await PlayRollupReader.ReadPopularity(
            connection,
            transaction,
            new(PopularityPopulationKind.Show, artist),
            through
        );
        return totals.SingleOrDefault(total => total.Id == showId);
    }

    private Task RefreshRollup(string relation, string from, string to) =>
        connection.ExecuteAsync(
            "CALL refresh_continuous_aggregate(@relation::regclass, @from::timestamptz, @to::timestamptz, force => true)",
            new
            {
                relation,
                from,
                to,
            }
        );

    [Test]
    public async Task FullAndOnDemandRefreshKeepYearlyOnlyPlaysWithoutScoringThem()
    {
        var oldShow = Guid.NewGuid();
        var oldTrack = Guid.NewGuid();
        var oldYear = Guid.NewGuid();
        var recentPlayAt = DateTime.UtcNow.AddDays(-2);
        var options = RedisService.BuildConfiguration("redis://127.0.0.1:16379");
        options.DefaultDatabase = 14;
        var redis = new RedisService(options);
        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO years(uuid,artist_id,year,updated_at)
                SELECT @oldYear,id,'1983',now() FROM artists WHERE uuid=@artist;
                INSERT INTO shows(uuid,artist_id,year_id,date,display_date,updated_at)
                SELECT @oldShow,artist_id,id,'1983-07-07','1983-07-07',now() FROM years WHERE uuid=@oldYear;
                INSERT INTO sources(uuid,artist_id,show_id,upstream_identifier,display_date,is_soundboard,is_remaster,has_jamcharts,updated_at)
                SELECT @oldShow,artist_id,id,@oldShow::text,'1983-07-07',false,false,false,now() FROM shows WHERE uuid=@oldShow;
                INSERT INTO source_tracks(uuid,artist_id,source_id,track_position,title,slug,mp3_url,duration,updated_at)
                SELECT @oldTrack,artist_id,id,1,'Old','old','https://example.test/old.mp3',240,now() FROM sources WHERE uuid=@oldShow;
                INSERT INTO source_track_plays(created_at,source_track_uuid,app_type)
                VALUES (now()-interval '60 days',@oldTrack,0),(@recentPlayAt,@track,0);
                """,
                new
                {
                    oldYear,
                    artist,
                    oldShow,
                    oldTrack,
                    track,
                    recentPlayAt,
                }
            );
            await connection.ExecuteAsync(
                "CALL refresh_continuous_aggregate('source_track_plays_daily',NULL,NULL)"
            );
            await connection.ExecuteAsync(
                "CALL refresh_continuous_aggregate('source_track_plays_monthly',NULL,NULL)"
            );

            var cache = new PopularityCacheService(redis);
            var jobs = System.Reflection.DispatchProxy.Create<IBackgroundJobClient, NoRefresh>();
            var service = new PopularityService(
                db,
                cache,
                jobs,
                NullLogger<PopularityService>.Instance
            );
            await service.RefreshReadModel();
            await AssertYearlyRowsAsync(oldShow, oldYear);

            // Clear the annual values before the stale-read batch to prove that it
            // recalculates the window instead of preserving the full refresh's result.
            await connection.ExecuteAsync(
                """
                UPDATE show_popularity SET plays_1y=NULL WHERE uuid=@oldShow;
                UPDATE year_popularity SET plays_1y=NULL WHERE uuid=@oldYear;
                """,
                new { oldShow, oldYear }
            );
            await service.RefreshPopulations([
                new(PopularityPopulationKind.Artist),
                new(PopularityPopulationKind.Show),
                new(PopularityPopulationKind.Show, artist),
                new(PopularityPopulationKind.Year),
                new(PopularityPopulationKind.Year, artist),
            ]);

            await AssertYearlyRowsAsync(oldShow, oldYear);

            var active = await connection.QuerySingleAsync<StoredPopularity>(
                "SELECT * FROM show_popularity WHERE uuid=@show AND NOT is_global",
                new { show }
            );
            active.momentum_score.Should().Be(1);
            var artistPlays = await connection.QuerySingleAsync<long>(
                "SELECT plays_1y FROM artist_popularity WHERE uuid=@artist",
                new { artist }
            );
            artistPlays.Should().BeGreaterThanOrEqualTo(2);

            var legacy = (
                await cache.GetAsync<Dictionary<Guid, PopularityMetrics>>(
                    "popularity:shows:map:30d-48h"
                )
            )
                .Entry!
                .data;
            legacy.Should().NotContainKey(oldShow);
            Newtonsoft.Json.Linq.JObject.FromObject(legacy[show])["windows"]!
                ["1y"]
                .Should()
                .BeNull();
        }
        finally
        {
            await connection.ExecuteAsync(
                """
                DELETE FROM source_track_plays
                WHERE source_track_uuid=@oldTrack OR (source_track_uuid=@track AND created_at=@recentPlayAt);
                DELETE FROM shows WHERE uuid=@oldShow;
                DELETE FROM years WHERE uuid=@oldYear;
                """,
                new
                {
                    oldTrack,
                    oldShow,
                    oldYear,
                    track,
                    recentPlayAt,
                }
            );
            await connection.ExecuteAsync(
                "CALL refresh_continuous_aggregate('source_track_plays_daily',NULL,NULL)"
            );
            await connection.ExecuteAsync(
                "CALL refresh_continuous_aggregate('source_track_plays_monthly',NULL,NULL)"
            );
            redis.connection.Dispose();
        }
    }

    private async Task AssertYearlyRowsAsync(Guid showId, Guid yearId)
    {
        foreach (
            var (table, id) in new[] { ("show_popularity", showId), ("year_popularity", yearId) }
        )
        {
            var rows = (
                await connection.QueryAsync<StoredPopularity>(
                    $"SELECT * FROM {table} WHERE uuid=@id",
                    new { id }
                )
            ).ToArray();
            rows.Should().HaveCount(2);
            foreach (var row in rows)
            {
                row.plays_1y.Should().Be(1);
                row.hours_1y.Should().Be(0.0667);
                row.hot_score_1y.Should().Be(1);
                row.plays_30d.Should().Be(0);
                row.momentum_score.Should().BeNull();
                row.trend_ratio.Should().Be(0);
            }
        }
    }

    public class NoRefresh : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) =>
            throw new InvalidOperationException(
                "An explicit refresh must not enqueue another refresh."
            );
    }
}
