using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;

namespace RelistenApiTests.V4;

public sealed partial class TestV4CatalogHttp
{
    [Test]
    public async Task Imported_song_list_revalidates_deleted_membership_without_an_artist_update()
    {
        var path = SongArtistPath + "/imported-songs";
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO setlist_songs(id,uuid,artist_id,name,slug,upstream_identifier,updated_at)
                VALUES(925099,'00000000-0000-0000-0000-000000000399',925001,'Temporary import','list-temporary','list-temporary','2026-01-01');
                """
            );
            var artistUpdatedAt = (await GetJson(SongArtistPath))["updated_at"]!.ToJsonString();
            using var initial = await http.GetAsync(path);
            var original = JsonNode.Parse(await initial.Content.ReadAsStringAsync())!;
            original
                .AsArray()
                .Should()
                .Contain(song =>
                    song!["id"]!.GetValue<string>() == CatalogId("imported_song", 399)
                );
            await connection.ExecuteAsync("DELETE FROM setlist_songs WHERE id=925099");
            using var changed = await Conditional(path, initial.Headers.ETag!.ToString());
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
            var updated = JsonNode.Parse(await changed.Content.ReadAsStringAsync())!;
            (await GetJson(SongArtistPath))["updated_at"]!
                .ToJsonString()
                .Should()
                .Be(artistUpdatedAt);
            updated
                .AsArray()
                .Should()
                .NotContain(song =>
                    song!["id"]!.GetValue<string>() == CatalogId("imported_song", 399)
                );
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM setlist_songs WHERE id=925099");
        }
    }
}
