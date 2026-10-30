using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Relisten.Normalization;

namespace RelistenApiTests.V4;

// Relationship identity and counts must mean the same thing wherever mobile encounters
// them. These tests cross HTTP routes and stored rows rather than deriving totals in a client.
public sealed partial class TestV4CatalogHttp
{
    [Test]
    public async Task Artist_venue_identity_and_counts_are_shared_across_years_and_venue_routes()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        var originalId = await StoredArtistVenueId(connection);
        try
        {
            await ChangeNormalization(
                connection,
                """
                INSERT INTO norm.show_venues(show_uuid,venue_id,basis,author_type,author_ref,run_uuid)
                VALUES('00000000-0000-0000-0000-000000000010','venue_00000001','ruling','importer','v4-review','00000000-0000-0000-0000-000000000099');
                """
            );

            var show = await GetJson(ShowPath(4));
            show.AsObject().ContainsKey("venue").Should().BeFalse();
            var artistVenue = show["artist_venue"]!;
            artistVenue["id"]!.GetValue<string>().Should().Be(originalId);
            ArtistVenueId.Parse(originalId).Value.Should().Be(originalId);
            artistVenue["artist_id"]!.GetValue<string>().Should().Be(BrowseArtistId);
            artistVenue["artist_show_count"]!.GetValue<int>().Should().Be(2);
            artistVenue["venue"]!["global_show_count"]!.GetValue<int>().Should().Be(3);

            var detail = await GetJson(BrowseArtistPath + "/venues/venue_00000001");
            JsonNode.DeepEquals(artistVenue, ResourceMetadata(detail)).Should().BeTrue();
            var entries = (await GetJson(BrowseArtistPath + "/venues")).AsArray();
            entries.Should().ContainSingle();
            JsonNode.DeepEquals(artistVenue, entries[0]!["artist_venue"]).Should().BeTrue();
            var global = await GetJson(CanonicalVenuePath);
            JsonNode.DeepEquals(ResourceMetadata(global), artistVenue["venue"]).Should().BeTrue();

            var year = await GetJson(BrowseArtistPath + "/years/by-number/1995");
            year["shows"]!.AsArray().Should().ContainSingle();
            JsonNode.DeepEquals(artistVenue, year["shows"]![0]!["artist_venue"]).Should().BeTrue();
            var earlier = await GetJson(ShowPath(10));
            JsonNode.DeepEquals(artistVenue, earlier["artist_venue"]).Should().BeTrue();
            var filtered = detail["shows"]!.AsArray();
            filtered.Should().HaveCount(2);
            foreach (var item in filtered)
            {
                JsonNode.DeepEquals(artistVenue, item!["artist_venue"]).Should().BeTrue();
            }

            var document = await GetJson("/api-docs/v4/swagger.json");
            AssertSchemaFields(document, "ArtistVenueResource", artistVenue);
            var id = document["components"]!["schemas"]!["ArtistVenueResource"]!["properties"]![
                "id"
            ]!;
            id["type"]!.GetValue<string>().Should().Be("string");
            id["format"]?.GetValue<string>().Should().NotBe("uuid");
            id["pattern"]!.GetValue<string>().Should().Be("^artist_venue_[0-9a-hjkmnp-tv-z]{8}$");
        }
        finally
        {
            await ChangeNormalization(
                connection,
                "DELETE FROM norm.show_venues WHERE show_uuid='00000000-0000-0000-0000-000000000010'"
            );
        }
    }

    [Test]
    public async Task Stored_count_corrections_revalidate_every_embedding_without_recording_updates()
    {
        var paths = new[]
        {
            ShowPath(4),
            BrowseArtistPath + "/shows",
            BrowseArtistPath + "/years/by-number/1995",
            BrowseArtistPath + "/venues",
            BrowseArtistPath + "/venues/venue_00000001",
            CanonicalVenuePath,
            ImportedVenuePath(74),
        };
        var validators = new Dictionary<string, string>();
        foreach (var path in paths)
        {
            using var response = await http.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            validators[path] = response.Headers.ETag!.ToString();
            using var unchanged = await Conditional(path, validators[path]);
            unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);
        }
        var before = await GetJson(ShowPath(4));
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            // Simulate a materialized count correction. HTTP must use the stored value,
            // rather than silently deriving another total from the currently returned shows.
            await connection.ExecuteAsync(
                """
                UPDATE norm.artist_venues SET artist_show_count=17,updated_at=clock_timestamp()
                WHERE artist_uuid='00000000-0000-0000-0000-000000000014' AND venue_id='venue_00000001'
                """
            );
            foreach (var path in paths)
            {
                using var changed = await Conditional(path, validators[path]);
                changed.StatusCode.Should().Be(HttpStatusCode.OK, path);
                changed.Headers.ETag!.ToString().Should().NotBe(validators[path], path);
            }

            var show = await GetJson(ShowPath(4));
            show["updated_at"]!.ToJsonString().Should().Be(before["updated_at"]!.ToJsonString());
            show["artist_venue"]!["id"]!
                .GetValue<string>()
                .Should()
                .Be(before["artist_venue"]!["id"]!.GetValue<string>());
            show["artist_venue"]!["artist_show_count"]!.GetValue<int>().Should().Be(17);
            show["artist_venue"]!["venue"]!["global_show_count"]!.GetValue<int>().Should().Be(18);
            (await GetJson(CanonicalVenuePath))["shows"]!.AsArray().Should().HaveCount(2);
        }
        finally
        {
            await connection.ExecuteAsync("SELECT norm.refresh_artist_venues()");
        }
    }

    [Test]
    public async Task Approval_changes_global_counts_and_validators_without_mutating_relationships()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        var storedBefore = (
            await connection.QueryAsync<string>(
                "SELECT av::text FROM norm.artist_venues av WHERE venue_id='venue_00000001' ORDER BY id"
            )
        ).ToArray();
        using var initial = await http.GetAsync(ShowPath(4));
        initial.StatusCode.Should().Be(HttpStatusCode.OK);
        var etag = initial.Headers.ETag!.ToString();
        (await GetJson(CanonicalVenuePath))["global_show_count"]!.GetValue<int>().Should().Be(2);
        try
        {
            await connection.ExecuteAsync(
                "UPDATE features SET norm_catalogue=true WHERE artist_id=910002"
            );

            using var changed = await Conditional(ShowPath(4), etag);
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
            changed.Headers.ETag!.ToString().Should().NotBe(etag);
            var show = JsonNode.Parse(await changed.Content.ReadAsStringAsync())!;
            show["artist_venue"]!["artist_show_count"]!.GetValue<int>().Should().Be(1);
            show["artist_venue"]!["venue"]!["global_show_count"]!.GetValue<int>().Should().Be(3);
            var filtered = (await GetJson(BrowseArtistPath + "/venues/venue_00000001"))[
                "shows"
            ]!.AsArray();
            filtered.Should().ContainSingle();
            filtered[0]!["artist_venue"]!["venue"]!["global_show_count"]!
                .GetValue<int>()
                .Should()
                .Be(3);
            (await GetJson(CanonicalVenuePath))["shows"]!.AsArray().Should().HaveCount(3);
            (
                await connection.QueryAsync<string>(
                    "SELECT av::text FROM norm.artist_venues av WHERE venue_id='venue_00000001' ORDER BY id"
                )
            )
                .Should()
                .Equal(storedBefore);
        }
        finally
        {
            await connection.ExecuteAsync(
                "UPDATE features SET norm_catalogue=false WHERE artist_id=910002"
            );
        }
    }

    [Test]
    public async Task Missing_materialized_pair_preserves_imported_fallback_without_refreshing_on_read()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                """
                DELETE FROM norm.artist_venues
                WHERE artist_uuid='00000000-0000-0000-0000-000000000014' AND venue_id='venue_00000001'
                """
            );
            var show = await GetJson(ShowPath(4));
            show["artist_venue"].Should().BeNull();
            show["imported_venue"]!["name"]!
                .GetValue<string>()
                .Should()
                .Be("Imported Civic Center");
            var entries = (await GetJson(BrowseArtistPath + "/venues")).AsArray();
            entries.Should().ContainSingle();
            entries[0]!["kind"]!.GetValue<string>().Should().Be("imported_venue");
            entries[0]!["imported_venue"]!["show_count"]!.GetValue<int>().Should().Be(2);
            using var missing = await http.GetAsync(BrowseArtistPath + "/venues/venue_00000001");
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await GetJson(CanonicalVenuePath))["global_show_count"]!
                .GetValue<int>()
                .Should()
                .Be(1);
            (
                await connection.QuerySingleAsync<int>(
                    """
                    SELECT count(*)::int FROM norm.artist_venues
                    WHERE artist_uuid='00000000-0000-0000-0000-000000000014' AND venue_id='venue_00000001'
                    """
                )
            ).Should().Be(0);
        }
        finally
        {
            await connection.ExecuteAsync("SELECT norm.refresh_artist_venues()");
        }
    }

    [Test]
    public async Task Zero_count_pair_keeps_detail_identity_but_leaves_positive_browse_membership()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        var originalId = await StoredArtistVenueId(connection);
        try
        {
            await ChangeNormalization(
                connection,
                "DELETE FROM norm.show_venues WHERE show_uuid='00000000-0000-0000-0000-000000000004'"
            );
            var detail = await GetJson(BrowseArtistPath + "/venues/venue_00000001");
            detail["id"]!.GetValue<string>().Should().Be(originalId);
            detail["artist_show_count"]!.GetValue<int>().Should().Be(0);
            detail["shows"]!.AsArray().Should().BeEmpty();
            JsonNode
                .DeepEquals(detail, await GetJson("/api/v4/artist-venues/" + originalId))
                .Should()
                .BeTrue();
            detail["venue"]!["global_show_count"]!.GetValue<int>().Should().Be(1);
            (await GetJson(BrowseArtistPath + "/venues"))
                .AsArray()
                .Should()
                .OnlyContain(entry => entry!["kind"]!.GetValue<string>() == "imported_venue");
            (await GetJson(ShowPath(4)))["artist_venue"].Should().BeNull();
        }
        finally
        {
            await ChangeNormalization(
                connection,
                """
                INSERT INTO norm.show_venues(show_uuid,venue_id,basis,author_type,author_ref,run_uuid)
                VALUES('00000000-0000-0000-0000-000000000004','venue_00000001','evidence','importer','v4-review','00000000-0000-0000-0000-000000000099');
                """
            );
        }
        (await GetJson(ShowPath(4)))["artist_venue"]!["id"]!
            .GetValue<string>()
            .Should()
            .Be(originalId);
    }

    private static Task<string> StoredArtistVenueId(Npgsql.NpgsqlConnection connection) =>
        connection.QuerySingleAsync<string>(
            """
            SELECT id FROM norm.artist_venues
            WHERE artist_uuid='00000000-0000-0000-0000-000000000014' AND venue_id='venue_00000001'
            """
        );
}
