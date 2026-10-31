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
            INSERT INTO artists(uuid,name,slug,updated_at) VALUES (@artist,'Popularity fixture',@artist::text,now());
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
        await connection.ExecuteAsync("DELETE FROM artists WHERE uuid=@artist", new { artist });
        await connection.DisposeAsync();
        DbService.ConnStr = previousPrimary;
        DbService.ReadOnlyConnStr = previousReplica;
    }

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
                "SELECT is_global FROM show_popularity WHERE uuid=@show",
                new { show }
            )
        )
            .Should()
            .Equal(true);
        await connection.ExecuteAsync("DELETE FROM show_popularity WHERE uuid=@show", new { show });
    }

    [Test]
    public async Task Stale_and_missing_populations_queue_once_while_fresh_empty_results_do_not()
    {
        await connection.ExecuteAsync(
            "DELETE FROM popularity_refreshes WHERE scope_uuid=@artist",
            new { artist }
        );
        await store.ReplaceArtistShows(
            artist,
            new Dictionary<Guid, CachedShowPopularity>(),
            DateTime.UtcNow.AddHours(-2)
        );
        var jobs = new RecordingPopularityJobs();
        var request = new PopularityRefreshRequest(db, jobs);
        request.Require(PopularityPopulationKind.Show, artist);
        request.Require(PopularityPopulationKind.Show, artist);
        request.Require(PopularityPopulationKind.Year, artist);
        request.Require(PopularityPopulationKind.Show, Guid.NewGuid());
        await request.EnqueueStale(CancellationToken.None);
        jobs.Batches.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new[]
                {
                    new PopularityPopulation(PopularityPopulationKind.Show, artist),
                    new PopularityPopulation(PopularityPopulationKind.Year, artist),
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
        await request.EnqueueStale(CancellationToken.None);
        jobs.Batches.Should().ContainSingle();
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

    public class NoRefresh : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) =>
            throw new InvalidOperationException(
                "An explicit refresh must not enqueue another refresh."
            );
    }
}
