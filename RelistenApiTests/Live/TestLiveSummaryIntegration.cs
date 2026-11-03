using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Relisten;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;
using Relisten.Data;
using Relisten.Services.Live;
using StackExchange.Redis;

namespace RelistenApiTests.Live;

/// <summary>
/// Runs the live summary query, ranking and source lookup against the disposable local
/// catalog. Plays use a fixed clock in March 2025; no other test writes plays, so the
/// window contains only the plays seeded here.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public class TestLiveSummaryIntegration
{
    // A Saturday. The window is [18:15, 18:30).
    private static readonly DateTime Now = new(2025, 3, 15, 18, 30, 20, DateTimeKind.Utc);
    private static readonly LiveWindow Window = LiveWindow.EndingBefore(Now, 15);

    private DbService db = null!;
    private LiveSummaryService service = null!;
    private string? previousWrite;
    private string? previousRead;

    // Big Band plays at its usual level for this hour of the week; Small Band normally
    // has no plays at this hour and is spiking.
    private int bigBandId;
    private int smallBandId;
    private readonly Dictionary<string, CatalogShow> shows = new();
    private long lastValidPlayId;

    private sealed record CatalogShow(
        string Key,
        int ArtistId,
        Guid ShowUuid,
        long[] SourceIds,
        Guid[][] TrackUuids
    );

    [OneTimeSetUp]
    public async Task SeedCatalogAndPlays()
    {
        MigratedTestDatabase.RequireLocalDatabase();

        // Startup registers these; SlimSourceWithShowAndArtistForIds needs them.
        SqlMapper.AddTypeHandler(new PersistentIdentifierHandler());
        SqlMapper.AddTypeHandler(new DateTimeHandler());
        previousWrite = DbService.ConnStr;
        previousRead = DbService.ReadOnlyConnStr;
        db = new DbService(
            LocalTestDatabase.DatabaseUrl,
            null,
            null,
            Host.CreateApplicationBuilder().Environment,
            NullLogger<DbService>.Instance
        );
        service = NewService(redis: null!);

        bigBandId = await InsertArtist("Big Band");
        smallBandId = await InsertArtist("Small Band");

        // A1 has two sources so the representative-source tie-break can be checked.
        await InsertShow("A1", bigBandId, "1977-05-08", "1977-05-08", sources: 2);
        await InsertShow("A2", bigBandId, "1978-01-01", "1978-01-01");
        await InsertShow("A3", bigBandId, "1979-01-01", "1979-01-01");
        await InsertShow("A4", bigBandId, "1980-01-01", "1980-01-01");
        await InsertShow("A5", bigBandId, "1981-01-01", "1981-01-01");
        await InsertShow("B1", smallBandId, "2025-03-12", "2025-03-12"); // recent concert
        await InsertShow("B2", smallBandId, "1980-03-15", "1980-03-15"); // on this day
        await InsertShow("B3", smallBandId, "1980-03-XX", "1980-03-01"); // partial date

        await SeedWindowPlays();
        await SeedBaselines();
    }

    [OneTimeTearDown]
    public void RestoreConnectionStrings()
    {
        if (previousWrite != null)
        {
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead!;
        }
    }

    [Test]
    public async Task Totals_CountOnlyResolvablePlaysInsideTheWindow()
    {
        var summary = await Summarize();

        summary.total_plays.Should().Be(20);
        summary.last_play_id.Should().Be(lastValidPlayId);
        summary.window_start.Should().Be(Window.Start);
        summary.window_end.Should().Be(Window.End);
        summary
            .app_types.Select(a => (a.app_type, a.plays))
            .Should()
            .Equal(
                (SourceTrackPlayAppType.iOS, 10L),
                (SourceTrackPlayAppType.Web, 6L),
                (SourceTrackPlayAppType.Android, 3L),
                (SourceTrackPlayAppType.Sonos, 1L)
            );
    }

    [Test]
    public async Task Timeline_HasOneZeroFilledBucketPerMinute()
    {
        var summary = await Summarize();

        summary.timeline.Should().HaveCount(15);
        summary
            .timeline.Select(b => b.bucket_start)
            .Should()
            .Equal(Enumerable.Range(0, 15).Select(i => Window.Start.AddMinutes(i)));
        summary.timeline.Sum(b => b.plays).Should().Be(summary.total_plays);
        summary.timeline.Should().Contain(b => b.plays == 0);

        var a1 = summary.shows.Single(s => s.show_uuid == shows["A1"].ShowUuid);
        a1.timeline.Should().HaveCount(15);
        a1.timeline.Sum().Should().Be((int)a1.plays);
    }

    [Test]
    public async Task Shows_GroupSourcesAndPickTheMostPlayedThenMostRecentSource()
    {
        var summary = await Summarize();
        var a1 = summary.shows.Single(s => s.show_uuid == shows["A1"].ShowUuid);

        a1.plays.Should().Be(4);
        a1.source_count.Should().Be(2);
        // Both sources have two plays; the second was played more recently.
        a1.source.id.Should().Be((int)shows["A1"].SourceIds[1]);
        a1.source.show!.uuid.Should().Be(shows["A1"].ShowUuid);
        a1.source.artist!.features.Should().NotBeNull();
        a1.first_played_at.Should().Be(At(16));
        a1.last_played_at.Should().Be(At(25));
        a1.latest_track.uuid.Should().Be(shows["A1"].TrackUuids[1][1]);
        a1.latest_track.played_at.Should().Be(At(25));
        a1.plays_30d.Should().Be(48);
    }

    [Test]
    public async Task Artists_ReportPlaysShowCountsAndShares()
    {
        var summary = await Summarize(sort: "plays", artistsLimit: 1);

        summary.artist_count.Should().Be(2);
        var bigBand = summary.artists.Single();
        bigBand.name.Should().Be("Big Band");
        bigBand.plays.Should().Be(12);
        bigBand.show_count.Should().Be(5);
        bigBand.share.Should().Be(0.6);
        summary.other_artists_plays.Should().Be(8);
        (summary.artists.Sum(a => a.plays) + summary.other_artists_plays)
            .Should()
            .Be(summary.total_plays);
    }

    [Test]
    public async Task SurpriseRanking_PutsASpikingSmallArtistAboveABigArtistAtItsUsualLevel()
    {
        var bySurprise = await Summarize(sort: "surprise");
        var byPlays = await Summarize(sort: "plays");

        bySurprise.artists.Select(a => a.name).Should().Equal("Small Band", "Big Band");
        byPlays.artists.Select(a => a.name).Should().Equal("Big Band", "Small Band");

        var bigBand = bySurprise.artists.Single(a => a.name == "Big Band");
        bigBand.expected_plays.Should().Be(12); // 48 plays in the 18:00 hour, a quarter overlapping.
        bigBand.surprise.Should().Be(1);
        var smallBand = bySurprise.artists.Single(a => a.name == "Small Band");
        smallBand.expected_plays.Should().Be(0);
        smallBand.surprise.Should().BeApproximately((8 + 3) / 3.0, 0.001);
    }

    [Test]
    public async Task Shows_AreRankedBySurpriseAndCappedPerArtist()
    {
        var capped = await Summarize(sort: "surprise");
        var uncapped = await Summarize(sort: "surprise", maxPerArtist: 0);

        ShowKeys(capped.shows).Should().Equal("A1", "B1", "B2", "A2");
        ShowKeys(uncapped.shows).Should().Equal("A1", "B1", "B2", "A2", "B3", "A4", "A3");
    }

    [Test]
    public async Task Shows_ByPlaysBreakTiesByMostRecentPlay()
    {
        var summary = await Summarize(sort: "plays", maxPerArtist: 0);

        ShowKeys(summary.shows).Should().Equal("A1", "B2", "B1", "A2", "A4", "A3", "B3");
    }

    [Test]
    public async Task SinglePlayShows_AreLeftOutOfEveryShowList()
    {
        var summary = await Summarize(maxPerArtist: 0);

        ShowKeys(summary.shows).Should().NotContain("A5");
        ShowKeys(summary.deep_cuts).Should().NotContain("A5");
        summary.artists.Single(a => a.name == "Big Band").plays.Should().Be(12);
    }

    [Test]
    public async Task DeepCuts_IncludeShowsAtOrBelowTheThirtyDayThreshold()
    {
        var summary = await Summarize();

        ShowKeys(summary.deep_cuts).Should().Equal("B1", "B3");
        summary
            .deep_cuts.Single(s => s.show_uuid == shows["B3"].ShowUuid)
            .plays_30d.Should()
            .Be(25);
    }

    [Test]
    public async Task Fresh_IncludesRecentConcerts()
    {
        var week = await Summarize(freshDays: 7);
        var oneDay = await Summarize(freshDays: 1);

        ShowKeys(week.fresh).Should().Equal("B1");
        week.fresh.Single().reason.Should().Be(LiveSummaryFreshReason.RecentShow);
        oneDay.fresh.Should().BeEmpty();
    }

    [Test]
    public async Task OnThisDay_MatchesFullDatesOnly()
    {
        var march15 = await Summarize(monthDay: "03-15");
        var march1 = await Summarize(monthDay: "03-01");

        march15.on_this_day.month_day.Should().Be("03-15");
        march15.on_this_day.plays.Should().Be(3);
        march15.on_this_day.show_count.Should().Be(1);
        march15.on_this_day.show_uuids.Should().Equal(shows["B2"].ShowUuid);

        // B3 is 1980-03-XX; its stored date is 1980-03-01, which must not count as a match.
        march1.on_this_day.show_count.Should().Be(0);
        march1.on_this_day.show_uuids.Should().BeEmpty();
    }

    [Test]
    public async Task EmptyWindow_ReturnsZerosAndAZeroFilledTimeline()
    {
        var emptyWindow = LiveWindow.EndingBefore(Now.AddDays(-2), 15);
        var summary = await service.BuildSummary(Request(), emptyWindow, Now);

        summary.total_plays.Should().Be(0);
        summary.last_play_id.Should().BeNull();
        summary.artist_count.Should().Be(0);
        summary.other_artists_plays.Should().Be(0);
        summary.app_types.Should().BeEmpty();
        summary.shows.Should().BeEmpty();
        summary.artists.Should().BeEmpty();
        summary.deep_cuts.Should().BeEmpty();
        summary.fresh.Should().BeEmpty();
        summary.on_this_day.show_count.Should().Be(0);
        summary.timeline.Should().HaveCount(15).And.OnlyContain(b => b.plays == 0);
    }

    [Test]
    public async Task SummaryJson_IsServedFromRedisWithinTheSameRefreshInterval()
    {
        var redis = new RedisService(RedisService.BuildConfiguration("redis://127.0.0.1:16379"));
        var request = Request();
        var cacheKey = request.CacheKey(Window);
        try
        {
            var cached = NewService(redis);
            await redis.db.KeyDeleteAsync(cacheKey);

            var first = await cached.SummaryJson(request, Window, Now);
            await redis.db.StringSetAsync(cacheKey, "{\"from\":\"cache\"}");
            var second = await cached.SummaryJson(request, Window, Now);

            JObject.Parse(first)["total_plays"]!.Value<long>().Should().Be(20);
            JObject.Parse(first)["shows"]![0]!["source"]!["id"].Should().BeNull("v3 hides ids");
            second.Should().Be("{\"from\":\"cache\"}");
        }
        finally
        {
            await redis.db.KeyDeleteAsync(cacheKey);
            redis.connection.Dispose();
        }
    }

    [Test]
    public async Task SummaryJson_IsServedUncachedWhenRedisIsUnavailable()
    {
        // Nothing listens on port 1; every Redis call fails quickly.
        var options = ConfigurationOptions.Parse(
            "127.0.0.1:1,abortConnect=false,connectTimeout=100,asyncTimeout=100,syncTimeout=100"
        );
        var redis = new RedisService(options);
        try
        {
            var json = await NewService(redis).SummaryJson(Request(), Window, Now);

            JObject.Parse(json)["total_plays"]!.Value<long>().Should().Be(20);
        }
        finally
        {
            redis.connection.Dispose();
        }
    }

    private LiveSummaryService NewService(RedisService redis) =>
        new(
            new SourceTrackPlaysService(db),
            new SourceService(db, null!),
            redis,
            NullLogger<LiveSummaryService>.Instance
        );

    private Task<LiveSummary> Summarize(
        string? sort = null,
        int artistsLimit = LiveSummaryRequest.DefaultArtistsLimit,
        int maxPerArtist = LiveSummaryRequest.DefaultMaxPerArtist,
        int freshDays = LiveSummaryRequest.DefaultFreshDays,
        string? monthDay = null
    ) =>
        service.BuildSummary(
            Request(sort, artistsLimit, maxPerArtist, freshDays, monthDay),
            Window,
            Now
        );

    private static LiveSummaryRequest Request(
        string? sort = null,
        int artistsLimit = LiveSummaryRequest.DefaultArtistsLimit,
        int maxPerArtist = LiveSummaryRequest.DefaultMaxPerArtist,
        int freshDays = LiveSummaryRequest.DefaultFreshDays,
        string? monthDay = null
    )
    {
        LiveSummaryRequest
            .TryCreate(
                LiveSummaryRequest.DefaultMinutes,
                LiveSummaryRequest.DefaultShowsLimit,
                artistsLimit,
                monthDay,
                sort,
                maxPerArtist,
                freshDays,
                Now,
                out var request,
                out _
            )
            .Should()
            .BeTrue();
        return request;
    }

    private IEnumerable<string> ShowKeys(IEnumerable<LiveSummaryShow> listed) =>
        listed.Select(s => shows.Values.Single(c => c.ShowUuid == s.show_uuid).Key);

    private static DateTime At(int minute, int second = 0) =>
        new(2025, 3, 15, 18, minute, second, DateTimeKind.Utc);

    /// <summary>
    /// 20 resolvable plays in the window: Big Band 12 (A1 4, A2 3, A3 2, A4 2, A5 1) and
    /// Small Band 8 (B1 3, B2 3, B3 2), plus one play before the window and one whose
    /// track no longer exists. The order of the list sets play ids and app types.
    /// </summary>
    private async Task SeedWindowPlays()
    {
        var plays = new List<(string Show, int Source, int Track, DateTime At)>
        {
            ("A1", 0, 0, At(16)),
            ("A1", 1, 0, At(17)),
            ("A1", 0, 1, At(20)),
            ("A1", 1, 1, At(25)),
            ("A2", 0, 0, At(18)),
            ("A2", 0, 1, At(19)),
            ("A2", 0, 0, At(21)),
            ("A3", 0, 0, At(18, 30)),
            ("A3", 0, 1, At(22)),
            ("A4", 0, 0, At(18, 40)),
            ("A4", 0, 1, At(23)),
            ("A5", 0, 0, At(24)),
            ("B1", 0, 0, At(15)),
            ("B1", 0, 1, At(16, 30)),
            ("B1", 0, 0, At(26)),
            ("B2", 0, 0, At(15, 10)),
            ("B2", 0, 1, At(20, 30)),
            ("B2", 0, 0, At(27)),
            ("B3", 0, 0, At(15, 20)),
            ("B3", 0, 1, At(21, 30)),
        };
        var appTypes = Enumerable
            .Repeat(SourceTrackPlayAppType.iOS, 10)
            .Concat(Enumerable.Repeat(SourceTrackPlayAppType.Web, 6))
            .Concat(Enumerable.Repeat(SourceTrackPlayAppType.Android, 3))
            .Append(SourceTrackPlayAppType.Sonos)
            .ToList();

        for (var i = 0; i < plays.Count; i++)
        {
            var play = plays[i];
            var id = await InsertPlay(
                shows[play.Show].TrackUuids[play.Source][play.Track],
                play.At,
                appTypes[i]
            );
            lastValidPlayId = Math.Max(lastValidPlayId, id);
        }

        await InsertPlay(shows["A1"].TrackUuids[0][0], At(10), SourceTrackPlayAppType.iOS);
        await InsertPlay(Guid.NewGuid(), At(28), SourceTrackPlayAppType.iOS);
    }

    /// <summary>
    /// Big Band: 48 plays in the 18:00 hour one to four weeks back, 12 on each of A1-A4,
    /// so its expected plays for the quarter-hour window are 12 and each of those shows
    /// has 48 plays over 30 days. Small Band has no plays at that hour of the week; B2
    /// and B3 have 26 and 25 plays on other days, either side of the deep-cut threshold.
    /// Plays seeded here are outside the window, so they reach the summary only through
    /// the aggregates.
    /// </summary>
    private async Task SeedBaselines()
    {
        foreach (var weeksAgo in Enumerable.Range(1, 4))
        {
            var hour = At(5).AddDays(-7 * weeksAgo);
            foreach (var key in new[] { "A1", "A2", "A3", "A4" })
            {
                for (var i = 0; i < 12; i++)
                {
                    await InsertPlay(shows[key].TrackUuids[0][0], hour, SourceTrackPlayAppType.Web);
                }
            }
        }

        for (var i = 0; i < 26; i++)
        {
            await InsertPlay(
                shows["B2"].TrackUuids[0][0],
                At(0).AddDays(-10),
                SourceTrackPlayAppType.Web
            );
        }

        // Five of B3's plays are from yesterday, which only the hourly aggregate covers.
        for (var i = 0; i < 25; i++)
        {
            await InsertPlay(
                shows["B3"].TrackUuids[0][0],
                At(0).AddDays(i < 20 ? -12 : -1),
                SourceTrackPlayAppType.Web
            );
        }

        // Production refreshes the hourly aggregate every hour and the daily one once a
        // day, materializing whole days only up to about two days ago. Mirror that lag.
        await db.WithWriteConnection(async connection =>
        {
            await connection.ExecuteAsync(
                "CALL refresh_continuous_aggregate('source_track_plays_hourly', @from, @to)",
                new { from = Now.AddDays(-40), to = Now.Date }
            );
            await connection.ExecuteAsync(
                "CALL refresh_continuous_aggregate('source_track_plays_daily', @from, @to)",
                new { from = Now.AddDays(-40), to = Now.Date.AddDays(-2) }
            );
        });
    }

    private Task<int> InsertArtist(string name)
    {
        var uuid = Guid.NewGuid();
        return db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<int>(
                """
                WITH artist AS (
                    INSERT INTO artists(name, slug, uuid, updated_at)
                    VALUES (@name, @slug, @uuid, now())
                    RETURNING id
                ), features AS (
                    INSERT INTO features(artist_id) SELECT id FROM artist
                )
                SELECT id FROM artist
                """,
                new
                {
                    name,
                    slug = "live-" + uuid,
                    uuid,
                }
            )
        );
    }

    private async Task InsertShow(
        string key,
        int artistId,
        string displayDate,
        string date,
        int sources = 1
    )
    {
        var showUuid = Guid.NewGuid();
        var sourceIds = new long[sources];
        var trackUuids = new Guid[sources][];

        await db.WithWriteConnection(async connection =>
        {
            var showId = await connection.QuerySingleAsync<long>(
                """
                INSERT INTO shows(artist_id, date, display_date, uuid, updated_at)
                VALUES (@artistId, @date::date, @displayDate, @showUuid, now())
                RETURNING id
                """,
                new
                {
                    artistId,
                    date,
                    displayDate,
                    showUuid,
                }
            );

            for (var s = 0; s < sources; s++)
            {
                sourceIds[s] = await connection.QuerySingleAsync<long>(
                    """
                    INSERT INTO sources(artist_id, show_id, upstream_identifier, display_date,
                        uuid, updated_at, is_soundboard, is_remaster, has_jamcharts)
                    VALUES (@artistId, @showId, @identifier, @displayDate, @uuid, now(),
                        false, false, false)
                    RETURNING id
                    """,
                    new
                    {
                        artistId,
                        showId,
                        identifier = $"live-{showUuid}-{s}",
                        displayDate,
                        uuid = Guid.NewGuid(),
                    }
                );

                trackUuids[s] = new[] { Guid.NewGuid(), Guid.NewGuid() };
                for (var t = 0; t < 2; t++)
                {
                    await connection.ExecuteAsync(
                        """
                        INSERT INTO source_tracks(source_id, artist_id, track_position, title,
                            slug, mp3_url, uuid, updated_at, duration)
                        VALUES (@sourceId, @artistId, @position, @title, @slug, @url, @uuid,
                            now(), 300)
                        """,
                        new
                        {
                            sourceId = sourceIds[s],
                            artistId,
                            position = t + 1,
                            title = $"{key} track {t + 1}",
                            slug = $"{key.ToLowerInvariant()}-track-{t + 1}",
                            url = $"https://example.invalid/{trackUuids[s][t]}.mp3",
                            uuid = trackUuids[s][t],
                        }
                    );
                }
            }
        });

        shows[key] = new CatalogShow(key, artistId, showUuid, sourceIds, trackUuids);
    }

    private Task<long> InsertPlay(Guid trackUuid, DateTime at, SourceTrackPlayAppType appType) =>
        db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<long>(
                """
                INSERT INTO source_track_plays(created_at, source_track_uuid, app_type)
                VALUES (@at, @trackUuid, @appType)
                RETURNING id
                """,
                new
                {
                    at,
                    trackUuid,
                    appType = (short)appType,
                }
            )
        );
}
