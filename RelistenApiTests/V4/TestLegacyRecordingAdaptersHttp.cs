using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Relisten.Api.Models;
using Relisten.Services.Popularity;

namespace RelistenApiTests.V4;

public sealed partial class TestV4CatalogHttp
{
    [TestCase(1)]
    [TestCase(3)]
    public async Task Legacy_show_adapter_preserves_full_responses_and_can_roll_back_without_Postgres_scores(
        int scenario
    )
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        var artist = Guid.Parse($"00000000-0000-0000-0000-{10 + scenario:000000000000}");
        var show = Guid.Parse($"00000000-0000-0000-0000-{scenario:000000000000}");
        var key = $"popularity:artist:{artist}:shows:map:30d-48h";
        var map = new Dictionary<Guid, CachedShowPopularity>
        {
            [show] = new()
            {
                momentum_score = 0.4567,
                trend_ratio = 0.8912,
                windows = new()
                {
                    days_30d = new()
                    {
                        plays = 123,
                        hours = 45.6789,
                        hot_score = 1.2345,
                    },
                },
            },
        };
        var addedCounts = (
            await connection.QueryAsync<int>(
                """
                INSERT INTO show_source_information (show_id,artist_id,source_count,max_created_at,max_updated_at)
                VALUES (@id,@id,1,'2026-01-01','2026-01-01') ON CONFLICT DO NOTHING RETURNING show_id
                """,
                new { id = 910000 + scenario }
            )
        ).ToArray();
        var links = (
            await connection.QueryAsync<int>(
                """
                INSERT INTO links (source_id,upstream_source_id,url,label,for_source,for_reviews,for_ratings)
                VALUES (@id,910010,'https://example.invalid/one','One',true,false,false) RETURNING id
                """,
                new { id = 910000 + scenario }
            )
        ).ToArray();
        var cache = new PopularityCacheService(legacyRedis);
        var db = app.Services.GetRequiredService<Relisten.DbService>();
        try
        {
            await cache.SetAsync(
                key,
                new PopularityCacheEntry<Dictionary<Guid, CachedShowPopularity>>
                {
                    generated_at = DateTime.UtcNow,
                    stale_after_seconds = 21600,
                    data = map,
                }
            );
            await new PopularityStore(db).ReplaceArtistShows(artist, map, DateTime.UtcNow);
            var paths = new[]
            {
                $"/api/v2/artists/{910000 + scenario}/shows/1995-12-31",
                $"/api/v2/artists/{910000 + scenario}/years/1995/1995-12-31",
                $"/api/v3/shows/{show}",
            };
            foreach (var path in paths)
            {
                legacyOptions.LegacyRecordingAdaptersEnabled = false;
                using var before = await http.GetAsync(path);
                before
                    .StatusCode.Should()
                    .Be(HttpStatusCode.OK, await before.Content.ReadAsStringAsync());
                var expected = JsonNode.Parse(await before.Content.ReadAsStringAsync())!;
                legacyOptions.LegacyRecordingAdaptersEnabled = true;
                using var after = await http.GetAsync(path);
                after
                    .StatusCode.Should()
                    .Be(HttpStatusCode.OK, await after.Content.ReadAsStringAsync());
                after
                    .Headers.CacheControl!.ToString()
                    .Should()
                    .Be(before.Headers.CacheControl!.ToString());
                var actual = JsonNode.Parse(await after.Content.ReadAsStringAsync())!;
                JsonNode
                    .DeepEquals(actual, expected)
                    .Should()
                    .BeTrue("the adapter preserves the entire legacy wire resource");
                actual["sources"]![0]!["links"]!.AsArray().Should().HaveCount(1);
                actual["sources"]![0]!["sets"]![0]!["tracks"]![0]!["title"]!
                    .GetValue<string>()
                    .Should()
                    .Be("Scarlet Begonias > Fire on the Mountain");
                actual["popularity"]!["windows"]!["30d"]!["plays"]!
                    .GetValue<int>()
                    .Should()
                    .Be(123);
            }

            // Different cache values prove the flag selects the intended score store.
            map[show].windows.days_30d.plays = 999;
            await cache.SetAsync(
                key,
                new PopularityCacheEntry<Dictionary<Guid, CachedShowPopularity>>
                {
                    generated_at = DateTime.UtcNow,
                    stale_after_seconds = 21600,
                    data = map,
                }
            );
            var storedScore = await GetJson(paths[0]);
            storedScore["popularity"]!["windows"]!["30d"]!["plays"]!
                .GetValue<int>()
                .Should()
                .Be(123);

            if (scenario == 3)
            {
                await connection.ExecuteAsync(
                    "UPDATE source_tracks SET is_orphaned=true WHERE source_id=@id",
                    new { id = 910000 + scenario }
                );
                try
                {
                    var empty = await GetJson(paths[2]);
                    empty["sources"]![0]!["sets"]!.AsArray().Should().BeEmpty();
                }
                finally
                {
                    await connection.ExecuteAsync(
                        "UPDATE source_tracks SET is_orphaned=false WHERE source_id=@id",
                        new { id = 910000 + scenario }
                    );
                }
            }

            // With a warm Redis map, disabling the adapter does not read Postgres scores.
            legacyOptions.LegacyRecordingAdaptersEnabled = false;
            await connection.ExecuteAsync(
                "ALTER TABLE show_popularity RENAME TO unavailable_show_popularity"
            );
            try
            {
                using var response = await http.GetAsync(paths[0]);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                var rollback = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
                rollback["popularity"]!["windows"]!["30d"]!["plays"]!
                    .GetValue<int>()
                    .Should()
                    .Be(999);
            }
            finally
            {
                await connection.ExecuteAsync(
                    "ALTER TABLE unavailable_show_popularity RENAME TO show_popularity"
                );
            }
            legacyOptions.LegacyRecordingAdaptersEnabled = true;
            using var missing = await http.GetAsync($"/api/v3/shows/{Guid.Empty}");
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
            missing.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
        finally
        {
            legacyOptions.LegacyRecordingAdaptersEnabled = false;
            await legacyRedis.db.KeyDeleteAsync(key);
            await connection.ExecuteAsync(
                """
                DELETE FROM links WHERE id=ANY(@links);
                DELETE FROM show_source_information WHERE show_id=ANY(@addedCounts);
                DELETE FROM show_popularity WHERE NOT is_global AND artist_uuid=@artist
                """,
                new
                {
                    links,
                    addedCounts,
                    artist,
                }
            );
        }
    }
}
