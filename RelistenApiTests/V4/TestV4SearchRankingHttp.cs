using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Relisten;
using Relisten.Api.Models;
using Relisten.Services.Popularity;
using RelistenApiTests.Popularity;

namespace RelistenApiTests.V4;

public sealed partial class TestV4CatalogHttp
{
    [Test]
    public async Task Stale_show_list_queues_one_batch_and_queue_failure_is_uncacheable_then_recovers()
    {
        var owner = Guid.Parse("00000000-0000-0000-0000-000000000014");
        var store = new PopularityStore(app.Services.GetRequiredService<DbService>());
        var metrics = PopularityService.CreateMetrics(100, 20, 1, 10, 200, 12000, 2400, 1200);
        await store.ReplaceArtistShows(
            owner,
            new Dictionary<Guid, CachedShowPopularity>
            {
                [Guid.Parse("00000000-0000-0000-0000-000000000004")] =
                    CachedShowPopularityMapper.FromPopularityMetrics(metrics),
            },
            DateTime.UtcNow
        );
        await using var connection = await MigratedTestDatabase.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE show_popularity SET generated_at=now()-interval '2 hours' WHERE NOT is_global AND artist_uuid=@owner",
            new { owner }
        );
        var count = refreshJobs.Batches.Count;
        using var initial = await http.GetAsync(BrowseArtistPath + "/shows");
        initial.StatusCode.Should().Be(HttpStatusCode.OK);
        refreshJobs.Batches.Should().HaveCount(count + 1);
        refreshJobs
            .Batches.Last()
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    new PopularityPopulation(PopularityPopulationKind.Year, owner),
                    new PopularityPopulation(PopularityPopulationKind.Show, owner),
                }
            );
        var etag = initial.Headers.ETag!.ToString();

        refreshJobs.FailNext = true;
        using var failed = await Conditional(BrowseArtistPath + "/shows", etag);
        failed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        failed.Headers.CacheControl!.NoStore.Should().BeTrue();
        failed.Headers.ETag.Should().BeNull();
        using var recovered = await Conditional(BrowseArtistPath + "/shows", etag);
        recovered.StatusCode.Should().Be(HttpStatusCode.NotModified);
        refreshJobs.Batches.Should().HaveCount(count + 2);
    }

    [Test]
    public async Task Inactive_rows_remain_null_and_empty_without_repeat_refreshes_or_zero_artist_aliases()
    {
        var owner = Guid.Parse("00000000-0000-0000-0000-000000000014");
        var store = new PopularityStore(app.Services.GetRequiredService<DbService>());
        await store.ReplaceArtistShows(
            owner,
            new Dictionary<Guid, CachedShowPopularity>(),
            DateTime.UtcNow
        );
        await store.ReplaceGlobalShows(new Dictionary<Guid, PopularityMetrics>(), DateTime.UtcNow);
        await store.ReplaceYears(owner, new Dictionary<Guid, PopularityMetrics>(), DateTime.UtcNow);
        var count = refreshJobs.Batches.Count;
        (await GetJson(ShowPath(4)))["popularity"].Should().BeNull();
        (await GetJson("/api/v4/popular/shows")).AsArray().Should().BeEmpty();
        (await GetJson($"/api/v4/shows/momentum?artist_ids={BrowseArtistId}"))
            .AsArray()
            .Should()
            .BeEmpty();
        refreshJobs.Batches.Should().HaveCount(count);

        await using var connection = await MigratedTestDatabase.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE show_popularity SET generated_at=now()-interval '2 hours' WHERE is_global"
        );
        (await GetJson($"/api/v4/shows/momentum?artist_ids={CatalogId("artist", 0)}"))
            .AsArray()
            .Should()
            .BeEmpty();
        refreshJobs.Batches.Should().HaveCount(count);
    }

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
        foreach (var entry in raw["songs"]!.AsArray())
        {
            entry!["imported_song"]!.AsObject().ContainsKey("normalized_song_id").Should().BeTrue();
            entry["imported_song"]!["normalized_song_id"].Should().BeNull();
        }
        // Only the imported title matches; its evidence points through a merged song.
        var mapped = await GetJson(
            $"/api/v4/search?q=Imported%20Scarlet&artist_id={CatalogId("artist", 301)}"
        );
        mapped["songs"]!.AsArray().Should().ContainSingle();
        mapped["songs"]![0]!["song"]!["id"]!.GetValue<string>().Should().Be("song_00000101");
        mapped["songs"]![0]!["imported_song"].Should().BeNull();
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
        var store = new PopularityStore(app.Services.GetRequiredService<DbService>());
        var owner = Guid.Parse("00000000-0000-0000-0000-000000000014");
        var rawOwner = Guid.Parse("00000000-0000-0000-0000-000000000011");
        try
        {
            CachedShowPopularity Metrics(int month, int week, int hours, double momentum = 0.1) =>
                new()
                {
                    windows = new PopularityWindows
                    {
                        days_30d = new()
                        {
                            plays = month,
                            hours = 1,
                            hot_score = month,
                        },
                        days_7d = new()
                        {
                            plays = week,
                            hours = 1,
                            hot_score = week,
                        },
                        hours_48h = new()
                        {
                            plays = hours,
                            hours = 1,
                            hot_score = hours,
                        },
                    },
                    trend_ratio = 2,
                    momentum_score = momentum,
                    plays_6h = 1,
                    plays_90d = 100,
                    momentum = new() { raw = momentum, organic = 1 - momentum },
                };
            static Dictionary<Guid, PopularityMetrics> PublicMetrics(
                Dictionary<Guid, CachedShowPopularity> values
            ) =>
                values.ToDictionary(
                    item => item.Key,
                    item => CachedShowPopularityMapper.ToPopularityMetrics(item.Value)
                );
            var showMap = new Dictionary<Guid, CachedShowPopularity>
            {
                [Guid.Parse("00000000-0000-0000-0000-000000000004")] = Metrics(100, 10, 20, 0.9),
                [Guid.Parse("00000000-0000-0000-0000-000000000010")] = Metrics(50, 40, 5, 0.2),
            };
            await store.ReplaceArtists(
                PublicMetrics(
                    new Dictionary<Guid, CachedShowPopularity> { [owner] = Metrics(2000, 100, 200) }
                ),
                DateTime.UtcNow
            );
            // Each ranking publishes the scores used by that endpoint.
            var globalMap = new Dictionary<Guid, CachedShowPopularity>
            {
                [Guid.Parse("00000000-0000-0000-0000-000000000004")] = Metrics(100, 10, 20, 0.4),
                [Guid.Parse("00000000-0000-0000-0000-000000000010")] = Metrics(50, 40, 5, 0.5),
            };
            await store.ReplaceGlobalShows(PublicMetrics(globalMap), DateTime.UtcNow);
            await store.ReplaceArtistShows(owner, showMap, DateTime.UtcNow);
            await store.ReplaceArtistShows(
                rawOwner,
                new Dictionary<Guid, CachedShowPopularity>
                {
                    [Guid.Parse("00000000-0000-0000-0000-000000000001")] = Metrics(25, 10, 5, 0.3),
                },
                DateTime.UtcNow
            );
            await store.ReplaceYears(
                null,
                PublicMetrics(
                    new Dictionary<Guid, CachedShowPopularity>
                    {
                        [Guid.Parse("00000000-0000-0000-0000-000000000064")] = Metrics(
                            2000,
                            100,
                            200
                        ),
                    }
                ),
                DateTime.UtcNow
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
            var momentum = (
                await GetJson($"/api/v4/shows/momentum?artist_ids={BrowseArtistId}")
            ).AsArray();
            momentum
                .Select(show => show!["id"]!.GetValue<string>())
                .Should()
                .Equal(ShowId(10), ShowId(4));
            momentum[0]!["popularity"]!["momentum_score"]!.GetValue<double>().Should().Be(0.8);
            momentum[1]!["popularity"]!["momentum_score"]!.GetValue<double>().Should().Be(0.1);
            (await GetJson("/api/v4/popular/artists"))[0]!["features"]!.Should().NotBeNull();
            (await GetJson("/api/v4/trending/years"))[0]!["id"]!
                .GetValue<string>()
                .Should()
                .StartWith("year_");
            var expectedPopularity = (await GetJson(ShowPath(4)))["popularity"]!;
            expectedPopularity["momentum_score"]!.GetValue<double>().Should().Be(0.9);
            var document = await GetJson("/api-docs/v4/swagger.json");
            AssertSchemaFields(document, "ArtistRankingResource", groups[0]!);
            AssertSchemaFields(document, "PopularityResource", expectedPopularity);
            AssertSchemaFields(
                document,
                "PopularityWindowsResource",
                expectedPopularity["windows"]!
            );
            AssertSchemaFields(
                document,
                "PopularityWindowResource",
                expectedPopularity["windows"]!["48h"]!
            );
            await AssertArtistLocalPopularityAcrossRoutes(expectedPopularity);
            JsonNode.DeepEquals(grouped["popularity"], expectedPopularity).Should().BeTrue();
            var resolved = await Resolve(Reference("show", "v4", ShowId(4)));
            JsonNode
                .DeepEquals(resolved["entities"]!["shows"]![0]!["popularity"], expectedPopularity)
                .Should()
                .BeTrue();
            var raw = await GetJson(ShowPath(1));
            raw["artist_venue"].Should().BeNull();
            raw["popularity"]!["momentum_score"]!.GetValue<double>().Should().Be(0.3);
            var rawList = await GetJson("/api/v4/artists/" + CatalogId("artist", 11) + "/shows");
            JsonNode.DeepEquals(rawList[0]!["popularity"], raw["popularity"]).Should().BeTrue();
            var noActivity = await GetJson(ShowPath(2));
            noActivity.AsObject().ContainsKey("popularity").Should().BeTrue();
            noActivity["popularity"].Should().BeNull();
            using var ranked = await http.GetAsync("/api/v4/popular/shows");
            var globalShows = JsonNode.Parse(await ranked.Content.ReadAsStringAsync())!.AsArray();
            globalShows.Single(show => show!["id"]!.GetValue<string>() == ShowId(4))![
                "popularity"
            ]!["momentum_score"]!
                .GetValue<double>()
                .Should()
                .Be(0.4);
            using var unchanged = await Conditional(
                "/api/v4/popular/shows",
                ranked.Headers.ETag!.ToString()
            );
            unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);
            using var artistShows = await http.GetAsync(BrowseArtistPath + "/shows");
            showMap[Guid.Parse("00000000-0000-0000-0000-000000000004")] = Metrics(100, 10, 20, 0.7);
            await store.ReplaceArtistShows(owner, showMap, DateTime.UtcNow);
            using var changed = await Conditional(
                "/api/v4/popular/shows",
                ranked.Headers.ETag!.ToString()
            );
            changed.StatusCode.Should().Be(HttpStatusCode.NotModified);
            using var changedShows = await Conditional(
                BrowseArtistPath + "/shows",
                artistShows.Headers.ETag!.ToString()
            );
            changedShows.StatusCode.Should().Be(HttpStatusCode.OK);
            var updated = (await GetJson(ShowPath(4)))["popularity"]!;
            updated["momentum_score"]!.GetValue<double>().Should().Be(0.7);
            globalMap[Guid.Parse("00000000-0000-0000-0000-000000000004")] = Metrics(
                100,
                10,
                20,
                0.6
            );
            await store.ReplaceGlobalShows(PublicMetrics(globalMap), DateTime.UtcNow);
            using var globalChanged = await Conditional(
                "/api/v4/popular/shows",
                ranked.Headers.ETag!.ToString()
            );
            globalChanged.StatusCode.Should().Be(HttpStatusCode.OK);
            // Catalog reads remain available without a Redis connection or refresh services.
            (await GetJson("/api/v4/popular/shows"))[0]!["popularity"]!["momentum_score"]!
                .GetValue<double>()
                .Should()
                .Be(0.6);
            await store.ReplaceArtistShows(owner, showMap, DateTime.UtcNow);
            (await GetJson(ShowPath(4)))["popularity"]!["momentum_score"]!
                .GetValue<double>()
                .Should()
                .Be(0.7);

            // Warm Redis maps round ratios before selection. Equal published ratios
            // must use hot-score ordering, rather than hidden extra precision.
            var hotter = Metrics(3000, 100, 200);
            hotter.trend_ratio = 1.234441;
            var cooler = Metrics(2000, 100, 200);
            cooler.trend_ratio = 1.234449;
            await store.ReplaceArtists(
                PublicMetrics(new() { [owner] = hotter, [rawOwner] = cooler }),
                DateTime.UtcNow
            );
            (await Ids("/api/v4/trending/artists"))
                .Should()
                .Equal(CatalogId("artist", 14), CatalogId("artist", 11));
        }
        finally
        {
            await using var connection = await MigratedTestDatabase.OpenAsync();
            await connection.ExecuteAsync(
                """
                DELETE FROM show_popularity WHERE artist_uuid=ANY(@owners);
                DELETE FROM year_popularity WHERE artist_uuid=ANY(@owners);
                DELETE FROM artist_popularity WHERE uuid=ANY(@owners);
                """,
                new { owners = new[] { owner, rawOwner } }
            );
        }
    }

    private async Task AssertArtistLocalPopularityAcrossRoutes(JsonNode expectedPopularity)
    {
        foreach (
            var (path, field) in new[]
            {
                (BrowseArtistPath + "/shows", (string?)null),
                (BrowseArtistPath + "/years/by-number/1995", "shows"),
                ("/api/v4/venues/venue_00000001", "shows"),
                (BrowseArtistPath + "/venues/venue_00000001", "shows"),
                ("/api/v4/imported-venues/" + CatalogId("imported_venue", 74), "shows"),
                ("/api/v4/tours/" + CatalogId("tour", 84), "shows"),
                ($"/api/v4/search?q=1995-12-31&artist_id={BrowseArtistId}", "shows"),
                (BrowseArtistPath + "/shows/on-date?month=12&day=31", null),
                (BrowseArtistPath + "/shows/recently-performed", null),
                (BrowseArtistPath + "/shows/popular-trending", "popular_shows"),
            }
        )
        {
            var body = await GetJson(path);
            var list = (field is null ? body : body[field])!.AsArray();
            var show = list.Single(item => item!["id"]!.GetValue<string>() == ShowId(4))!;
            JsonNode.DeepEquals(show["popularity"], expectedPopularity).Should().BeTrue(path);
        }

        var song = await GetJson("/api/v4/songs/song_00000003");
        var songShow = song["shows"]!
            .AsArray()
            .Single(entry => entry!["show"]!["id"]!.GetValue<string>() == ShowId(4))!["show"]!;
        JsonNode.DeepEquals(songShow["popularity"], expectedPopularity).Should().BeTrue();
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
}
