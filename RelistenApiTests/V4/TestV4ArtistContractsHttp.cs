using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;

namespace RelistenApiTests.V4;

public sealed partial class TestV4CatalogHttp
{
    [Test]
    public async Task Artist_feature_mask_preserves_each_published_bit_and_revalidates_changes()
    {
        // Fixed wire values protect persisted client masks from accidental renumbering.
        var flags = new (string Column, ulong Value)[]
        {
            ("descriptions", 1UL),
            ("eras", 2UL),
            ("multiple_sources", 4UL),
            ("reviews", 8UL),
            ("ratings", 16UL),
            ("tours", 32UL),
            ("taper_notes", 64UL),
            ("source_information", 128UL),
            ("sets", 256UL),
            ("per_show_venues", 512UL),
            ("per_source_venues", 1024UL),
            ("venue_coords", 2048UL),
            ("songs", 4096UL),
            ("years", 8192UL),
            ("track_md5s", 16384UL),
            ("review_titles", 32768UL),
            ("jam_charts", 65536UL),
            ("setlist_data_incomplete", 131072UL),
            ("track_names", 262144UL),
            ("venue_past_names", 524288UL),
            ("reviews_have_ratings", 1048576UL),
            ("track_durations", 2097152UL),
            ("can_have_flac", 4194304UL),
            ("norm_catalogue", 8388608UL),
        };
        await using var connection = await MigratedTestDatabase.OpenAsync();
        var original = await connection.QuerySingleAsync<string>(
            "SELECT row_to_json(f)::text FROM features f WHERE artist_id=910004"
        );
        var columns = string.Join(",", flags.Select(flag => flag.Column));
        var reset =
            "UPDATE features SET "
            + string.Join(",", flags.Select(flag => flag.Column + "=false"))
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
            foreach (var (column, value) in flags)
            {
                await connection.ExecuteAsync(reset);
                await connection.ExecuteAsync(
                    $"UPDATE features SET {column}=true WHERE artist_id=910004"
                );
                using var changed = await Conditional(BrowseArtistPath, etag);
                changed.StatusCode.Should().Be(HttpStatusCode.OK);
                JsonNode.Parse(await changed.Content.ReadAsStringAsync())!["features"]!
                    .GetValue<string>()
                    .Should()
                    .Be(value.ToString(CultureInfo.InvariantCulture), column);
            }
            await connection.ExecuteAsync(
                "UPDATE features SET "
                    + string.Join(",", flags.Select(flag => flag.Column + "=true"))
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
            }
        }
        finally
        {
            await connection.ExecuteAsync(
                $"UPDATE features SET ({columns})=(SELECT {columns} FROM json_populate_record(NULL::features,CAST(@original AS json))) WHERE artist_id=910004",
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
                "INSERT INTO upstream_sources(id,name) VALUES(910011,'archive.org'); INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier) VALUES(910004,910011,'artist-collection')"
            );
            foreach (var provider in providers)
            {
                await connection.ExecuteAsync(
                    "UPDATE upstream_sources SET name=@provider WHERE id=910010",
                    new { provider }
                );
                var assignments = (await GetJson(BrowseArtistPath))["upstream_sources"]!.AsArray();
                assignments.Should().HaveCount(2);
                var assigned = assignments.Single(item =>
                    item!["upstream_identifier"]!.GetValue<string>() == "matched-upstream"
                )!;
                assigned["provider"]!.GetValue<string>().Should().Be(provider);
                assigned
                    .AsObject()
                    .Select(field => field.Key)
                    .Should()
                    .BeEquivalentTo("provider", "upstream_identifier");
                assignments
                    .Should()
                    .Contain(item =>
                        item!["provider"]!.GetValue<string>() == "archive.org"
                        && item["upstream_identifier"]!.GetValue<string>() == "artist-collection"
                    );
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
                "DELETE FROM artists_upstream_sources WHERE artist_id=910004 AND upstream_source_id=910011; DELETE FROM upstream_sources WHERE id=910011; UPDATE upstream_sources SET name='review.provider' WHERE id=910010"
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
        (await GetJson(path)).AsArray().Should().NotBeEmpty();
        using var initial = await http.GetAsync(path);
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
