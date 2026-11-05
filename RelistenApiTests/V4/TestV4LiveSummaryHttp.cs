using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;

namespace RelistenApiTests.V4;

public sealed partial class TestV4CatalogHttp
{
    [Test]
    public async Task Live_summary_groups_playable_tracks_with_typed_catalog_resources_and_shared_cache_headers()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        var clientUuid = Guid.NewGuid();
        var playedAt = DateTime.UtcNow.AddMinutes(-2);
        try
        {
            await connection.ExecuteAsync(
                """
                UPDATE source_tracks SET is_orphaned=true WHERE id=910002;
                UPDATE source_tracks SET source_set_id=NULL WHERE id=910003;
                INSERT INTO source_track_plays (source_track_uuid,user_uuid,app_type,created_at)
                SELECT t.uuid,@clientUuid,2,@playedAt FROM source_tracks t
                CROSS JOIN generate_series(1,2) WHERE t.id IN (910001,910002,910003,910004);
                INSERT INTO source_track_plays (source_track_uuid,user_uuid,app_type,created_at)
                VALUES ('00000000-0000-0000-0000-000000000044',@clientUuid,0,@playedAt),
                       ('00000000-0000-0000-0000-999999999999',@clientUuid,2,@playedAt);
                """,
                new { clientUuid, playedAt }
            );

            using var response = await http.GetAsync(
                "/api/v4/live/summary?sort=plays&month_day=12-31"
            );
            response
                .StatusCode.Should()
                .Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            response.Headers.CacheControl!.Public.Should().BeTrue();
            response.Headers.CacheControl.MaxAge!.Value.TotalSeconds.Should().BeInRange(1, 60);
            var resource = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            resource["total_plays"]!.GetValue<int>().Should().Be(4);
            resource["listened_seconds"]!.GetValue<int>().Should().Be(2400);
            resource["timeline"]!.AsArray().Should().HaveCount(15);
            resource["timeline"]!
                .AsArray()
                .Sum(bucket => bucket!["plays"]!.GetValue<int>())
                .Should()
                .Be(4);
            resource["app_types"]!.AsArray().Single()!["app_type"]!
                .GetValue<string>()
                .Should()
                .Be("web");
            resource["on_this_day"]!["show_ids"]!
                .AsArray()
                .Select(id => id!.GetValue<string>())
                .Should()
                .BeEquivalentTo(ShowId(1), ShowId(4));
            foreach (var scenario in new[] { 1, 4 })
            {
                var entry = resource["shows"]!
                    .AsArray()
                    .Single(item => item!["show"]!["id"]!.GetValue<string>() == ShowId(scenario))!;
                entry["plays"]!.GetValue<int>().Should().Be(2);
                entry["source"]!["id"]!.GetValue<string>().Should().StartWith("source_");
                var track = entry["latest_play"]!["track"]!;
                JsonNode.DeepEquals(track, await GetJson(TrackPath(scenario))).Should().BeTrue();
                resource["artists"]!
                    .AsArray()
                    .Should()
                    .Contain(artist =>
                        artist!["artist_id"]!.GetValue<string>()
                            == track["artist_id"]!.GetValue<string>()
                        && artist["top_show"]!["show"]!["id"]!.GetValue<string>()
                            == ShowId(scenario)
                    );
            }
            using var unchanged = await Conditional(
                "/api/v4/live/summary?sort=plays&month_day=12-31",
                response.Headers.ETag!.ToString()
            );
            if (unchanged.StatusCode == HttpStatusCode.NotModified)
            {
                (await unchanged.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
            }
            else
            {
                unchanged.StatusCode.Should().Be(HttpStatusCode.OK);
                var next = JsonNode.Parse(await unchanged.Content.ReadAsStringAsync())!;
                next["window_end"]!
                    .GetValue<DateTime>()
                    .Should()
                    .BeAfter(resource["window_end"]!.GetValue<DateTime>());
            }
            var schema = await GetJson("/api-docs/v4/swagger.json");
            AssertSchemaFields(schema, "LiveSummaryResource", resource);
            schema["paths"]!["/api/v4/live/summary"]!["get"]!["responses"]!
                ["304"]
                .Should()
                .NotBeNull();
        }
        finally
        {
            await connection.ExecuteAsync(
                "DELETE FROM source_track_plays WHERE user_uuid=@clientUuid; UPDATE source_tracks SET is_orphaned=false WHERE id=910002; UPDATE source_tracks SET source_set_id=910003 WHERE id=910003;",
                new { clientUuid }
            );
        }
    }

    [TestCase("month_day=11-31")]
    [TestCase("sort=other")]
    [TestCase("minutes=bad")]
    public async Task Live_summary_rejects_invalid_query_values(string query)
    {
        using var response = await http.GetAsync("/api/v4/live/summary?" + query);
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }
}
