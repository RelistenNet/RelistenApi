using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Relisten.Identifiers;
using Relisten.Services.Popularity;
using RelistenApiTests.Popularity;
using StackExchange.Redis;

namespace RelistenApiTests.V4;

public sealed partial class TestV4CatalogHttp
{
    private const string ReviewServiceKey = "synthetic-catalog-review-key-for-tests-only";

    private static string McpArtistPath(string suffix) =>
        "/api/v4/artists/"
        + UuidTypeId.Format("artist", Guid.Parse("00000000-0000-0000-0000-000000000014"))
        + suffix;

    [Test]
    public async Task Mcp_performance_page_groups_by_show_and_song_and_reports_a_segue()
    {
        var path = McpArtistPath("/performances");
        var first = await GetJson(path + "?years=1995&limit=1");
        var row = first["items"]![0]!;
        row["show_id"]!.GetValue<string>().Should().Be(ShowId(4));
        row["track_ids"]!
            .AsArray()
            .Select(track => track!.GetValue<string>())
            .Should()
            .Equal(TrackId(4));
        var recording = row["representative"]!;
        recording["track_id"]!.GetValue<string>().Should().Be(TrackId(4));
        recording["file_duration_seconds"]!.GetValue<int>().Should().Be(600);
        recording["link"]!
            .GetValue<string>()
            .Should()
            .Be(
                "https://relisten.net/v4-review-4/1995/12/31?source=00000000-0000-0000-0000-000000000024"
            );

        var second = await GetJson(
            path + "?years=1995&limit=1&cursor=" + first["next_cursor"]!.GetValue<string>()
        );
        second["items"]![0]!["song_id"]!
            .GetValue<string>()
            .Should()
            .NotBe(row["song_id"]!.GetValue<string>());
        second["next_cursor"].Should().BeNull();

        var segue = await GetJson(
            path
                + "?song_id=song_00000004&segued_from=song_00000003&soundboard_only=true&min_minutes=9"
        );
        var match = segue["items"]!.AsArray().Should().ContainSingle().Subject!;
        match["representative"]!["previous"]!["id"]!
            .GetValue<string>()
            .Should()
            .Be("song_00000003");
        match["representative"]!["previous"]!["segue"]!.GetValue<bool>().Should().BeTrue();
        var excluded = await GetJson(path + "?exclude=" + TrackId(4));
        excluded["items"]!.AsArray().Should().BeEmpty();
    }

    [Test]
    public async Task Mcp_date_page_passes_shows_without_playable_matches_and_keeps_its_song_cursor()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        var imported = (
            await connection.QueryAsync<Guid>(
                """
                INSERT INTO shows (uuid, artist_id, date, display_date, created_at, updated_at)
                SELECT gen_random_uuid(), artist_id, date + n, (date + n)::text, now(), now()
                FROM shows CROSS JOIN generate_series(1, 17) n
                WHERE uuid = '00000000-0000-0000-0000-000000000004'
                RETURNING uuid
                """
            )
        ).ToArray();
        try
        {
            var path = McpArtistPath("/performances?limit=1");
            var first = await GetJson(path);
            first["items"]![0]!["show_id"]!.GetValue<string>().Should().Be(ShowId(4));
            var second = await GetJson(
                path + "&cursor=" + first["next_cursor"]!.GetValue<string>()
            );
            second["items"]![0]!["show_id"]!.GetValue<string>().Should().Be(ShowId(4));
            second["items"]![0]!["song_id"]!
                .GetValue<string>()
                .Should()
                .NotBe(first["items"]![0]!["song_id"]!.GetValue<string>());
        }
        finally
        {
            await connection.ExecuteAsync(
                "DELETE FROM shows WHERE uuid=ANY(@imported)",
                new { imported }
            );
        }
    }

    [Test]
    public async Task Mcp_performance_timeout_suggests_a_narrower_query()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            "LOCK TABLE norm.track_songs IN ACCESS EXCLUSIVE MODE",
            transaction: transaction
        );

        using var response = await http.GetAsync(McpArtistPath("/performances"));
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        problem["code"]!.GetValue<string>().Should().Be("performance_query_timeout");
        problem["title"]!.GetValue<string>().Should().Contain("Narrow");
    }

    [Test]
    public async Task Mcp_review_search_keeps_literals_literal_and_ignores_caller_patterns()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        var reviewId = Guid.NewGuid();
        var decoyId = Guid.NewGuid();
        var literal = "Rare [marker]!%_ Scarlet Begonias";
        var decoy = "Rare [marker]!XY Scarlet Begonias";
        await connection.ExecuteAsync(
            """
            INSERT INTO source_reviews (uuid, source_id, rating, review, updated_at)
            VALUES (@reviewId, 910004, 9, @literal, now()),
                   (@decoyId, 910004, 9, @decoy, now())
            """,
            new
            {
                reviewId,
                literal,
                decoyId,
                decoy,
            }
        );
        try
        {
            var path = McpArtistPath("/reviews/search");
            var query = Uri.EscapeDataString("[MARKER]!%_");
            var found = (await GetJson(path + "?q=" + query + "&years=1995"))
                .AsArray()
                .Should()
                .ContainSingle()
                .Subject!;
            found["excerpt"]!.GetValue<string>().Should().Contain(literal);
            found["show_id"]!.GetValue<string>().Should().Be(ShowId(4));
            found["track_ids"]!
                .AsArray()
                .Select(track => track!.GetValue<string>())
                .Should()
                .Contain(TrackId(4));

            var bySong = (
                await GetJson(path + "?song_id=song_00000003&show_id=" + ShowId(4))
            ).AsArray();
            bySong
                .Select(review => review!["excerpt"]!.GetValue<string>())
                .Should()
                .Contain(literal);
            // The route takes no caller-supplied regular expression. One that matches
            // nothing must not narrow the results.
            var ignoredPattern = (
                await GetJson(path + "?pattern=matches-no-review&show_id=" + ShowId(4))
            ).AsArray();
            ignoredPattern
                .Select(review => review!["excerpt"]!.GetValue<string>())
                .Should()
                .Contain(literal);
            var noMatches = (await GetJson(path + "?q=" + query + "&years=1994")).AsArray();
            noMatches.Should().BeEmpty();
        }
        finally
        {
            await connection.ExecuteAsync(
                "DELETE FROM source_reviews WHERE uuid IN (@reviewId,@decoyId)",
                new { reviewId, decoyId }
            );
        }
    }

    [Test]
    public async Task Mcp_review_search_times_out_with_narrow_search_advice()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            "LOCK TABLE source_reviews IN ACCESS EXCLUSIVE MODE",
            transaction: transaction
        );

        using var response = await http.GetAsync(McpArtistPath("/reviews/search?q=Scarlet"));
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        problem["code"]!.GetValue<string>().Should().Be("review_search_timeout");
        problem["title"]!.GetValue<string>().Should().Contain("Narrow");
    }

    [Test]
    public async Task Mcp_review_search_limits_the_forwarded_client_independently_of_catalog_reads()
    {
        async Task<HttpStatusCode> Status(string client, string? serviceKey = null)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                McpArtistPath("/reviews/search?limit=1")
            );
            request.Headers.Add("X-Forwarded-For", client);
            if (serviceKey is not null)
            {
                request.Headers.Add("X-Relisten-Mcp-Review-Key", serviceKey);
            }
            using var response = await http.SendAsync(request);
            return response.StatusCode;
        }

        for (var count = 0; count < 10; count++)
        {
            (await Status("192.0.2.1")).Should().Be(HttpStatusCode.OK);
        }
        (await Status("192.0.2.1")).Should().Be(HttpStatusCode.TooManyRequests);
        (await Status("192.0.2.2")).Should().Be(HttpStatusCode.OK);
        (await Status("192.0.2.1", "invalid-test-key")).Should().Be(HttpStatusCode.Forbidden);
        (await Status("192.0.2.1", ReviewServiceKey)).Should().Be(HttpStatusCode.OK);
        (await Status("192.0.2.1")).Should().Be(HttpStatusCode.TooManyRequests);
        (await GetJson(McpArtistPath("/years"))).AsArray().Should().NotBeEmpty();
    }

    [Test]
    public async Task AShowWithoutAPopularityPublicationHasNullPlaybackWindows()
    {
        var artist = UuidTypeId.Format(
            "artist",
            Guid.Parse("00000000-0000-0000-0000-000000000014")
        );
        var list = await GetJson($"/api/v4/artists/{artist}/performances?song_id=song_00000004");
        var show = list["items"]!.AsArray().Single()!;
        show.AsObject().ContainsKey("popularity").Should().BeTrue();
        show["popularity"].Should().BeNull();
        show["representative"]!["popularity"].Should().BeNull();

        var song = await GetJson("/api/v4/songs/song_00000004");
        var appearance = song["shows"]!.AsArray().Single()!;
        appearance.AsObject().ContainsKey("song_popularity").Should().BeTrue();
        appearance["song_popularity"].Should().BeNull();
    }

    [Test]
    public async Task BatchedPopularityPublishesTheSameTotalsIncludingSoundchecks()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        var firstTrack = Guid.Parse("00000000-0000-0000-0000-000000000341");
        var secondTrack = Guid.Parse("00000000-0000-0000-0000-000000000342");
        var otherShowTrack = Guid.Parse("00000000-0000-0000-0000-000000000343");
        var originalTitle = await connection.QuerySingleAsync<string>(
            "SELECT title FROM source_tracks WHERE uuid=@secondTrack",
            new { secondTrack }
        );
        var through = DateTime.UtcNow.Date;
        var completeMonth = new DateTime(
            through.Year,
            through.Month,
            1,
            0,
            0,
            0,
            DateTimeKind.Utc
        ).AddMonths(-2);
        var firstPlay = completeMonth.AddDays(3);
        var secondPlay = completeMonth.AddDays(8);
        var end = completeMonth.AddMonths(1);
        var recentDay = through.AddDays(-1);
        var recentPlay = recentDay.AddHours(12);
        var recentEnd = recentDay.AddDays(1);

        async Task RefreshRollups()
        {
            foreach (
                var aggregate in new[] { "source_track_plays_daily", "source_track_plays_monthly" }
            )
            {
                await connection.ExecuteAsync(
                    "CALL refresh_continuous_aggregate(@aggregate::regclass,@start::timestamptz,@end::timestamptz,force => true)",
                    new
                    {
                        aggregate,
                        start = completeMonth,
                        end,
                    }
                );
            }
        }

        async Task RefreshRecent()
        {
            foreach (
                var aggregate in new[] { "source_track_plays_hourly", "source_track_plays_daily" }
            )
            {
                await connection.ExecuteAsync(
                    "CALL refresh_continuous_aggregate(@aggregate::regclass,@start::timestamptz,@end::timestamptz,force => true)",
                    new
                    {
                        aggregate,
                        start = DateTime.SpecifyKind(recentDay, DateTimeKind.Utc),
                        end = DateTime.SpecifyKind(recentEnd, DateTimeKind.Utc),
                    }
                );
            }
        }

        var artistUuid = Guid.Parse("00000000-0000-0000-0000-000000000301");
        async Task RefreshCatalogPopularity(int showBatchSize)
        {
            var catalog = new Relisten.DbService(
                LocalTestDatabase.DatabaseUrl,
                null,
                null,
                new HostingEnvironment { EnvironmentName = "Production" },
                NullLogger<Relisten.DbService>.Instance
            );
            var redis = new Relisten.RedisService(ConfigurationOptions.Parse("127.0.0.1:16379"));
            try
            {
                var cache = new PopularityCacheService(redis);
                var service = new PopularityService(
                    catalog,
                    cache,
                    new RecordingPopularityJobs(),
                    NullLogger<PopularityService>.Instance
                );
                await service.RefreshDetailPopulations(
                    artistUuid,
                    CancellationToken.None,
                    showBatchSize
                );
            }
            finally
            {
                await redis.connection.DisposeAsync();
            }
        }

        Task ClearCatalogPublication() =>
            connection.ExecuteAsync(
                """
                DELETE FROM song_performance_popularity WHERE artist_uuid=@artistUuid;
                DELETE FROM recording_popularity WHERE artist_uuid=@artistUuid;
                DELETE FROM song_popularity WHERE artist_uuid=@artistUuid;
                DELETE FROM popularity_detail_publications WHERE artist_uuid=@artistUuid;
                """,
                new { artistUuid }
            );

        Task<IEnumerable<string>> PublishedPopularity() =>
            connection.QueryAsync<string>(
                """
                SELECT (to_jsonb(p) - 'generated_at')::text FROM song_performance_popularity p WHERE artist_uuid=@artistUuid
                UNION ALL
                SELECT (to_jsonb(p) - 'generated_at')::text FROM recording_popularity p WHERE artist_uuid=@artistUuid
                UNION ALL
                SELECT (to_jsonb(p) - 'generated_at')::text FROM song_popularity p WHERE artist_uuid=@artistUuid
                """,
                new { artistUuid }
            );

        var previousShow = await connection.QuerySingleAsync<long>(
            "SELECT s.show_id FROM source_tracks t JOIN sources s ON s.id=t.source_id WHERE t.uuid=@otherShowTrack",
            new { otherShowTrack }
        );
        var correctedShow = await connection.QuerySingleAsync<long>(
            "SELECT id FROM shows WHERE uuid='00000000-0000-0000-0000-000000000313'"
        );
        Task MoveRecording(long showId) =>
            connection.ExecuteAsync(
                "UPDATE sources SET show_id=@showId WHERE id=(SELECT source_id FROM source_tracks WHERE uuid=@otherShowTrack)",
                new { showId, otherShowTrack }
            );

        await connection.ExecuteAsync(
            """
            INSERT INTO source_track_plays(created_at, source_track_uuid, app_type)
            VALUES (@firstPlay, @firstTrack, 0), (@firstPlay, @secondTrack, 0), (@secondPlay, @firstTrack, 0), (@recentPlay, @firstTrack, 0),
                   (@firstPlay, @otherShowTrack, 0), (@recentPlay, @otherShowTrack, 0)
            """,
            new
            {
                firstPlay,
                secondPlay,
                recentPlay = DateTime.SpecifyKind(recentPlay, DateTimeKind.Utc),
                firstTrack,
                secondTrack,
                otherShowTrack,
            }
        );
        try
        {
            await connection.ExecuteAsync(
                "UPDATE source_tracks SET title='(soundcheck) ' || @originalTitle WHERE uuid=@secondTrack",
                new { secondTrack, originalTitle }
            );
            await RefreshRollups();
            await RefreshRecent();
            // A corrected show date leaves the completed month's rollup on
            // the former show until Timescale rematerializes it.
            await MoveRecording(correctedShow);
            await RefreshCatalogPopularity(int.MaxValue);
            var oneBatch = await PublishedPopularity();
            await ClearCatalogPublication();

            await RefreshCatalogPopularity(1);
            (await PublishedPopularity()).Should().BeEquivalentTo(oneBatch);
            (
                await connection.QuerySingleAsync<long>(
                    "SELECT plays_1y FROM song_popularity WHERE song_id='song_00000101'"
                )
            )
                .Should()
                .Be(6);
            (
                await connection.QuerySingleAsync<long>(
                    """
                    SELECT p.plays_1y FROM recording_popularity p
                    JOIN sources s ON s.uuid=p.uuid
                    JOIN source_tracks t ON t.source_id=s.id WHERE t.uuid=@otherShowTrack
                    """,
                    new { otherShowTrack }
                )
            ).Should().Be(2);

            var artist = UuidTypeId.Format("artist", artistUuid);
            var page = await GetJson(
                $"/api/v4/artists/{artist}/performances?song_id=song_00000101"
            );
            var row = page["items"]!
                .AsArray()
                .Single(item => item!["eligible_source_count"]!.GetValue<int>() == 2)!;
            row["popularity"]!["windows"]!["1y"]!["plays"]!.GetValue<int>().Should().Be(4);
            row["representative"]!["popularity"]!["windows"]!["1y"]!["plays"]!
                .GetValue<int>()
                .Should()
                .Be(3);
            var song = await GetJson("/api/v4/songs/song_00000101");
            var songShow = song["shows"]!
                .AsArray()
                .Single(show => show!["show"]!["id"]!.ToString() == row["show_id"]!.ToString())!;
            var windows = songShow["song_popularity"]!["windows"]!;
            windows["1y"]!["plays"]!.GetValue<int>().Should().Be(4);
            foreach (var window in new[] { "48h", "7d", "30d" })
            {
                windows[window]!["plays"]!.GetValue<int>().Should().Be(1);
            }
        }
        finally
        {
            await MoveRecording(previousShow);
            await connection.ExecuteAsync(
                "UPDATE source_tracks SET title=@originalTitle WHERE uuid=@secondTrack",
                new { secondTrack, originalTitle }
            );
            await connection.ExecuteAsync(
                "DELETE FROM source_track_plays WHERE source_track_uuid IN (@firstTrack,@secondTrack,@otherShowTrack) AND created_at IN (@firstPlay,@secondPlay,@recentPlay)",
                new
                {
                    firstTrack,
                    secondTrack,
                    otherShowTrack,
                    firstPlay,
                    secondPlay,
                    recentPlay = DateTime.SpecifyKind(recentPlay, DateTimeKind.Utc),
                }
            );
            await RefreshRollups();
            await RefreshRecent();
            await ClearCatalogPublication();
        }
    }
}
