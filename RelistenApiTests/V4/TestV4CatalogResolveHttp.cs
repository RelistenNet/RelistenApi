using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Relisten;

namespace RelistenApiTests.V4;

public sealed partial class TestV4CatalogHttp
{
    private const string ResolvePath = "/api/v4/catalog/resolve";

    [Test]
    public async Task Resolver_preserves_reference_order_and_deduplicates_shallow_entity_patches()
    {
        var trackReference = Reference("track", "v4", TrackId(4));
        var result = await Resolve(
            Reference("artist", "v4", BrowseArtistId),
            Reference("artist", "legacy_uuid", ReferenceUuid(14)),
            Reference("show", "legacy_integer", "910004", BrowseArtistId),
            Reference("tour", "legacy_slug", "winter-tour", BrowseArtistId),
            trackReference,
            trackReference,
            Reference("year", "legacy_uuid", ReferenceUuid(64))
        );
        var references = result["references"]!.AsArray();
        references.Should().HaveCount(7);
        references
            .Select(item => item!["availability"]!.GetValue<string>())
            .Should()
            .OnlyContain(value => value == "available");
        references
            .Select(item => item!["target"]!["id"]!.GetValue<string>())
            .Should()
            .Equal(
                BrowseArtistId,
                BrowseArtistId,
                ShowId(4),
                CatalogId("tour", 84),
                TrackId(4),
                TrackId(4),
                CatalogId("year", 64)
            );
        references[1]!["id"]!.GetValue<string>().Should().Be(ReferenceUuid(14));
        var entities = result["entities"]!;
        entities["artists"]!.AsArray().Should().ContainSingle();
        entities["tracks"]!.AsArray().Should().ContainSingle();
        JsonNode.DeepEquals(entities["tracks"]![0], await GetJson(TrackPath(4))).Should().BeTrue();
        entities["sources"]![0]!.AsObject().ContainsKey("sets").Should().BeFalse();
        entities["source_sets"]![0]!.AsObject().ContainsKey("tracks").Should().BeFalse();
        entities["shows"]![0]!.AsObject().ContainsKey("sources").Should().BeFalse();
        entities["sources"]![0]!["id"]!.GetValue<string>().Should().Be(CatalogId("source", 24));
        entities["source_sets"]![0]!["source_id"]!
            .GetValue<string>()
            .Should()
            .Be(CatalogId("source", 24));
    }

    [Test]
    public async Task Resolver_supports_each_catalog_kind_without_fetching_unrequested_child_collections()
    {
        var relationship = (await GetJson(BrowseArtistPath + "/venues"))
            .AsArray()
            .Single(entry => entry!["artist_venue"] is not null)!["artist_venue"]!;
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO source_reviews(id,uuid,source_id,review,updated_at)
                VALUES(916901,'00000000-0000-0000-0000-000000000601',910004,'Resolver review','2026-01-01');
                """
            );
            var result = await Resolve(
                Reference("artist", "v4", BrowseArtistId),
                Reference("show", "v4", ShowId(4)),
                Reference("source", "v4", CatalogId("source", 24)),
                Reference("source_set", "v4", CatalogId("source_set", 34)),
                Reference("track", "v4", TrackId(4)),
                Reference("year", "v4", CatalogId("year", 64)),
                Reference("tour", "v4", CatalogId("tour", 84)),
                Reference("imported_venue", "v4", CatalogId("imported_venue", 74)),
                Reference("imported_song", "v4", CatalogId("imported_song", 351)),
                Reference("setlist_show", "v4", CatalogId("setlist_show", 361)),
                Reference("source_review", "v4", CatalogId("source_review", 601)),
                Reference("venue", "v4", "venue_00000001"),
                Reference("song", "v4", "song_00000101"),
                Reference("artist_venue", "v4", relationship["id"]!.GetValue<string>())
            );
            result["references"]!
                .AsArray()
                .Should()
                .HaveCount(14)
                .And.OnlyContain(item => item!["availability"]!.GetValue<string>() == "available");
            var entities = result["entities"]!;
            entities["source_reviews"]![0]!["source_id"]!
                .GetValue<string>()
                .Should()
                .Be(CatalogId("source", 24));
            entities["setlist_shows"]![0]!["id"]!
                .GetValue<string>()
                .Should()
                .Be(CatalogId("setlist_show", 361));
            entities["artists"]!.AsArray().Should().ContainSingle();
            entities["songs"]!.AsArray().Should().ContainSingle();
            entities["songs"]![0]!.AsObject().ContainsKey("shows").Should().BeFalse();
            EnumValues(
                    await GetJson("/api-docs/v4/swagger.json"),
                    "CatalogReferenceRequest",
                    "kind"
                )
                .Should()
                .BeEquivalentTo(
                    result["references"]!
                        .AsArray()
                        .Select(item => item!["kind"]!.GetValue<string>())
                );
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM source_reviews WHERE id=916901");
        }
    }

    [Test]
    public async Task Resolver_reports_merges_and_unavailability_without_changing_imported_identity()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        await ChangeNormalization(
            connection,
            """
            INSERT INTO norm.songs(id,artist_uuid,name,slug,author_type,author_ref,run_uuid)
            VALUES('song_zzzzzz01','00000000-0000-0000-0000-000000000301','Cycle fixture','resolver-cycle','importer','v4-review','00000000-0000-0000-0000-000000000099');
            UPDATE norm.songs SET merged_into='song_zzzzzz01' WHERE id='song_00000103';
            UPDATE norm.songs SET merged_into='song_00000103' WHERE id='song_zzzzzz01';
            UPDATE sources SET show_id=NULL WHERE id=910002;
            """
        );
        try
        {
            var result = await Resolve(
                Reference("song", "v4", "song_00000104"),
                Reference("song", "v4", "song_00000105"),
                Reference("imported_song", "legacy_uuid", ReferenceUuid(355)),
                Reference("show", "v4", ShowId(999)),
                Reference("track", "v4", CatalogId("track", 344)),
                Reference("song", "v4", "song_00000101", BrowseArtistId),
                Reference("song", "v4", "song_00000103"),
                Reference("source", "v4", CatalogId("source", 22))
            );
            var references = result["references"]!.AsArray();
            references
                .Select(item => item!["availability"]!.GetValue<string>())
                .Should()
                .Equal(
                    "redirected",
                    "unavailable",
                    "available",
                    "unavailable",
                    "unavailable",
                    "unavailable",
                    "unavailable",
                    "unavailable"
                );
            references[0]!["target"]!["id"]!.GetValue<string>().Should().Be("song_00000101");
            references[2]!["target"]!["kind"]!.GetValue<string>().Should().Be("imported_song");
            references[2]!["target"]!["id"]!
                .GetValue<string>()
                .Should()
                .Be(CatalogId("imported_song", 355));
            references[6]!["reason"]!.GetValue<string>().Should().Be("normalization_unavailable");
            using var graphResponse = await http.PostAsJsonAsync(
                "/api/v4/graphql",
                new
                {
                    query = "{ song(id:\"song_00000103\") { id } artist(id:\""
                        + BrowseArtistId
                        + "\") { id } }",
                }
            );
            var graph = JsonNode.Parse(await graphResponse.Content.ReadAsStringAsync())!;
            graph["errors"]![0]!["extensions"]!["code"]!
                .GetValue<string>()
                .Should()
                .Be("normalization_unavailable");
            graph["data"]!["song"].Should().BeNull();
            graph["data"]!["artist"].Should().NotBeNull();

            foreach (
                var item in references.Where(item =>
                    item!["availability"]!.GetValue<string>() == "unavailable"
                )
            )
            {
                item!["target"].Should().BeNull();
            }
            result["entities"]!["songs"]!.AsArray().Should().ContainSingle();
            result["entities"]!["artists"]!.AsArray().Should().BeEmpty();
        }
        finally
        {
            await ChangeNormalization(
                connection,
                """
                UPDATE norm.songs SET merged_into=NULL WHERE id='song_00000103';
                DELETE FROM norm.songs WHERE id='song_zzzzzz01';
                UPDATE sources SET show_id=910002 WHERE id=910002;
                """
            );
        }
    }

    [Test]
    public async Task Resolver_does_not_guess_ambiguous_slugs_or_parse_numeric_tour_names_as_ids()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO venues(id,uuid,artist_id,name,location,slug,updated_at)
                VALUES(916911,'00000000-0000-0000-0000-000000000611',910004,'First','A','resolver-ambiguous','2026-01-01'),
                      (916912,'00000000-0000-0000-0000-000000000612',910004,'Second','B','resolver-ambiguous','2026-01-01');
                INSERT INTO tours(id,uuid,artist_id,name,slug,upstream_identifier,updated_at)
                VALUES(916913,'00000000-0000-0000-0000-000000000613',910004,'2012','2012','resolver-2012','2026-01-01');
                """
            );
            var result = await Resolve(
                Reference("imported_venue", "legacy_slug", "resolver-ambiguous", BrowseArtistId),
                Reference("tour", "legacy_slug", "2012", BrowseArtistId),
                Reference("source_set", "legacy_integer", "910004", BrowseArtistId)
            );
            var references = result["references"]!;
            references[0]!["availability"]!.GetValue<string>().Should().Be("unavailable");
            references[0]!["reason"]!.GetValue<string>().Should().Be("ambiguous");
            references[1]!["target"]!["id"]!.GetValue<string>().Should().Be(CatalogId("tour", 613));
            references[2]!["target"]!["id"]!
                .GetValue<string>()
                .Should()
                .Be(CatalogId("source_set", 34));
        }
        finally
        {
            await connection.ExecuteAsync(
                "DELETE FROM tours WHERE id=916913; DELETE FROM venues WHERE id IN(916911,916912)"
            );
        }
    }

    [Test]
    public async Task Resolver_distinguishes_retired_normalized_ids_from_unknown_ids()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        await ChangeNormalization(
            connection,
            """
            INSERT INTO norm.venues(id,name,slug,author_type,author_ref,run_uuid)
            VALUES('venue_zzzzzz01','Retired resolver venue','resolver-retired','importer','v4-review','00000000-0000-0000-0000-000000000099');
            DELETE FROM norm.venues WHERE id='venue_zzzzzz01';
            INSERT INTO norm.songs(id,artist_uuid,name,slug,author_type,author_ref,run_uuid)
            VALUES('song_zzzzzz02','00000000-0000-0000-0000-000000000014','Retired resolver song','resolver-retired','importer','v4-review','00000000-0000-0000-0000-000000000099');
            DELETE FROM norm.songs WHERE id='song_zzzzzz02';
            """
        );
        var result = await Resolve(
            Reference("venue", "v4", "venue_zzzzzz01"),
            Reference("venue", "v4", "venue_zzzzzz02")
        );
        result["references"]![0]!["availability"]!.GetValue<string>().Should().Be("retired");
        result["references"]![0]!["target"].Should().BeNull();
        result["references"]![1]!["availability"]!.GetValue<string>().Should().Be("unavailable");
        result["entities"]!["venues"]!.AsArray().Should().BeEmpty();
        foreach (
            var path in new[] { "/api/v4/venues/venue_zzzzzz01", "/api/v4/songs/song_zzzzzz02" }
        )
        {
            using var retired = await http.GetAsync(path);
            retired.StatusCode.Should().Be(HttpStatusCode.Gone);
            retired.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
    }

    [Test]
    public async Task Resolver_validates_input_and_enforces_batch_and_body_limits()
    {
        foreach (
            var reference in new[]
            {
                Reference("unknown", "v4", BrowseArtistId),
                Reference("artist", "unknown", BrowseArtistId),
                Reference("track", "v4", ShowId(4)),
                Reference("show", "legacy_integer", "910004"),
                Reference("artist_venue", "legacy_uuid", ReferenceUuid(14)),
                Reference("year", "legacy_slug", "1995", BrowseArtistId),
            }
        )
        {
            using var invalid = await http.PostAsJsonAsync(
                ResolvePath,
                new { references = new[] { reference } }
            );
            invalid.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            invalid.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
        using var malformed = await http.PostAsync(
            ResolvePath,
            new StringContent("{", Encoding.UTF8, "application/json")
        );
        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var empty = await http.PostAsJsonAsync(
            ResolvePath,
            new { references = Array.Empty<object>() }
        );
        empty.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var oversizedBatch = await http.PostAsJsonAsync(
            ResolvePath,
            new
            {
                references = Enumerable
                    .Repeat(Reference("artist", "v4", BrowseArtistId), 501)
                    .ToArray(),
            }
        );
        oversizedBatch.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var maximum = await Resolve(
            Enumerable.Repeat(Reference("artist", "v4", BrowseArtistId), 500).ToArray()
        );
        maximum["references"]!.AsArray().Should().HaveCount(500);
        maximum["entities"]!["artists"]!.AsArray().Should().ContainSingle();
        using var oversizedRequest = new HttpRequestMessage(HttpMethod.Post, ResolvePath)
        {
            Content = JsonContent.Create(
                new
                {
                    references = new[] { Reference("artist", "v4", BrowseArtistId) },
                    padding = new string('x', 128 * 1024),
                }
            ),
        };
        oversizedRequest.Headers.TransferEncodingChunked = true;
        using var oversizedBody = await http.SendAsync(oversizedRequest);
        oversizedBody.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        oversizedBody.Headers.CacheControl!.NoStore.Should().BeTrue();
        JsonNode.Parse(await oversizedBody.Content.ReadAsStringAsync())!["code"]!
            .GetValue<string>()
            .Should()
            .Be("request_too_large");
    }

    [Test]
    public async Task Resolver_recovers_after_an_uncached_dependency_failure()
    {
        var previous = DbService.ConnStr;
        try
        {
            DbService.ConnStr =
                "Host=127.0.0.1;Port=1;Database=unavailable;Username=relisten;Timeout=1";
            using var failed = await http.PostAsJsonAsync(
                ResolvePath,
                new { references = new[] { Reference("artist", "v4", BrowseArtistId) } }
            );
            failed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            failed.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
        finally
        {
            DbService.ConnStr = previous;
        }
        (await Resolve(Reference("artist", "v4", BrowseArtistId)))["references"]![0]![
            "availability"
        ]!
            .GetValue<string>()
            .Should()
            .Be("available");
    }

    private static object Reference(
        string kind,
        string scheme,
        string id,
        string? artistId = null
    ) =>
        new
        {
            kind,
            scheme,
            id,
            artist_id = artistId,
        };

    private static string ReferenceUuid(int suffix) =>
        $"00000000-0000-0000-0000-{suffix:000000000000}";

    private async Task<JsonNode> Resolve(params object[] references)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ResolvePath)
        {
            Content = JsonContent.Create(new { references }),
        };
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.ETag.Should().BeNull();
        return JsonNode.Parse(body)!;
    }
}
