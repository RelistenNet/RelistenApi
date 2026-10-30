using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Relisten;

namespace RelistenApiTests.V4;

public sealed partial class TestV4CatalogHttp
{
    private static string TourPath(int suffix) => "/api/v4/tours/" + CatalogId("tour", suffix);

    [Test]
    public async Task Tour_detail_matches_browse_and_embedded_metadata_and_revalidates_membership()
    {
        var path = TourPath(84);
        var detail = await GetJson(path);
        var tours = (await GetJson(BrowseArtistPath + "/tours")).AsArray();
        tours.Should().ContainSingle();
        JsonNode.DeepEquals(tours[0], ResourceMetadata(detail)).Should().BeTrue();
        JsonNode.DeepEquals(tours[0], (await GetJson(ShowPath(4)))["tour"]).Should().BeTrue();
        detail["show_count"]!.GetValue<int>().Should().Be(1);
        detail["shows"]!
            .AsArray()
            .Select(show => show!["id"]!.GetValue<string>())
            .Should()
            .Equal(ShowId(4));
        using var initial = await http.GetAsync(path);
        var etag = initial.Headers.ETag!.ToString();
        using var unchanged = await Conditional(path, etag);
        unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await connection.ExecuteAsync("UPDATE shows SET tour_id=910004 WHERE id=910010");
            using var changed = await Conditional(path, etag);
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
            changed.Headers.ETag!.ToString().Should().NotBe(etag);
            var current = JsonNode.Parse(await changed.Content.ReadAsStringAsync())!;
            current["updated_at"]!.ToJsonString().Should().Be(detail["updated_at"]!.ToJsonString());
            current["show_count"]!.GetValue<int>().Should().Be(2);
            current["shows"]!
                .AsArray()
                .Select(show => show!["id"]!.GetValue<string>())
                .Should()
                .Equal(ShowId(10), ShowId(4));
        }
        finally
        {
            await connection.ExecuteAsync("UPDATE shows SET tour_id=NULL WHERE id=910010");
        }
    }

    [Test]
    public async Task Known_empty_tours_eras_and_reviews_return_complete_empty_collections()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO tours(id,uuid,artist_id,name,slug,upstream_identifier,updated_at)
                VALUES(910201,'00000000-0000-0000-0000-000000000899',910004,'Empty Tour','v4-empty-tour','v4-empty-tour','2026-01-01');
                """
            );
            var tour = await GetJson(TourPath(899));
            tour["show_count"]!.GetValue<int>().Should().Be(0);
            tour["shows"]!.AsArray().Should().BeEmpty();
            (await GetJson(ArtistPath(20) + "/tours")).AsArray().Should().BeEmpty();
            (await GetJson(ArtistPath(20) + "/eras")).AsArray().Should().BeEmpty();
            (await GetJson(SourcePath(24) + "/reviews")).AsArray().Should().BeEmpty();
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM tours WHERE id=910201");
        }
    }

    [Test]
    public async Task Artist_eras_are_ordered_embedded_metadata_and_revalidate_name_only_corrections()
    {
        var path = BrowseArtistPath + "/eras";
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO eras(id,artist_id,"order",name,updated_at)
                VALUES(910201,910004,2,'Later','2026-01-01'),(910202,910004,1,'Earlier','2026-01-01');
                UPDATE shows SET era_id=910202 WHERE id=910004;
                """
            );
            var eras = (await GetJson(path)).AsArray();
            eras.Select(era => era!["name"]!.GetValue<string>()).Should().Equal("Earlier", "Later");
            JsonNode.DeepEquals(eras[0], (await GetJson(ShowPath(4)))["era"]).Should().BeTrue();
            eras[0]!.AsObject().Select(field => field.Key).Should().BeEquivalentTo("order", "name");
            using var initial = await http.GetAsync(path);
            var etag = initial.Headers.ETag!.ToString();
            await connection.ExecuteAsync("UPDATE eras SET name='Corrected era' WHERE id=910202");
            using var changed = await Conditional(path, etag);
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
            JsonNode.Parse(await changed.Content.ReadAsStringAsync())![0]!["name"]!
                .GetValue<string>()
                .Should()
                .Be("Corrected era");
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM eras WHERE id IN(910201,910202)");
        }
    }

    [Test]
    public async Task Source_reviews_keep_typed_ownership_nullable_fields_and_revalidate_body_only_corrections()
    {
        var path = SourcePath(24) + "/reviews";
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO source_reviews(id,uuid,source_id,rating,title,review,author,updated_at)
                VALUES(910201,'00000000-0000-0000-0000-000000000901',910004,5,'Great','First review','Reviewer','2026-01-02'),
                      (910202,'00000000-0000-0000-0000-000000000902',910004,NULL,NULL,'Second review',NULL,'2026-01-02'),
                      (910203,'00000000-0000-0000-0000-000000000903',910004,3,NULL,'Older review',NULL,'2026-01-01'),
                      (910204,'00000000-0000-0000-0000-000000000904',910003,1,NULL,'Other source',NULL,'2026-01-03');
                """
            );
            var reviews = (await GetJson(path)).AsArray();
            reviews
                .Select(review => review!["id"]!.GetValue<string>())
                .Should()
                .Equal(
                    CatalogId("source_review", 901),
                    CatalogId("source_review", 902),
                    CatalogId("source_review", 903)
                );
            reviews
                .Should()
                .OnlyContain(review =>
                    review!["source_id"]!.GetValue<string>() == CatalogId("source", 24)
                );
            reviews[0]!["rating"]!.GetValue<int>().Should().Be(5);
            reviews[1]!["rating"].Should().BeNull();
            reviews[1]!["title"].Should().BeNull();
            reviews[1]!["author"].Should().BeNull();
            AssertSchemaFields(
                await GetJson("/api-docs/v4/swagger.json"),
                "SourceReviewResource",
                reviews[0]!
            );
            using var initial = await http.GetAsync(path);
            var etag = initial.Headers.ETag!.ToString();
            using var unchanged = await Conditional(path, etag);
            unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);
            await connection.ExecuteAsync(
                "UPDATE source_reviews SET review='Corrected body' WHERE id=910201"
            );
            using var changed = await Conditional(path, etag);
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
            var current = JsonNode.Parse(await changed.Content.ReadAsStringAsync())![0]!;
            current["review"]!.GetValue<string>().Should().Be("Corrected body");
            current["updated_at"]!
                .ToJsonString()
                .Should()
                .Be(reviews[0]!["updated_at"]!.ToJsonString());
        }
        finally
        {
            await connection.ExecuteAsync(
                "DELETE FROM source_reviews WHERE id BETWEEN 910201 AND 910204"
            );
        }
    }

    [Test]
    public async Task Navigation_routes_distinguish_invalid_ids_from_unknown_owners()
    {
        foreach (
            var path in new[]
            {
                ArtistPath(999) + "/tours",
                ArtistPath(999) + "/eras",
                TourPath(999),
                SourcePath(999) + "/reviews",
            }
        )
        {
            using var missing = await http.GetAsync(path);
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound, path);
        }
        foreach (
            var path in new[]
            {
                "/api/v4/tours/" + ShowId(4),
                "/api/v4/artists/" + ShowId(4) + "/eras",
                "/api/v4/sources/" + ShowId(4) + "/reviews",
            }
        )
        {
            using var invalid = await http.GetAsync(path);
            invalid.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, path);
            invalid.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
    }

    [Test]
    public async Task Navigation_reads_recover_after_uncached_dependency_failure()
    {
        var paths = new[]
        {
            BrowseArtistPath + "/tours",
            BrowseArtistPath + "/eras",
            TourPath(84),
            SourcePath(24) + "/reviews",
        };
        var previous = DbService.ConnStr;
        try
        {
            DbService.ConnStr =
                "Host=127.0.0.1;Port=1;Database=unavailable;Username=relisten;Timeout=1";
            foreach (var path in paths)
            {
                using var failed = await http.GetAsync(path);
                failed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, path);
                failed.Headers.CacheControl!.NoStore.Should().BeTrue();
            }
        }
        finally
        {
            DbService.ConnStr = previous;
        }
        foreach (var path in paths)
        {
            using var recovered = await http.GetAsync(path);
            recovered.StatusCode.Should().Be(HttpStatusCode.OK, path);
        }
    }
}
