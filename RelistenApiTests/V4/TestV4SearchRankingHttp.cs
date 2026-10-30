using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Relisten.Services.Popularity;
using StackExchange.Redis;

namespace RelistenApiTests.V4;

public sealed partial class TestV4CatalogHttp
{
    [Test]
    public async Task Search_returns_existing_resource_shapes_and_approved_aliases_with_imported_fallback()
    {
        var result = await GetJson($"/api/v4/search?q=Scarlet&artist_id={BrowseArtistId}");
        result["songs"]!.AsArray().Should().NotBeEmpty();
        result["songs"]![0]!["kind"]!.GetValue<string>().Should().Be("song");
        result["songs"]![0]!["song"]!["id"]!.GetValue<string>().Should().Be("song_00000003");
        var raw = await GetJson($"/api/v4/search?q=Scarlet&artist_id={CatalogId("artist", 11)}");
        raw["songs"]!
            .AsArray()
            .Should()
            .OnlyContain(entry => entry!["kind"]!.GetValue<string>() == "imported_song");
        var venues = await GetJson($"/api/v4/search?q=Historical&artist_id={BrowseArtistId}");
        venues["venues"]!.AsArray().Should().Contain(entry => entry!["artist_venue"] != null);
        var show = await GetJson($"/api/v4/search?q=1995-12-31&artist_id={BrowseArtistId}");
        show["shows"]![0]!["id"]!.GetValue<string>().Should().Be(ShowId(4));
        show["shows"]![0]!["artist_venue"].Should().NotBeNull();
        var source = await GetJson($"/api/v4/search?q=Fixture%20taper&artist_id={BrowseArtistId}");
        source["sources"]!.AsArray().Should().NotBeEmpty();
        source["sources"]![0]!["show_id"]!.GetValue<string>().Should().Be(ShowId(4));
        source["sources"]![0]!.AsObject().ContainsKey("sets").Should().BeFalse();
        var tour = await GetJson($"/api/v4/search?q=Winter&artist_id={BrowseArtistId}");
        tour["tours"]![0]!["id"]!.GetValue<string>().Should().StartWith("tour_");
        var artist = await GetJson($"/api/v4/search?q=Matched&artist_id={BrowseArtistId}");
        artist["artists"]![0]!["features"]!["normalization"]!.GetValue<bool>().Should().BeTrue();
        (await GetJson($"/api/v4/search?q=Scarlet&artist_id={BrowseArtistId}&songs=false"))[
            "songs"
        ]!
            .AsArray()
            .Should()
            .BeEmpty();
    }

    [Test]
    public async Task Search_finds_merged_venue_aliases_and_imported_labels_using_the_current_relationship()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await ChangeNormalization(
                connection,
                """
                UPDATE norm.venues SET parent_id=NULL WHERE id='venue_00000001';
                INSERT INTO norm.venue_names (venue_id,normalized,name,kind,valid_from,valid_to,author_type,author_ref,run_uuid)
                SELECT 'venue_00000003',normalized,name,kind,valid_from,valid_to,author_type,author_ref,run_uuid
                FROM norm.venue_names WHERE venue_id='venue_00000001' AND name='Historical Areena';
                DELETE FROM norm.venue_names WHERE venue_id='venue_00000001' AND name='Historical Areena';
                UPDATE norm.venues SET merged_into='venue_00000001' WHERE id='venue_00000003';
                UPDATE norm.show_venues SET venue_id='venue_00000003' WHERE show_uuid='00000000-0000-0000-0000-000000000004';
                """
            );
            foreach (var term in new[] { "Historical Areena", "Imported Civic" })
            {
                var result = await GetJson(
                    $"/api/v4/search?q={Uri.EscapeDataString(term)}&artist_id={BrowseArtistId}"
                );
                result["venues"]!
                    .AsArray()
                    .Should()
                    .Contain(entry =>
                        entry!["artist_venue"] != null
                        && entry["artist_venue"]!["venue"]!["id"]!.GetValue<string>()
                            == "venue_00000001"
                    );
            }
        }
        finally
        {
            await ChangeNormalization(
                connection,
                """
                UPDATE norm.show_venues SET venue_id='venue_00000001' WHERE show_uuid='00000000-0000-0000-0000-000000000004';
                UPDATE norm.venues SET merged_into=NULL WHERE id='venue_00000003';
                INSERT INTO norm.venue_names (venue_id,normalized,name,kind,valid_from,valid_to,author_type,author_ref,run_uuid)
                SELECT 'venue_00000001',normalized,name,kind,valid_from,valid_to,author_type,author_ref,run_uuid
                FROM norm.venue_names WHERE venue_id='venue_00000003' AND name='Historical Areena';
                DELETE FROM norm.venue_names WHERE venue_id='venue_00000003' AND name='Historical Areena';
                UPDATE norm.venues SET parent_id='venue_00000003' WHERE id='venue_00000001';
                """
            );
        }
    }

    [TestCase("/api/v4/search")]
    [TestCase("/api/v4/search?q=ab")]
    [TestCase(
        "/api/v4/search?q=Scarlet&artists=false&shows=false&songs=false&sources=false&tours=false&venues=false"
    )]
    public async Task Empty_search_always_contains_all_six_categories(string path)
    {
        var result = (await GetJson(path)).AsObject();
        result
            .Select(entry => entry.Key)
            .Should()
            .BeEquivalentTo("artists", "shows", "songs", "sources", "tours", "venues");
        result.All(entry => entry.Value!.AsArray().Count == 0).Should().BeTrue();
    }

    [Test]
    public async Task Ranking_windows_thresholds_groups_and_organic_momentum_preserve_current_catalog_metadata()
    {
        var keys = new[]
        {
            "popularity:artists:map:30d-48h",
            "popularity:shows:map:30d-48h",
            "popularity:years:map:30d-48h",
            $"popularity:artist:{Guid.Parse("00000000-0000-0000-0000-000000000014")}:shows:map:30d-48h",
        };
        var previous = new Dictionary<string, RedisValue>();
        foreach (var key in keys)
            previous[key] = await rankingRedis.db.StringGetAsync(key);
        try
        {
            object Metrics(int month, int week, int hours, double momentum = 0.1) =>
                new
                {
                    windows = new Dictionary<string, object>
                    {
                        ["30d"] = new
                        {
                            plays = month,
                            hours = 1,
                            hot_score = month,
                        },
                        ["7d"] = new
                        {
                            plays = week,
                            hours = 1,
                            hot_score = week,
                        },
                        ["48h"] = new
                        {
                            plays = hours,
                            hours = 1,
                            hot_score = hours,
                        },
                    },
                    trend_ratio = 2.0,
                    momentum_score = momentum,
                    plays_6h = 1,
                    plays_90d = 100,
                    momentum = new { raw = momentum, organic = 1 - momentum },
                };
            async Task Put(string key, object data) =>
                await rankingRedis.db.StringSetAsync(
                    key,
                    Newtonsoft.Json.JsonConvert.SerializeObject(
                        new
                        {
                            generated_at = DateTime.UtcNow,
                            stale_after_seconds = 3600,
                            data,
                        }
                    )
                );
            var showMap = new Dictionary<Guid, object>
            {
                [Guid.Parse("00000000-0000-0000-0000-000000000004")] = Metrics(100, 10, 20, 0.9),
                [Guid.Parse("00000000-0000-0000-0000-000000000010")] = Metrics(50, 40, 5, 0.2),
            };
            await Put(
                keys[0],
                new Dictionary<Guid, object>
                {
                    [Guid.Parse("00000000-0000-0000-0000-000000000014")] = Metrics(2000, 100, 200),
                }
            );
            await Put(keys[1], showMap);
            await Put(keys[3], showMap);
            await Put(
                keys[2],
                new Dictionary<Guid, object>
                {
                    [Guid.Parse("00000000-0000-0000-0000-000000000064")] = Metrics(2000, 100, 200),
                }
            );
            async Task<string[]> Ids(string path) =>
                (await GetJson(path))
                    .AsArray()
                    .Select(item => item!["id"]!.GetValue<string>())
                    .ToArray();
            (await Ids("/api/v4/popular/shows?window=30d")).Should().Equal(ShowId(4), ShowId(10));
            (await Ids("/api/v4/popular/shows?window=7d")).Should().Equal(ShowId(10), ShowId(4));
            (await Ids("/api/v4/trending/shows?window=48h")).Should().Equal(ShowId(4));
            var groups = await GetJson(
                $"/api/v4/shows/popular-trending?artist_ids={BrowseArtistId}&artist_ids={BrowseArtistId}&limit=1"
            );
            groups.AsArray().Should().HaveCount(1);
            groups[0]!["popular_shows"]!.AsArray().Should().HaveCount(1);
            var grouped = groups[0]!["popular_shows"]![0]!;
            grouped["artist_venue"].Should().NotBeNull();
            grouped["id"]!.GetValue<string>().Should().Be(ShowId(4));
            (await Ids($"/api/v4/shows/momentum?artist_ids={BrowseArtistId}"))
                .Should()
                .Equal(ShowId(10), ShowId(4));
            (await GetJson("/api/v4/popular/artists"))[0]!["features"]!.Should().NotBeNull();
            (await GetJson("/api/v4/trending/years"))[0]!["id"]!
                .GetValue<string>()
                .Should()
                .StartWith("year_");
            using var ranked = await http.GetAsync("/api/v4/popular/shows");
            using var unchanged = await Conditional(
                "/api/v4/popular/shows",
                ranked.Headers.ETag!.ToString()
            );
            unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);
        }
        finally
        {
            foreach (var (key, value) in previous)
            {
                if (value.IsNull)
                    await rankingRedis.db.KeyDeleteAsync(key);
                else
                    await rankingRedis.db.StringSetAsync(key, value);
            }
        }
    }

    [Test]
    public async Task Empty_ranking_selections_stay_empty_and_invalid_window_is_explicit()
    {
        (await GetJson("/api/v4/shows/momentum")).AsArray().Should().BeEmpty();
        (await GetJson("/api/v4/shows/popular-trending")).AsArray().Should().BeEmpty();
        using var invalid = await http.GetAsync("/api/v4/popular/shows?window=forever");
        invalid.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        invalid.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var invalidId = await http.GetAsync("/api/v4/search?q=Scarlet&artist_id=wrong");
        invalidId.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    public class UnexpectedRankingRefresh : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException(
                "Fresh ranking fixture must not enqueue background work"
            );
    }
}
