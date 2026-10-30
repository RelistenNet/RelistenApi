using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;

namespace RelistenApiTests.V4;

// The snapshot replaces artist-owned collection membership and patches shared metadata.
// It does not replace recording details or delete globally shared venue/song entities.
public sealed partial class TestV4CatalogHttp
{
    [Test]
    public async Task Artist_snapshot_matches_navigation_collections_without_recording_details()
    {
        foreach (var artistPath in new[] { BrowseArtistPath, SongArtistPath })
        {
            var snapshot = await GetJson(artistPath + "/catalog");
            JsonNode.DeepEquals(snapshot["artist"], await GetJson(artistPath)).Should().BeTrue();
            foreach (
                var (field, route) in new[]
                {
                    ("years", "/years"),
                    ("shows", "/shows"),
                    ("tours", "/tours"),
                    ("eras", "/eras"),
                    ("imported_venues", "/imported-venues"),
                    ("imported_songs", "/imported-songs"),
                    ("venue_entries", "/venues"),
                    ("song_entries", "/songs"),
                }
            )
            {
                JsonNode
                    .DeepEquals(snapshot[field], await GetJson(artistPath + route))
                    .Should()
                    .BeTrue(field);
            }
            snapshot["shows"]!
                .AsArray()
                .Should()
                .OnlyContain(show => !show!.AsObject().ContainsKey("sources"));
            snapshot["songs"]!
                .AsArray()
                .Select(song => song!.ToJsonString())
                .Should()
                .BeEquivalentTo(
                    snapshot["song_entries"]!
                        .AsArray()
                        .Where(entry => entry!["song"] is not null)
                        .Select(entry => entry!["song"]!.ToJsonString())
                );
            foreach (var relationship in snapshot["artist_venues"]!.AsArray())
            {
                JsonNode
                    .DeepEquals(
                        relationship,
                        ResourceMetadata(
                            await GetJson(
                                "/api/v4/artist-venues/" + relationship!["id"]!.GetValue<string>()
                            )
                        )
                    )
                    .Should()
                    .BeTrue();
                snapshot["venues"]!
                    .AsArray()
                    .Should()
                    .Contain(venue => JsonNode.DeepEquals(venue, relationship["venue"]));
            }
            AssertSchemaFields(
                await GetJson("/api-docs/v4/swagger.json"),
                "ArtistCatalogResource",
                snapshot
            );
        }
        var songs = (await GetJson(SongArtistPath + "/catalog"))["songs"]!.AsArray();
        songs.Single(song => song!["id"]!.GetValue<string>() == "song_00000103")![
            "matched_show_count"
        ]!
            .GetValue<int>()
            .Should()
            .Be(0);
    }

    [Test]
    public async Task Artist_snapshot_keeps_complete_imported_inventories_without_approved_normalization()
    {
        foreach (var suffix in new[] { 11, 12, 302, 303 })
        {
            var path = ArtistPath(suffix);
            var snapshot = await GetJson(path + "/catalog");
            foreach (var field in new[] { "artist_venues", "venues", "songs" })
            {
                snapshot[field]!.AsArray().Should().BeEmpty();
            }
            JsonNode
                .DeepEquals(snapshot["imported_venues"], await GetJson(path + "/imported-venues"))
                .Should()
                .BeTrue();
            JsonNode
                .DeepEquals(snapshot["imported_songs"], await GetJson(path + "/imported-songs"))
                .Should()
                .BeTrue();
            snapshot["venue_entries"]!
                .AsArray()
                .Should()
                .OnlyContain(entry => entry!["kind"]!.GetValue<string>() == "imported_venue");
            snapshot["song_entries"]!
                .AsArray()
                .Should()
                .OnlyContain(entry => entry!["kind"]!.GetValue<string>() == "imported_song");
            snapshot["shows"]!
                .AsArray()
                .Should()
                .OnlyContain(show => show!["artist_venue"] == null);
        }
    }

    [Test]
    public async Task Artist_snapshot_distinguishes_empty_catalogs_unknown_artists_and_invalid_ids()
    {
        var empty = await GetJson(ArtistPath(20) + "/catalog");
        empty["artist"]!["id"]!.GetValue<string>().Should().Be(CatalogId("artist", 20));
        foreach (var field in empty.AsObject().Where(field => field.Key != "artist"))
        {
            field.Value!.AsArray().Should().BeEmpty(field.Key);
        }
        using var unknown = await http.GetAsync(ArtistPath(999) + "/catalog");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var invalid = await http.GetAsync("/api/v4/artists/" + ShowId(4) + "/catalog");
        invalid.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        invalid.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Test]
    public async Task Artist_snapshot_revalidates_normalization_changes_and_keeps_zero_count_shared_patches()
    {
        var path = BrowseArtistPath + "/catalog";
        var original = await GetJson(path);
        var relationshipId = original["artist_venues"]![0]!["id"]!.GetValue<string>();
        using var initial = await http.GetAsync(path);
        var etag = initial.Headers.ETag!.ToString();
        using var unchanged = await Conditional(path, etag);
        unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await ChangeNormalization(
                connection,
                "DELETE FROM norm.show_venues WHERE show_uuid='00000000-0000-0000-0000-000000000004'"
            );
            using var changed = await Conditional(path, etag);
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
            changed.Headers.ETag!.ToString().Should().NotBe(etag);
            var snapshot = JsonNode.Parse(await changed.Content.ReadAsStringAsync())!;
            snapshot["artist"]!["updated_at"]!
                .ToJsonString()
                .Should()
                .Be(original["artist"]!["updated_at"]!.ToJsonString());
            snapshot["venue_entries"]!
                .AsArray()
                .Should()
                .OnlyContain(entry => entry!["kind"]!.GetValue<string>() == "imported_venue");
            var retained = snapshot["artist_venues"]!
                .AsArray()
                .Single(item => item!["id"]!.GetValue<string>() == relationshipId)!;
            retained["artist_show_count"]!.GetValue<int>().Should().Be(0);
            retained["venue"]!["global_show_count"]!.GetValue<int>().Should().Be(1);
            snapshot["venues"]!
                .AsArray()
                .Should()
                .Contain(venue => JsonNode.DeepEquals(venue, retained["venue"]));
            snapshot["shows"]!
                .AsArray()
                .Should()
                .HaveCount(2)
                .And.OnlyContain(show => show!["artist_venue"] == null);
            (await GetJson(CanonicalVenuePath))["shows"]!.AsArray().Should().ContainSingle();
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
    }

    [Test]
    public async Task Artist_snapshot_removes_deleted_imported_membership_without_changing_artist_timestamp()
    {
        var path = SongArtistPath + "/catalog";
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO setlist_songs(id,uuid,artist_id,name,slug,upstream_identifier,updated_at)
                VALUES(925099,'00000000-0000-0000-0000-000000000399',925001,'Temporary import','snapshot-temporary','snapshot-temporary','2026-01-01');
                """
            );
            using var initial = await http.GetAsync(path);
            var original = JsonNode.Parse(await initial.Content.ReadAsStringAsync())!;
            original["imported_songs"]!
                .AsArray()
                .Should()
                .Contain(song =>
                    song!["id"]!.GetValue<string>() == CatalogId("imported_song", 399)
                );
            await connection.ExecuteAsync("DELETE FROM setlist_songs WHERE id=925099");
            using var changed = await Conditional(path, initial.Headers.ETag!.ToString());
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
            var snapshot = JsonNode.Parse(await changed.Content.ReadAsStringAsync())!;
            snapshot["artist"]!["updated_at"]!
                .ToJsonString()
                .Should()
                .Be(original["artist"]!["updated_at"]!.ToJsonString());
            snapshot["imported_songs"]!
                .AsArray()
                .Should()
                .NotContain(song =>
                    song!["id"]!.GetValue<string>() == CatalogId("imported_song", 399)
                );
            snapshot["song_entries"]!
                .AsArray()
                .Select(entry => entry!["imported_song"]?["id"]?.GetValue<string>())
                .Should()
                .NotContain(CatalogId("imported_song", 399));
            JsonNode.DeepEquals(snapshot["songs"], original["songs"]).Should().BeTrue();
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM setlist_songs WHERE id=925099");
        }
    }
}
