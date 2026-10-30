using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;

namespace RelistenApiTests.V4;

public sealed partial class TestV4CatalogHttp
{
    [Test]
    public async Task Anniversary_lists_scope_artists_and_link_to_the_same_full_show()
    {
        var path = BrowseArtistPath + "/shows/on-date?month=12&day=31";
        var shows = (await GetJson(path)).AsArray();
        shows.Select(show => show!["id"]!.GetValue<string>()).Should().Equal(ShowId(4));
        var summary = shows.Single()!;
        summary["artist_venue"]!.Should().NotBeNull();
        summary.AsObject().ContainsKey("sources").Should().BeFalse();
        JsonNode
            .DeepEquals(await GetJson(BrowseArtistPath + "/shows/today?month=12&day=31"), shows)
            .Should()
            .BeTrue();

        var global = (await GetJson("/api/v4/shows/on-date?month=12&day=31")).AsArray();
        global
            .Select(show => show!["id"]!.GetValue<string>())
            .Should()
            .Contain(new[] { ShowId(1), ShowId(4) });
        global
            .Single(show => show!["id"]!.GetValue<string>() == ShowId(1))!["artist_venue"]
            .Should()
            .BeNull();

        var detail = await GetJson(BrowseArtistPath + "/shows/by-date/1995-12-31");
        JsonNode.DeepEquals(detail, await GetJson(ShowPath(4))).Should().BeTrue();
        detail["sources"]![0]!["sets"]![0]!["tracks"]![0]!["normalization"]!["state"]!
            .GetValue<string>()
            .Should()
            .Be("interpreted");
        using var response = await http.GetAsync(path);
        using var unchanged = await Conditional(path, response.Headers.ETag!.ToString());
        unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await GetJson(BrowseArtistPath + "/shows/on-date?month=2&day=29"))
            .AsArray()
            .Should()
            .BeEmpty();
    }

    [Test]
    public async Task Today_defaults_to_the_database_date_and_exact_lookup_keeps_partial_labels()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        var date = await connection.QuerySingleAsync<DateTime>("SELECT CURRENT_DATE::timestamp");
        JsonNode
            .DeepEquals(
                await GetJson("/api/v4/shows/today"),
                await GetJson($"/api/v4/shows/on-date?month={date.Month}&day={date.Day}")
            )
            .Should()
            .BeTrue();

        try
        {
            await connection.ExecuteAsync(
                "UPDATE shows SET display_date='1995-12-xx' WHERE id=910004"
            );
            var detail = await GetJson(BrowseArtistPath + "/shows/by-date/1995-12-xx");
            detail["id"]!.GetValue<string>().Should().Be(ShowId(4));
            detail["display_date"]!.GetValue<string>().Should().Be("1995-12-xx");
        }
        finally
        {
            await connection.ExecuteAsync(
                "UPDATE shows SET display_date='1995-12-31' WHERE id=910004"
            );
        }
    }

    [Test]
    public async Task Recent_lists_keep_imported_windows_source_order_and_rating_rank_separate()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                """
                UPDATE shows SET date=CURRENT_DATE-1,display_date=(CURRENT_DATE-1)::text,
                    updated_at=CURRENT_DATE-1 WHERE id=910004;
                UPDATE shows SET date=CURRENT_DATE-100,display_date=(CURRENT_DATE-100)::text,
                    updated_at=CURRENT_DATE-2 WHERE id=910010;
                -- The read contract consumes stored source aggregates, refreshed by ingestion.
                INSERT INTO show_source_information (show_id,artist_id,max_created_at,max_updated_at,source_count,max_avg_rating_weighted)
                VALUES (910004,910004,CURRENT_DATE-4,CURRENT_DATE-1,1,2),
                       (910010,910004,CURRENT_DATE-1,CURRENT_DATE-4,1,5);
                """
            );

            var recent = BrowseArtistPath + "/shows/";
            async Task<string[]> Ids(string path) =>
                (await GetJson(path))
                    .AsArray()
                    .Select(show => show!["id"]!.GetValue<string>())
                    .ToArray();
            (await Ids(recent + "recently-performed?shows=1")).Should().Equal(ShowId(4));
            (await Ids(recent + "recently-performed?days=999&shows=1")).Should().Equal(ShowId(4)); // Day window caps at 90.
            (await Ids(recent + "recently-updated?days=3&shows=1"))
                .Should()
                .Equal(ShowId(4), ShowId(10)); // Window overrides limit.
            (await Ids($"/api/v4/shows/recently-updated?artist_ids={BrowseArtistId}&shows=1"))
                .Should()
                .Equal(ShowId(4));
            var multipleArtists = (
                await GetJson(
                    $"/api/v4/shows/recently-updated?artist_ids={BrowseArtistId}&artist_ids={CatalogId("artist", 11)}"
                )
            ).AsArray();
            multipleArtists
                .Select(show => show!["artist_id"]!.GetValue<string>())
                .Distinct()
                .Should()
                .BeEquivalentTo(BrowseArtistId, CatalogId("artist", 11));
            (await Ids(recent + "recently-added?limit=1")).Should().Equal(ShowId(10));
            (await Ids(recent + "recently-added?sort_by=updated_at&limit=1"))
                .Should()
                .Equal(ShowId(4));
            (await Ids(recent + "recently-added?previous_days=2")).Should().Equal(ShowId(10));
            (await Ids(recent + "recently-added?previous_days=2147483647"))
                .Should()
                .Equal(ShowId(10), ShowId(4));
            (await Ids(recent + "top?limit=1")).Should().Equal(ShowId(10));

            // Equal ranking timestamps still produce stable UUID order and ETags.
            await connection.ExecuteAsync(
                "UPDATE shows SET updated_at=CURRENT_DATE-1 WHERE id=910010"
            );
            var tied = recent + "recently-updated";
            (await Ids(tied)).Should().Equal(ShowId(4), ShowId(10));
            using var before = await http.GetAsync(tied);
            await ChangeNormalization(
                connection,
                "UPDATE norm.venues SET city='Discovery correction' WHERE id='venue_00000001'"
            );
            using var corrected = await Conditional(tied, before.Headers.ETag!.ToString());
            corrected.StatusCode.Should().Be(HttpStatusCode.OK);
            var beforeShows = JsonNode.Parse(await before.Content.ReadAsStringAsync())!.AsArray();
            var afterShows = JsonNode.Parse(await corrected.Content.ReadAsStringAsync())!.AsArray();
            afterShows
                .Select(show => show!["updated_at"]!.GetValue<string>())
                .Should()
                .Equal(beforeShows.Select(show => show!["updated_at"]!.GetValue<string>()));
            (await Ids(tied)).Should().Equal(ShowId(4), ShowId(10));
        }
        finally
        {
            await connection.ExecuteAsync(
                """
                UPDATE shows SET date='1995-12-31',display_date='1995-12-31',updated_at='2026-01-01T00:00:00Z' WHERE id=910004;
                UPDATE shows SET date='1994-07-01',display_date='1994-07-01',updated_at='2026-01-01T00:00:00Z' WHERE id=910010;
                """
            );
            await connection.ExecuteAsync(
                "DELETE FROM show_source_information WHERE show_id IN (910004,910010)"
            );
            await ChangeNormalization(
                connection,
                "UPDATE norm.venues SET city='Philadelphia' WHERE id='venue_00000001'"
            );
        }
    }

    [Test]
    public async Task Random_returns_a_full_show_without_cache_validation_and_empty_artists_are_explicit()
    {
        var path = BrowseArtistPath + "/shows/random";
        using var response = await Conditional(path, "*");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.ETag.Should().BeNull();
        var show = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        show["artist_id"]!.GetValue<string>().Should().Be(BrowseArtistId);
        JsonNode
            .DeepEquals(show, await GetJson("/api/v4/shows/" + show["id"]!.GetValue<string>()))
            .Should()
            .BeTrue();

        var empty = "/api/v4/artists/" + CatalogId("artist", 20) + "/shows/";
        (await GetJson(empty + "recently-added")).AsArray().Should().BeEmpty();
        using var noRandom = await http.GetAsync(empty + "random");
        noRandom.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var wrongOwner = await http.GetAsync(
            "/api/v4/artists/" + CatalogId("artist", 11) + "/shows/by-date/1994-07-01"
        );
        wrongOwner.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var unknown = await http.GetAsync(
            "/api/v4/artists/" + CatalogId("artist", 9999) + "/shows/top"
        );
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestCase("/api/v4/shows/on-date?month=12")]
    [TestCase("/api/v4/shows/on-date?month=2&day=30")]
    [TestCase("/api/v4/shows/today?month=13")]
    [TestCase("/api/v4/shows/recently-performed?shows=-1")]
    [TestCase("/api/v4/shows/recently-updated?artist_ids=song_00000001")]
    [TestCase("/api/v4/shows/recently-added?sort_by=invalid")]
    [TestCase("/api/v4/shows/recently-added?previous_days=-1")]
    [TestCase("/api/v4/artists/wrong/shows/random")]
    public async Task Discovery_rejects_invalid_inputs_with_uncacheable_problem_details(string path)
    {
        using var response = await http.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }
}
