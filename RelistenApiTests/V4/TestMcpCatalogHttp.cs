using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Relisten.Identifiers;

namespace RelistenApiTests.V4;

public sealed partial class TestV4CatalogHttp
{
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
        row["longest_seconds"]!.GetValue<int>().Should().Be(600);
        row["link"]!
            .GetValue<string>()
            .Should()
            .Be(
                "https://relisten.net/v4-review-4/1995/12/31?source=00000000-0000-0000-0000-000000000024"
            );
        row.AsObject().ContainsKey("plays_1y").Should().BeFalse();

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
        match["previous"]!["id"]!.GetValue<string>().Should().Be("song_00000003");
        match["previous"]!["segue"]!.GetValue<bool>().Should().BeTrue();
        var excluded = await GetJson(path + "?exclude=" + TrackId(4));
        excluded["items"]!.AsArray().Should().BeEmpty();
    }

    [Test]
    public async Task Mcp_review_search_keeps_literals_literal_and_rejects_malformed_patterns()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        var reviewId = Guid.NewGuid();
        var literal = "Rare [marker] Scarlet Begonias";
        await connection.ExecuteAsync(
            """
            INSERT INTO source_reviews (uuid, source_id, rating, review, updated_at)
            VALUES (@reviewId, 910004, 9, @literal, now())
            """,
            new { reviewId, literal }
        );
        try
        {
            var path = McpArtistPath("/reviews/search");
            var found = (await GetJson(path + "?q=%5Bmarker%5D&years=1995"))
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
            using var backref = await http.GetAsync(path + "?pattern=%28x%29%5C1");
            backref.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            using var malformed = await http.GetAsync(path + "?pattern=%5B");
            malformed.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            JsonNode.Parse(await malformed.Content.ReadAsStringAsync())!["code"]!
                .GetValue<string>()
                .Should()
                .Be("invalid_review_pattern");
            var noMatches = (await GetJson(path + "?q=%5Bmarker%5D&years=1994")).AsArray();
            noMatches.Should().BeEmpty();
        }
        finally
        {
            await connection.ExecuteAsync(
                "DELETE FROM source_reviews WHERE uuid=@reviewId",
                new { reviewId }
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
        async Task<HttpStatusCode> Status(string client)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                McpArtistPath("/reviews/search?limit=1")
            );
            request.Headers.Add("X-Forwarded-For", client);
            using var response = await http.SendAsync(request);
            return response.StatusCode;
        }

        for (var count = 0; count < 10; count++)
        {
            (await Status("192.0.2.1")).Should().Be(HttpStatusCode.OK);
        }
        (await Status("192.0.2.1")).Should().Be(HttpStatusCode.TooManyRequests);
        (await Status("192.0.2.2")).Should().Be(HttpStatusCode.OK);
        (await GetJson(McpArtistPath("/years"))).AsArray().Should().NotBeEmpty();
    }

    [Test]
    public async Task Mcp_performance_popularity_counts_overlapping_track_days_once()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        var firstTrack = Guid.Parse("00000000-0000-0000-0000-000000000341");
        var secondTrack = Guid.Parse("00000000-0000-0000-0000-000000000342");
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

        async Task RefreshRollups()
        {
            foreach (
                var aggregate in new[]
                {
                    "source_track_plays_daily",
                    "source_track_plays_monthly",
                    "source_plays_daily",
                }
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

        await connection.ExecuteAsync(
            """
            INSERT INTO source_track_plays(created_at, source_track_uuid, app_type)
            VALUES (@firstPlay, @firstTrack, 0), (@firstPlay, @secondTrack, 0), (@secondPlay, @firstTrack, 0)
            """,
            new
            {
                firstPlay,
                secondPlay,
                firstTrack,
                secondTrack,
            }
        );
        try
        {
            await RefreshRollups();
            app.Configuration["Popularity:EnableYearlyWindow"] = "true";
            var artist = UuidTypeId.Format(
                "artist",
                Guid.Parse("00000000-0000-0000-0000-000000000301")
            );
            var page = await GetJson(
                $"/api/v4/artists/{artist}/performances?song_id=song_00000101"
            );
            var row = page["items"]!
                .AsArray()
                .Single(item => item!["source_count"]!.GetValue<int>() == 2)!;
            row["plays_1y"]!.GetValue<int>().Should().Be(3);
            row["days_with_plays"]!.GetValue<int>().Should().Be(2);
            row["source_plays_1y"]!.GetValue<int>().Should().Be(2);
            row["source_days_with_plays"]!.GetValue<int>().Should().Be(2);
        }
        finally
        {
            app.Configuration["Popularity:EnableYearlyWindow"] = null;
            await connection.ExecuteAsync(
                "DELETE FROM source_track_plays WHERE source_track_uuid IN (@firstTrack,@secondTrack) AND created_at >= @start AND created_at < @end",
                new
                {
                    firstTrack,
                    secondTrack,
                    start = completeMonth,
                    end,
                }
            );
            await RefreshRollups();
        }
    }
}
