using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;

namespace RelistenApiTests.V4;

public sealed partial class TestV4CatalogHttp
{
    [Test]
    public async Task Play_reports_persist_typed_tracks_and_history_reuses_catalog_resources_without_client_identity()
    {
        var clientUuid = Guid.NewGuid();
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            foreach (
                var (appType, storedType, scenario) in new[]
                {
                    ("ios", 1, 4),
                    ("web", 2, 1),
                    ("sonos", 3, 3),
                    ("android", 4, 4),
                }
            )
            {
                using var response = await http.PostAsJsonAsync(
                    "/api/v4/live/play",
                    new
                    {
                        track_id = TrackId(scenario),
                        app_type = appType,
                        client_uuid = clientUuid,
                    }
                );
                response
                    .StatusCode.Should()
                    .Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
                response.Headers.CacheControl!.NoStore.Should().BeTrue();
                response.Headers.ETag.Should().BeNull();
                var receipt = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
                receipt["track_id"]!.GetValue<string>().Should().Be(TrackId(scenario));
                receipt["app_type"]!.GetValue<string>().Should().Be(appType);
                receipt.AsObject().ContainsKey("client_uuid").Should().BeFalse();
                var sequence = receipt["sequence"]!.GetValue<int>();
                var stored = await connection.QuerySingleAsync<(Guid Track, Guid Client, int App)>(
                    "SELECT source_track_uuid,user_uuid,app_type::int FROM source_track_plays WHERE id=@sequence",
                    new { sequence }
                );
                stored
                    .Should()
                    .Be(
                        (
                            Guid.Parse($"00000000-0000-0000-0000-{40 + scenario:000000000000}"),
                            clientUuid,
                            storedType
                        )
                    );

                using var historyResponse = await http.GetAsync(
                    $"/api/v4/live/history?after_sequence={sequence - 1}"
                );
                historyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
                historyResponse.Headers.CacheControl!.NoStore.Should().BeTrue();
                historyResponse.Headers.ETag.Should().BeNull();
                var item = JsonNode
                    .Parse(await historyResponse.Content.ReadAsStringAsync())!
                    .AsArray()
                    .Single()!;
                item["sequence"]!.GetValue<int>().Should().Be(sequence);
                item["played_at"]!.ToJsonString().Should().Be(receipt["played_at"]!.ToJsonString());
                item["app_type"]!.GetValue<string>().Should().Be(appType);
                item.AsObject()
                    .Select(property => property.Key)
                    .Should()
                    .BeEquivalentTo("sequence", "played_at", "app_type", "track", "show");
                JsonNode
                    .DeepEquals(item["track"], await GetJson(TrackPath(scenario)))
                    .Should()
                    .BeTrue();
                item["show"]!["id"]!.GetValue<string>().Should().Be(ShowId(scenario));
                item["track"]!["show_id"]!.GetValue<string>().Should().Be(ShowId(scenario));
                JsonNode
                    .DeepEquals(
                        item["show"]!["artist_venue"],
                        (await GetJson(ShowPath(scenario)))["artist_venue"]
                    )
                    .Should()
                    .BeTrue();
            }

            var schema = await GetJson("/api-docs/v4/swagger.json");
            EnumValues(schema, "PlayRequest", "app_type")
                .Should()
                .BeEquivalentTo("ios", "web", "sonos", "android");
            (
                schema["components"]!["schemas"]!["PlayRequest"]!["properties"]!["app_type"]!
                    ["nullable"]
                    ?.GetValue<bool>() ?? false
            )
                .Should()
                .BeFalse();
            schema["paths"]!["/api/v4/live/play"]!["post"]!["requestBody"]!["content"]!
                .AsObject()
                .Select(property => property.Key)
                .Should()
                .Contain("application/json");
        }
        finally
        {
            await connection.ExecuteAsync(
                "DELETE FROM source_track_plays WHERE user_uuid=@clientUuid",
                new { clientUuid }
            );
        }
    }

    [Test]
    public async Task Activity_polling_selects_and_pages_by_sequence_within_the_recent_window()
    {
        var clientUuid = Guid.NewGuid();
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            // Timestamps can tie or run backwards. A sequence cursor must also select by sequence.
            var sequences = (
                await connection.QueryAsync<int>(
                    """
                    INSERT INTO source_track_plays (source_track_uuid,user_uuid,app_type,created_at)
                    SELECT '00000000-0000-0000-0000-000000000044',@clientUuid,1,now()-make_interval(mins=>i)
                    FROM generate_series(1,3) i RETURNING id
                    """,
                    new { clientUuid }
                )
            ).ToArray();
            await connection.ExecuteAsync(
                """
                INSERT INTO source_track_plays (source_track_uuid,user_uuid,app_type,created_at)
                VALUES ('00000000-0000-0000-0000-000000000044',@clientUuid,1,now()-interval '2 hours')
                """,
                new { clientUuid }
            );

            var initial = (await GetJson("/api/v4/live/history?limit=2")).AsArray();
            initial
                .Select(item => item!["sequence"]!.GetValue<int>())
                .Should()
                .Equal(sequences.Skip(1));
            var first = (
                await GetJson($"/api/v4/live/history?after_sequence={sequences[0]}&limit=1")
            ).AsArray();
            first.Single()!["sequence"]!.GetValue<int>().Should().Be(sequences[1]);
            var next = (
                await GetJson($"/api/v4/live/history?after_sequence={sequences[1]}&limit=1")
            ).AsArray();
            next.Single()!["sequence"]!.GetValue<int>().Should().Be(sequences[2]);
            (await GetJson($"/api/v4/live/history?after_sequence={sequences[2]}&limit=9999"))
                .AsArray()
                .Should()
                .BeEmpty();

            await connection.ExecuteAsync(
                "UPDATE source_tracks SET is_orphaned=true WHERE id=910004"
            );
            (await GetJson("/api/v4/live/history")).AsArray().Should().BeEmpty();

            var visible = await connection.QuerySingleAsync<int>(
                """
                INSERT INTO source_track_plays (source_track_uuid,user_uuid,app_type)
                VALUES (NULL,@clientUuid,1),('00000000-0000-0000-0000-000000000041',@clientUuid,0);
                INSERT INTO source_track_plays (source_track_uuid,user_uuid,app_type)
                VALUES ('00000000-0000-0000-0000-000000000041',@clientUuid,2) RETURNING id
                """,
                new { clientUuid }
            );
            var unblocked = (
                await GetJson($"/api/v4/live/history?after_sequence={sequences[0]}&limit=1")
            ).AsArray();
            unblocked.Single()!["sequence"]!.GetValue<int>().Should().Be(visible);
        }
        finally
        {
            await connection.ExecuteAsync(
                "DELETE FROM source_track_plays WHERE user_uuid=@clientUuid; UPDATE source_tracks SET is_orphaned=false WHERE id=910004",
                new { clientUuid }
            );
        }
    }

    [TestCase("{}")]
    [TestCase("{\"track_id\":\"wrong\",\"app_type\":\"ios\"}")]
    [TestCase("{\"track_id\":\"track_0000000000000000000000001c\",\"app_type\":\"unknown\"}")]
    [TestCase("{\"track_id\":\"track_0000000000000000000000001c\",\"app_type\":1}")]
    [TestCase("{\"track_id\":\"track_0000000000000000000000001c\",\"app_type\":null}")]
    [TestCase(
        "{\"track_id\":\"track_0000000000000000000000001c\",\"app_type\":\"web\",\"client_uuid\":\"user_invalid\"}"
    )]
    [TestCase("{")]
    public async Task Invalid_reports_return_422_without_writing_telemetry(string body)
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        var before = await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM source_track_plays"
        );
        using var response = await http.PostAsync(
            "/api/v4/live/play",
            new StringContent(body, Encoding.UTF8, "application/json")
        );
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM source_track_plays"))
            .Should()
            .Be(before);
    }

    [Test]
    public async Task Unknown_or_orphaned_tracks_do_not_record_plays()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        var before = await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM source_track_plays"
        );
        try
        {
            await connection.ExecuteAsync(
                "UPDATE source_tracks SET is_orphaned=true WHERE id=910004"
            );
            foreach (var id in new[] { TrackId(4), CatalogId("track", 999999) })
            {
                using var response = await http.PostAsJsonAsync(
                    "/api/v4/live/play",
                    new { track_id = id, app_type = "web" }
                );
                response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            }
            (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM source_track_plays"))
                .Should()
                .Be(before);
        }
        finally
        {
            await connection.ExecuteAsync(
                "UPDATE source_tracks SET is_orphaned=false WHERE id=910004"
            );
        }
    }

    [TestCase("after_sequence=-1")]
    [TestCase("after_sequence=abc")]
    [TestCase("limit=0")]
    public async Task Invalid_activity_queries_return_422(string query)
    {
        using var response = await http.GetAsync("/api/v4/live/history?" + query);
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Test]
    public async Task Telemetry_dependency_failures_return_503_and_history_recovers()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        await connection.ExecuteAsync(
            "ALTER TABLE source_track_plays RENAME TO unavailable_track_plays"
        );
        try
        {
            using var play = await http.PostAsJsonAsync(
                "/api/v4/live/play",
                new { track_id = TrackId(4), app_type = "web" }
            );
            using var history = await http.GetAsync("/api/v4/live/history");
            foreach (var response in new[] { play, history })
            {
                response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
                response
                    .Content.Headers.ContentType!.MediaType.Should()
                    .Be("application/problem+json");
                response.Headers.CacheControl!.NoStore.Should().BeTrue();
            }
        }
        finally
        {
            await connection.ExecuteAsync(
                "ALTER TABLE unavailable_track_plays RENAME TO source_track_plays"
            );
        }
        (await GetJson("/api/v4/live/history")).AsArray().Should().BeEmpty();
    }
}
