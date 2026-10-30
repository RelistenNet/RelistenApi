using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Relisten;

namespace RelistenApiTests.V4;

// Recording matches identify playable files. Imported setlist evidence retains its
// own show collection and must never invent a source or track assignment.
public sealed partial class TestV4CatalogHttp
{
    private const string DiscoverySongPath = "/api/v4/songs/song_00000101";
    private static string SongArtistPath => ArtistPath(301);

    private static string ImportedSongPath(int artistSuffix, int songSuffix) =>
        ArtistPath(artistSuffix) + "/imported-songs/" + CatalogId("imported_song", songSuffix);

    [Test]
    public async Task Song_browse_uses_evidence_mapping_and_preserves_unmatched_same_name_and_zero_match_songs()
    {
        var entries = (await GetJson(SongArtistPath + "/songs")).AsArray();
        entries.Should().HaveCount(5);
        var songs = entries.Where(entry => entry!["kind"]!.GetValue<string>() == "song").ToArray();
        songs
            .Select(entry => entry!["song"]!["id"]!.GetValue<string>())
            .Should()
            .BeEquivalentTo("song_00000101", "song_00000102", "song_00000103");
        songs.Should().OnlyContain(entry => entry!["imported_song"] == null);
        var raw = entries
            .Where(entry => entry!["kind"]!.GetValue<string>() == "imported_song")
            .ToArray();
        raw.Select(entry => entry!["imported_song"]!["id"]!.GetValue<string>())
            .Should()
            .BeEquivalentTo(CatalogId("imported_song", 352), CatalogId("imported_song", 353));
        raw.Should().OnlyContain(entry => entry!["song"] == null);
        raw.Single(entry =>
            entry!["imported_song"]!["id"]!.GetValue<string>() == CatalogId("imported_song", 352)
        )!["imported_song"]!["name"]!
            .GetValue<string>()
            .Should()
            .Be("Scarlet Begonias");

        var detail = await GetJson(DiscoverySongPath);
        detail["matched_track_count"]!.GetValue<int>().Should().Be(3);
        detail["matched_show_count"]!.GetValue<int>().Should().Be(2);
        detail["original_artist"]!.GetValue<string>().Should().Be("Grateful Dead");
        detail["aliases"]!
            .AsArray()
            .Select(alias => alias!.GetValue<string>())
            .Should()
            .Contain(["Scarlet", "Old Scarlet"]);
        JsonNode
            .DeepEquals(
                songs.Single(entry =>
                    entry!["song"]!["id"]!.GetValue<string>() == "song_00000101"
                )!["song"],
                detail
            )
            .Should()
            .BeTrue();
        JsonNode.DeepEquals(await GetJson("/api/v4/songs/song_00000104"), detail).Should().BeTrue();

        var empty = await GetJson("/api/v4/songs/song_00000103");
        empty["matched_track_count"]!.GetValue<int>().Should().Be(0);
        empty["matched_show_count"]!.GetValue<int>().Should().Be(0);
        (await GetJson("/api/v4/songs/song_00000103/tracks")).AsArray().Should().BeEmpty();
        (await GetJson("/api/v4/songs/song_00000103/shows")).AsArray().Should().BeEmpty();

        var document = await GetJson("/api-docs/v4/swagger.json");
        AssertSchemaFields(document, "SongDetailResource", detail);
        AssertSchemaFields(document, "ArtistSongEntryResource", entries[0]!);
        EnumValues(document, "ArtistSongEntryResource", "kind")
            .Should()
            .BeEquivalentTo("song", "imported_song");
    }

    [Test]
    public async Task Song_tracks_keep_exact_parents_and_all_parts_but_return_each_file_once()
    {
        var matches = (await GetJson(DiscoverySongPath + "/tracks")).AsArray();
        matches.Should().HaveCount(3);
        matches
            .Select(match => match!["track"]!["id"]!.GetValue<string>())
            .Should()
            .BeEquivalentTo(
                CatalogId("track", 341),
                CatalogId("track", 342),
                CatalogId("track", 343)
            );
        foreach (var match in matches)
        {
            var track = match!["track"]!;
            var direct = await GetJson("/api/v4/source-tracks/" + track["id"]!.GetValue<string>());
            JsonNode.DeepEquals(track, direct).Should().BeTrue();
            track["artist_id"]!.GetValue<string>().Should().Be(CatalogId("artist", 301));
            match["matching_part_ordinals"]!.AsArray().Should().NotBeEmpty();
        }
        var medley = matches.Single(match =>
            match!["track"]!["id"]!.GetValue<string>() == CatalogId("track", 341)
        )!;
        medley["matching_part_ordinals"]!
            .AsArray()
            .Select(part => part!.GetValue<int>())
            .Should()
            .Equal(1, 3);
        medley["track"]!["source_id"]!.GetValue<string>().Should().Be(CatalogId("source", 321));
        medley["track"]!["source_set_id"]!
            .GetValue<string>()
            .Should()
            .Be(CatalogId("source_set", 331));
        medley["track"]!["show_id"]!.GetValue<string>().Should().Be(CatalogId("show", 311));
        medley["track"]!["title"]!
            .GetValue<string>()
            .Should()
            .Be("Original Scarlet > Fire > Scarlet");
        medley["track"]!["normalization"]!["parts"]!.AsArray().Should().HaveCount(3);
        var partial = matches.Single(match =>
            match!["track"]!["id"]!.GetValue<string>() == CatalogId("track", 343)
        )!;
        partial["track"]!["normalization"]!["state"]!.GetValue<string>().Should().Be("partial");
        partial["matching_part_ordinals"]!
            .AsArray()
            .Select(part => part!.GetValue<int>())
            .Should()
            .Equal(1);

        var shows = (await GetJson(DiscoverySongPath + "/shows")).AsArray();
        shows
            .Select(show => show!["id"]!.GetValue<string>())
            .Should()
            .Equal(CatalogId("show", 311), CatalogId("show", 312));
        var detail = await GetJson(DiscoverySongPath);
        detail["matched_track_count"]!.GetValue<int>().Should().Be(matches.Count);
        detail["matched_show_count"]!.GetValue<int>().Should().Be(shows.Count);
        var document = await GetJson("/api-docs/v4/swagger.json");
        AssertSchemaFields(document, "SongTrackMatchResource", medley);
    }

    [Test]
    public async Task Imported_song_inventory_and_detail_preserve_distinct_setlist_show_scope()
    {
        var inventory = (await GetJson(SongArtistPath + "/imported-songs")).AsArray();
        inventory.Should().HaveCount(4);
        var imported = inventory.Single(song =>
            song!["id"]!.GetValue<string>() == CatalogId("imported_song", 351)
        )!;
        imported["setlist_show_count"]!.GetValue<int>().Should().Be(2);
        var detail = await GetJson(ImportedSongPath(301, 351));
        detail["setlist_show_count"]!.GetValue<int>().Should().Be(2);
        detail["shows"]!
            .AsArray()
            .Select(show => show!["id"]!.GetValue<string>())
            .Should()
            .Equal(CatalogId("show", 311), CatalogId("show", 313));
        detail
            .AsObject()
            .Where(property => property.Key != "shows")
            .Select(property => property.Key)
            .Should()
            .BeEquivalentTo(imported.AsObject().Select(property => property.Key));
        foreach (var property in imported.AsObject())
        {
            JsonNode.DeepEquals(property.Value, detail[property.Key]).Should().BeTrue(property.Key);
        }
        foreach (var suffix in new[] { 353, 354 })
        {
            var empty = await GetJson(ImportedSongPath(301, suffix));
            empty["setlist_show_count"]!.GetValue<int>().Should().Be(0);
            empty["shows"]!.AsArray().Should().BeEmpty();
        }
        using var foreign = await http.GetAsync(ImportedSongPath(302, 351));
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var document = await GetJson("/api-docs/v4/swagger.json");
        AssertSchemaFields(document, "ImportedSongResource", imported);
        AssertSchemaFields(document, "ImportedSongWithShowsResource", detail);
    }

    [TestCase(302, 355)]
    [TestCase(303, 356)]
    public async Task Artists_without_approved_song_normalization_keep_imported_browsing(
        int artistSuffix,
        int importedSuffix
    )
    {
        var entries = (await GetJson(ArtistPath(artistSuffix) + "/songs")).AsArray();
        entries.Should().ContainSingle();
        entries[0]!["kind"]!.GetValue<string>().Should().Be("imported_song");
        entries[0]!["song"].Should().BeNull();
        entries[0]!["imported_song"]!["id"]!
            .GetValue<string>()
            .Should()
            .Be(CatalogId("imported_song", importedSuffix));
        var detail = await GetJson(ImportedSongPath(artistSuffix, importedSuffix));
        detail["setlist_show_count"]!.GetValue<int>().Should().Be(0);
        detail["shows"]!.AsArray().Should().BeEmpty();
    }

    [TestCase("/api/v4/songs/song_00000105", HttpStatusCode.NotFound)]
    [TestCase("/api/v4/songs/song_00000105/tracks", HttpStatusCode.NotFound)]
    [TestCase("/api/v4/songs/song_00000105/shows", HttpStatusCode.NotFound)]
    [TestCase("/api/v4/songs/song_zzzzzzzz", HttpStatusCode.NotFound)]
    [TestCase("/api/v4/songs/venue_00000101", HttpStatusCode.UnprocessableEntity)]
    [TestCase("/api/v4/songs/song_bad/tracks", HttpStatusCode.UnprocessableEntity)]
    public async Task Song_routes_distinguish_unavailable_unknown_and_invalid_targets(
        string path,
        HttpStatusCode expected
    )
    {
        using var response = await http.GetAsync(path);
        response.StatusCode.Should().Be(expected);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Test]
    public async Task Song_collections_distinguish_unknown_artists_and_known_empty_results()
    {
        foreach (var collection in new[] { "/songs", "/imported-songs" })
        {
            (await GetJson(ArtistPath(20) + collection)).AsArray().Should().BeEmpty();
            using var response = await http.GetAsync(ArtistPath(999) + collection);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        using var malformed = await http.GetAsync(
            SongArtistPath + "/imported-songs/" + CatalogId("song", 351)
        );
        malformed.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var unknown = await http.GetAsync(ImportedSongPath(301, 999));
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Song_recording_counts_and_queries_revalidate_after_normalization_only_correction()
    {
        var paths = new[]
        {
            DiscoverySongPath,
            DiscoverySongPath + "/tracks",
            DiscoverySongPath + "/shows",
            SongArtistPath + "/songs",
        };
        var validators = new Dictionary<string, string>();
        foreach (var path in paths)
        {
            using var response = await http.GetAsync(path);
            validators[path] = response.Headers.ETag!.ToString();
            using var unchanged = await Conditional(path, validators[path]);
            unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);
        }
        var original = await GetJson("/api/v4/source-tracks/" + CatalogId("track", 343));
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await ChangeNormalization(
                connection,
                """
                UPDATE norm.track_songs SET song_id=NULL,resolved=false,basis='open'
                WHERE track_uuid='00000000-0000-0000-0000-000000000343' AND part=1
                """
            );
            foreach (var path in paths)
            {
                using var changed = await Conditional(path, validators[path]);
                changed.StatusCode.Should().Be(HttpStatusCode.OK, path);
                changed.Headers.ETag!.ToString().Should().NotBe(validators[path], path);
            }
            var detail = await GetJson(DiscoverySongPath);
            detail["matched_track_count"]!.GetValue<int>().Should().Be(2);
            detail["matched_show_count"]!.GetValue<int>().Should().Be(1);
            var track = await GetJson("/api/v4/source-tracks/" + CatalogId("track", 343));
            JsonNode.DeepEquals(track["updated_at"], original["updated_at"]).Should().BeTrue();
            track["title"]!.GetValue<string>().Should().Be(original["title"]!.GetValue<string>());
            (await GetJson(ImportedSongPath(301, 351)))["setlist_show_count"]!
                .GetValue<int>()
                .Should()
                .Be(2);
        }
        finally
        {
            await ChangeNormalization(
                connection,
                """
                UPDATE norm.track_songs SET song_id='song_00000101',resolved=true,basis='ruling'
                WHERE track_uuid='00000000-0000-0000-0000-000000000343' AND part=1
                """
            );
        }
        (await GetJson(DiscoverySongPath))["matched_track_count"]!.GetValue<int>().Should().Be(3);
        (await GetJson(DiscoverySongPath + "/shows")).AsArray().Should().HaveCount(2);
    }

    [Test]
    public async Task Song_alias_correction_refreshes_browse_and_detail_without_changing_recording_titles()
    {
        using var initial = await http.GetAsync(DiscoverySongPath);
        var etag = initial.Headers.ETag!.ToString();
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await ChangeNormalization(
                connection,
                "UPDATE norm.song_aliases SET alias='Reviewed Scarlet' WHERE song_id='song_00000101' AND normalized='scarlet'"
            );
            using var changed = await Conditional(DiscoverySongPath, etag);
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
            var song = JsonNode.Parse(await changed.Content.ReadAsStringAsync())!;
            song["aliases"]!
                .AsArray()
                .Select(alias => alias!.GetValue<string>())
                .Should()
                .Contain("Reviewed Scarlet");
            var entries = (await GetJson(SongArtistPath + "/songs")).AsArray();
            JsonNode
                .DeepEquals(
                    entries.Single(entry =>
                        entry!["song"]?["id"]?.GetValue<string>() == "song_00000101"
                    )!["song"],
                    song
                )
                .Should()
                .BeTrue();
            (await GetJson("/api/v4/source-tracks/" + CatalogId("track", 341)))["title"]!
                .GetValue<string>()
                .Should()
                .Be("Original Scarlet > Fire > Scarlet");
        }
        finally
        {
            await ChangeNormalization(
                connection,
                "UPDATE norm.song_aliases SET alias='Scarlet' WHERE song_id='song_00000101' AND normalized='scarlet'"
            );
        }
    }

    [Test]
    public async Task Broken_song_merge_preserves_imported_fallback_and_recovers_after_correction()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await ChangeNormalization(
                connection,
                "UPDATE norm.songs SET merged_into='song_00000104' WHERE id='song_00000101'"
            );
            using var broken = await http.GetAsync(DiscoverySongPath);
            broken.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            broken.Headers.CacheControl!.NoStore.Should().BeTrue();
            var entries = (await GetJson(SongArtistPath + "/songs")).AsArray();
            entries
                .Select(entry => entry!["imported_song"]?["id"]?.GetValue<string>())
                .Should()
                .Contain(CatalogId("imported_song", 351));
            entries
                .Select(entry => entry!["song"]?["id"]?.GetValue<string>())
                .Should()
                .NotContain("song_00000101");
            var track = await GetJson("/api/v4/source-tracks/" + CatalogId("track", 341));
            track["normalization"]!["state"]!.GetValue<string>().Should().Be("unavailable");
            track["normalization"]!["reason"]!.GetValue<string>().Should().Be("inconsistent");
            (await GetJson("/api/v4/songs/song_00000102"))["matched_track_count"]!
                .GetValue<int>()
                .Should()
                .Be(0);
            (await GetJson(ImportedSongPath(301, 351)))["shows"]!.AsArray().Should().HaveCount(2);
        }
        finally
        {
            await ChangeNormalization(
                connection,
                "UPDATE norm.songs SET merged_into=NULL WHERE id='song_00000101'"
            );
        }
        (await GetJson(DiscoverySongPath))["matched_track_count"]!.GetValue<int>().Should().Be(3);
    }

    [Test]
    public async Task Song_dependency_failure_is_not_cached_and_recovers_with_the_same_public_reads()
    {
        var previous = DbService.ConnStr;
        try
        {
            DbService.ConnStr =
                "Host=127.0.0.1;Port=1;Database=unavailable;Username=relisten;Timeout=1";
            using var failed = await http.GetAsync(DiscoverySongPath + "/tracks");
            failed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            failed.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
        finally
        {
            DbService.ConnStr = previous;
        }
        (await GetJson(DiscoverySongPath + "/tracks")).AsArray().Should().HaveCount(3);
    }
}
