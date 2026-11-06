using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Api.V4;

namespace RelistenApiTests.V4;

public sealed partial class TestV4CatalogHttp
{
    [Test]
    public async Task Artist_feature_mask_serializes_strings_and_revalidates_changes()
    {
        var columns = new[]
        {
            "descriptions",
            "eras",
            "multiple_sources",
            "reviews",
            "ratings",
            "tours",
            "taper_notes",
            "source_information",
            "sets",
            "per_show_venues",
            "per_source_venues",
            "venue_coords",
            "songs",
            "years",
            "track_md5s",
            "review_titles",
            "jam_charts",
            "setlist_data_incomplete",
            "track_names",
            "venue_past_names",
            "reviews_have_ratings",
            "track_durations",
            "can_have_flac",
            "norm_catalogue",
        };
        await using var connection = await MigratedTestDatabase.OpenAsync();
        var original = await connection.QuerySingleAsync<string>(
            "SELECT row_to_json(f)::text FROM features f WHERE artist_id=910004"
        );
        var reset =
            "UPDATE features SET "
            + string.Join(",", columns.Select(column => column + "=false"))
            + " WHERE artist_id=910004";
        try
        {
            await connection.ExecuteAsync(reset);
            using var zero = await http.GetAsync(BrowseArtistPath);
            zero.EnsureSuccessStatusCode();
            JsonNode.Parse(await zero.Content.ReadAsStringAsync())!["features"]!
                .GetValue<string>()
                .Should()
                .Be("0");
            var etag = zero.Headers.ETag!.ToString();
            await connection.ExecuteAsync(
                "UPDATE features SET descriptions=true,track_durations=true,norm_catalogue=true WHERE artist_id=910004"
            );
            using var changed = await Conditional(BrowseArtistPath, etag);
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
            JsonNode.Parse(await changed.Content.ReadAsStringAsync())!["features"]!
                .GetValue<string>()
                .Should()
                .Be("10485761");

            await connection.ExecuteAsync(
                "UPDATE features SET "
                    + string.Join(",", columns.Select(column => column + "=true"))
                    + " WHERE artist_id=910004"
            );
            (await GetJson(BrowseArtistPath))["features"]!
                .GetValue<string>()
                .Should()
                .Be("16777215");

            var document = await GetJson("/api-docs/v4/swagger.json");
            foreach (var resource in new[] { "ArtistResource", "RankedArtistResource" })
            {
                var schema = document["components"]!["schemas"]![resource]!["properties"]![
                    "features"
                ]!;
                schema["type"]!.GetValue<string>().Should().Be("string");
                schema["format"].Should().BeNull();
                schema["pattern"]!.GetValue<string>().Should().Be("^(0|[1-9][0-9]*)$");
                schema["enum"].Should().BeNull("combined masks must be allowed");
                schema["description"]!.GetValue<string>().Should().Contain("Normalization=8388608");
                schema["x-feature-flags"]!
                    .AsObject()
                    .ToDictionary(flag => flag.Key, flag => flag.Value!.GetValue<string>())
                    .Should()
                    .BeEquivalentTo(
                        Enum.GetValues<ArtistFeatures>()
                            .Where(flag => flag != ArtistFeatures.None)
                            .ToDictionary(flag => flag.ToString(), flag => flag.ToString("D"))
                    );
            }
        }
        finally
        {
            var columnList = string.Join(",", columns);
            await connection.ExecuteAsync(
                $"UPDATE features SET ({columnList})=(SELECT {columnList} FROM json_populate_record(NULL::features,CAST(@original AS json))) WHERE artist_id=910004",
                new { original }
            );
        }
    }

    [Test]
    public async Task Artist_providers_keep_typed_names_and_per_artist_identifiers()
    {
        var providers = new[]
        {
            "archive.org",
            "phish.in",
            "phish.net",
            "setlist.fm",
            "phantasytour.com",
            "jerrygarcia.com",
            "panicstream.com",
            "local",
        };
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO upstream_sources(id,name) VALUES(910011,'archive.org');
                INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier)
                VALUES(910004,910011,'artist-collection');
                UPDATE upstream_sources SET name='archive.org' WHERE id=910010;
                """
            );
            var assignments = (await GetJson(BrowseArtistPath))["upstream_sources"]!.AsArray();
            assignments.Should().HaveCount(2);
            assignments
                .Should()
                .OnlyContain(item => item!["provider"]!.GetValue<string>() == "archive.org");
            assignments
                .Select(item => item!["upstream_identifier"]!.GetValue<string>())
                .Should()
                .BeEquivalentTo("matched-upstream", "artist-collection");
            foreach (var assignment in assignments)
            {
                assignment!
                    .AsObject()
                    .Select(field => field.Key)
                    .Should()
                    .BeEquivalentTo("provider", "upstream_identifier");
            }
            EnumValues(
                    await GetJson("/api-docs/v4/swagger.json"),
                    "ArtistProviderResource",
                    "provider"
                )
                .Should()
                .BeEquivalentTo(providers);
        }
        finally
        {
            await connection.ExecuteAsync(
                """
                DELETE FROM artists_upstream_sources WHERE artist_id=910004 AND upstream_source_id=910011;
                DELETE FROM upstream_sources WHERE id=910011;
                UPDATE upstream_sources SET name='review.provider' WHERE id=910010;
                """
            );
        }
    }

    [TestCase(910001, 11, "venues")]
    [TestCase(925003, 303, "songs")]
    public async Task Enabling_normalization_returns_an_empty_canonical_list_without_imported_fallbacks(
        long artistId,
        int suffix,
        string collection
    )
    {
        var path = ArtistPath(suffix) + "/" + collection;
        using var initial = await http.GetAsync(path);
        initial.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await initial.Content.ReadAsStringAsync())!.AsArray().Should().NotBeEmpty();
        var etag = initial.Headers.ETag!.ToString();
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                "UPDATE features SET norm_catalogue=true WHERE artist_id=@artistId",
                new { artistId }
            );
            using var changed = await Conditional(path, etag);
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
            JsonNode.Parse(await changed.Content.ReadAsStringAsync())!.AsArray().Should().BeEmpty();
        }
        finally
        {
            await connection.ExecuteAsync(
                "UPDATE features SET norm_catalogue=false WHERE artist_id=@artistId",
                new { artistId }
            );
        }
        (await GetJson(path)).AsArray().Should().NotBeEmpty();
    }
}
