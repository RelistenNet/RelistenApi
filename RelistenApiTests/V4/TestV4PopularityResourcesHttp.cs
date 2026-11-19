using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Relisten;
using Relisten.Api.Models;
using Relisten.Services.Popularity;

namespace RelistenApiTests.V4;

public sealed partial class TestV4CatalogHttp
{
    [Test]
    public async Task Ordinary_artist_and_year_resources_share_metadata_and_scores_across_reads()
    {
        var owner = Guid.Parse(ReferenceUuid(14));
        var year = Guid.Parse(ReferenceUuid(64));
        var store = new PopularityStore(app.Services.GetRequiredService<DbService>());
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO show_source_information
                    (show_id,artist_id,max_created_at,max_updated_at,source_count,has_soundboard_source)
                VALUES(910004,910004,'2026-01-01','2026-01-01',1,true);
                """
            );
            await store.ReplaceArtists(
                new Dictionary<Guid, PopularityMetrics> { [owner] = Score(100) },
                DateTime.UtcNow
            );
            await store.ReplaceYears(
                owner,
                new Dictionary<Guid, PopularityMetrics> { [year] = Score(10) },
                DateTime.UtcNow
            );
            await store.ReplaceYears(
                null,
                new Dictionary<Guid, PopularityMetrics> { [year] = Score(900) },
                DateTime.UtcNow
            );

            var artists = (await GetJson("/api/v4/artists?include_autocreated=true")).AsArray();
            var artist = await GetJson(BrowseArtistPath);
            var listedArtist = artists.Single(item =>
                item!["id"]!.GetValue<string>() == BrowseArtistId
            )!;
            JsonNode.DeepEquals(artist["popularity"], listedArtist["popularity"]).Should().BeTrue();
            artist["popularity"]!["windows"]!["30d"]!["plays"]!.GetValue<int>().Should().Be(100);
            (await GetJson(ArtistPath(11)))["popularity"].Should().BeNull();

            var years = (await GetJson(BrowseArtistPath + "/years")).AsArray();
            years.Single(item => item!["year"]!.GetValue<string>() == "1994")![
                "has_soundboard_source"
            ]!
                .GetValue<bool>()
                .Should()
                .BeFalse();
            var detail = await GetJson(BrowseArtistPath + "/years/" + CatalogId("year", 64));
            var listedYear = years.Single(item =>
                item!["id"]!.GetValue<string>() == CatalogId("year", 64)
            )!;
            var embeddedYear = (await GetJson(ShowPath(4)))["year"]!;
            var resolved = (
                await Resolve(
                    Reference("artist", "v4", BrowseArtistId),
                    Reference("year", "v4", CatalogId("year", 64))
                )
            )["entities"]!;
            JsonNode
                .DeepEquals(artist["popularity"], resolved["artists"]![0]!["popularity"])
                .Should()
                .BeTrue();
            foreach (
                var resource in new[] { listedYear, detail, embeddedYear, resolved["years"]![0]! }
            )
            {
                resource["has_soundboard_source"]!.GetValue<bool>().Should().BeTrue();
                resource["popularity"]!["windows"]!["30d"]!["plays"]!
                    .GetValue<int>()
                    .Should()
                    .Be(10);
                JsonNode
                    .DeepEquals(resource["popularity"], listedYear["popularity"])
                    .Should()
                    .BeTrue();
            }
            var rankedYear = (await GetJson("/api/v4/popular/years?limit=1"))[0]!;
            rankedYear["has_soundboard_source"]!.GetValue<bool>().Should().BeTrue();
            rankedYear["popularity"]!["windows"]!["30d"]!["plays"]!
                .GetValue<int>()
                .Should()
                .Be(900);
            AssertSchemaFields(
                await GetJson("/api-docs/v4/swagger.json"),
                "ArtistResource",
                artist
            );
            AssertSchemaFields(
                await GetJson("/api-docs/v4/swagger.json"),
                "YearResource",
                listedYear
            );
        }
        finally
        {
            await connection.ExecuteAsync(
                "DELETE FROM show_source_information WHERE show_id=910004"
            );
            await ClearBrowseScores(owner);
        }
    }

    [TestCase("artist")]
    [TestCase("year")]
    public async Task Score_only_changes_revalidate_normal_resources_and_clear_inactive_scores(
        string kind
    )
    {
        var owner = Guid.Parse(ReferenceUuid(14));
        var year = Guid.Parse(ReferenceUuid(64));
        var store = new PopularityStore(app.Services.GetRequiredService<DbService>());
        var path =
            kind == "artist"
                ? BrowseArtistPath
                : BrowseArtistPath + "/years/" + CatalogId("year", 64);
        Task Publish(PopularityMetrics? metrics)
        {
            var map = new Dictionary<Guid, PopularityMetrics>();
            if (metrics is not null)
            {
                map[kind == "artist" ? owner : year] = metrics;
            }
            return kind == "artist"
                ? store.ReplaceArtists(map, DateTime.UtcNow)
                : store.ReplaceYears(owner, map, DateTime.UtcNow);
        }
        try
        {
            await Publish(Score(10));
            using var initial = await http.GetAsync(path);
            var original = JsonNode.Parse(await initial.Content.ReadAsStringAsync())!;
            var etag = initial.Headers.ETag!.ToString();
            using var unchanged = await Conditional(path, etag);
            unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);

            await Publish(Score(20));
            using var changed = await Conditional(path, etag);
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
            changed.Headers.ETag!.ToString().Should().NotBe(etag);
            var current = JsonNode.Parse(await changed.Content.ReadAsStringAsync())!;
            current["updated_at"]!
                .ToJsonString()
                .Should()
                .Be(original["updated_at"]!.ToJsonString());
            current["popularity"]!["windows"]!["30d"]!["plays"]!.GetValue<int>().Should().Be(20);

            await Publish(null);
            using var inactive = await Conditional(path, changed.Headers.ETag!.ToString());
            inactive.StatusCode.Should().Be(HttpStatusCode.OK);
            JsonNode
                .Parse(await inactive.Content.ReadAsStringAsync())!["popularity"]
                .Should()
                .BeNull();
        }
        finally
        {
            await ClearBrowseScores(owner);
        }
    }

    [Test]
    public async Task Yearly_only_popularity_is_metadata_with_numeric_scores_and_stays_out_of_rankings()
    {
        var owner = Guid.Parse(ReferenceUuid(14));
        var year = Guid.Parse(ReferenceUuid(64));
        var show = Guid.Parse(ReferenceUuid(4));
        var rankedShow = Guid.Parse(ReferenceUuid(1));
        await using var connection = await MigratedTestDatabase.OpenAsync();

        try
        {
            var store = new PopularityStore(app.Services.GetRequiredService<DbService>());
            var time = DateTime.UtcNow;
            await store.ReplaceArtists(new Dictionary<Guid, PopularityMetrics>(), time);
            await store.ReplaceYears(owner, new Dictionary<Guid, PopularityMetrics>(), time);
            await store.ReplaceGlobalShows(
                new Dictionary<Guid, PopularityMetrics> { [rankedShow] = Score(10) },
                time
            );
            await store.ReplaceArtistShows(
                owner,
                new Dictionary<Guid, CachedShowPopularity>(),
                time
            );
            await connection.ExecuteAsync(
                """
                UPDATE artist_popularity SET plays_1y=81,hours_1y=10,hot_score_1y=9 WHERE uuid=@owner;
                UPDATE year_popularity SET plays_1y=49,hours_1y=8,hot_score_1y=7 WHERE uuid=@year;
                UPDATE show_popularity SET plays_1y=25,hours_1y=6,hot_score_1y=5 WHERE uuid=@show;
                UPDATE show_popularity SET plays_1y=100,hours_1y=12,hot_score_1y=10
                WHERE uuid=@rankedShow AND is_global;
                """,
                new
                {
                    owner,
                    year,
                    show,
                    rankedShow,
                }
            );

            var yearId = CatalogId("year", 64);
            foreach (
                var (path, plays, hours, score) in new[]
                {
                    (BrowseArtistPath, 81, 10, 9),
                    (BrowseArtistPath + "/years/" + yearId, 49, 8, 7),
                    (ShowPath(4), 25, 6, 5),
                }
            )
            {
                var popularity = (await GetJson(path))["popularity"]!;
                popularity["momentum_score"]!.GetValue<double>().Should().Be(0);
                popularity["trend_ratio"]!.GetValue<double>().Should().Be(0);
                var yearly = popularity["windows"]!["1y"]!;
                yearly["plays"]!.GetValue<long>().Should().Be(plays);
                yearly["hours"]!.GetValue<double>().Should().Be(hours);
                yearly["hot_score"]!.GetValue<double>().Should().Be(score);
            }

            var ranked = (await GetJson("/api/v4/popular/shows")).AsArray();
            ranked.Should().NotContain(item => item!["id"]!.GetValue<string>() == ShowId(4));
            var rankedResource = ranked.Single(item =>
                item!["id"]!.GetValue<string>() == ShowId(1)
            );
            rankedResource!["popularity"]!["windows"]!["1y"]!["plays"]!
                .GetValue<long>()
                .Should()
                .Be(100);
        }
        finally
        {
            await ClearBrowseScores(owner);
            await connection.ExecuteAsync(
                "DELETE FROM show_popularity WHERE uuid=ANY(@shows)",
                new { shows = new[] { show, rankedShow } }
            );
        }
    }

    private static PopularityMetrics Score(int plays) =>
        PopularityService.CreateMetrics(plays, 2, 1, 1, plays * 2, 12000, 2400, 1200);

    private static async Task ClearBrowseScores(Guid owner)
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        await connection.ExecuteAsync(
            "DELETE FROM artist_popularity WHERE uuid=@owner; DELETE FROM year_popularity WHERE artist_uuid=@owner",
            new { owner }
        );
    }
}
