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

    private static string ImportedSongPath(int songSuffix) =>
        "/api/v4/imported-songs/" + CatalogId("imported_song", songSuffix);

    [Test]
    public async Task Song_show_timings_separate_complete_single_song_files_from_medleys_and_preserve_unknowns()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await ChangeNormalization(
                connection,
                """
                UPDATE source_tracks SET duration=CASE id WHEN 925041 THEN 900 WHEN 925042 THEN 101 ELSE 500 END
                WHERE id IN(925041,925042,925043);
                INSERT INTO source_tracks
                    (id,uuid,artist_id,source_id,source_set_id,track_position,title,slug,mp3_url,duration,updated_at)
                SELECT 926000+i,('00000000-0000-0000-0000-'||lpad((700+i)::text,12,'0'))::uuid,
                       925001,925021,925031,10+i,'Timing sample','timing-sample-'||i,
                       'https://example.invalid/timing/'||i||'.mp3',duration,'2026-01-01'
                FROM (VALUES(1,100),(2,400),(3,200),(4,600),(5,1000),(6,0),(7,NULL),(8,-1)) samples(i,duration);
                INSERT INTO norm.track_songs(track_uuid,part,song_id,resolved,basis,author_type,author_ref,run_uuid)
                SELECT uuid,1,'song_00000101',true,'ruling','importer','v4-review','00000000-0000-0000-0000-000000000099'
                FROM source_tracks WHERE id BETWEEN 926001 AND 926008;
                INSERT INTO norm.track_songs(track_uuid,part,song_id,resolved,basis,author_type,author_ref,run_uuid)
                SELECT uuid,2,CASE id WHEN 926001 THEN NULL WHEN 926002 THEN 'song_00000104' ELSE 'song_00000102' END,
                       true,'ruling','importer','v4-review','00000000-0000-0000-0000-000000000099'
                FROM source_tracks WHERE id BETWEEN 926001 AND 926005;
                INSERT INTO norm.track_songs(track_uuid,part,song_id,resolved,basis,author_type,author_ref,run_uuid)
                VALUES('00000000-0000-0000-0000-000000000341',4,NULL,false,'open','importer','v4-review','00000000-0000-0000-0000-000000000099');
                """
            );

            using var initial = await http.GetAsync(DiscoverySongPath);
            initial.StatusCode.Should().Be(HttpStatusCode.OK);
            var detail = JsonNode.Parse(await initial.Content.ReadAsStringAsync())!;
            var shows = detail["shows"]!.AsArray();
            shows
                .Select(entry => entry!["show"]!["id"]!.GetValue<string>())
                .Should()
                .Equal(CatalogId("show", 311), CatalogId("show", 312));
            var first = shows[0]!;
            // The ordinary show remains unchanged; only the song collection owns timings.
            var ordinary = (await GetJson(SongArtistPath + "/shows"))
                .AsArray()
                .Single(show => show!["id"]!.GetValue<string>() == CatalogId("show", 311));
            JsonNode.DeepEquals(first["show"], ordinary).Should().BeTrue();
            foreach (
                var (bucket, count, min, max, avg, median) in new[]
                {
                    ("single_song", 3, 100, 400, 601d / 3, 101d),
                    ("multi_song", 4, 200, 1000, 675d, 750d),
                }
            )
            {
                var stats = first["timings"]![bucket]!;
                stats["track_count"]!.GetValue<int>().Should().Be(count);
                stats["min"]!.GetValue<int>().Should().Be(min);
                stats["max"]!.GetValue<int>().Should().Be(max);
                stats["avg"]!.GetValue<double>().Should().BeApproximately(avg, 1e-9);
                stats["median"]!.GetValue<double>().Should().Be(median);
            }
            // The second show has a positive duration, but an unresolved ending.
            shows[1]!["timings"]!["single_song"].Should().BeNull();
            shows[1]!["timings"]!["multi_song"].Should().BeNull();
            var schema = await GetJson("/api-docs/v4/swagger.json");
            AssertSchemaFields(schema, "SongShowResource", first);
            AssertSchemaFields(schema, "SongShowTimingsResource", first["timings"]!);
            AssertSchemaFields(
                schema,
                "TrackDurationStatisticsResource",
                first["timings"]!["single_song"]!
            );

            await ChangeNormalization(
                connection,
                "UPDATE norm.track_songs SET song_id='song_00000102' WHERE track_uuid='00000000-0000-0000-0000-000000000701' AND part=2"
            );
            using var reclassified = await Conditional(
                DiscoverySongPath,
                initial.Headers.ETag!.ToString()
            );
            reclassified.StatusCode.Should().Be(HttpStatusCode.OK);
            var shifted = JsonNode.Parse(await reclassified.Content.ReadAsStringAsync())!;
            shifted["matched_track_count"]!
                .GetValue<int>()
                .Should()
                .Be(detail["matched_track_count"]!.GetValue<int>());
            shifted["shows"]![0]!["timings"]!["single_song"]!["track_count"]!
                .GetValue<int>()
                .Should()
                .Be(2);
            shifted["shows"]![0]!["timings"]!["multi_song"]!["track_count"]!
                .GetValue<int>()
                .Should()
                .Be(5);

            await connection.ExecuteAsync(
                "UPDATE source_tracks SET duration=NULL WHERE source_id IN(925021,925022)"
            );
            using var changed = await Conditional(
                DiscoverySongPath,
                reclassified.Headers.ETag!.ToString()
            );
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
            var unknown = JsonNode.Parse(await changed.Content.ReadAsStringAsync())!;
            unknown["matched_track_count"]!
                .GetValue<int>()
                .Should()
                .Be(detail["matched_track_count"]!.GetValue<int>());
            unknown["shows"]!.AsArray().Should().HaveCount(2);
            unknown["shows"]![0]!["timings"]!["single_song"].Should().BeNull();
            unknown["shows"]![0]!["timings"]!["multi_song"].Should().BeNull();
        }
        finally
        {
            await ChangeNormalization(
                connection,
                """
                DELETE FROM norm.track_songs WHERE track_uuid IN(SELECT uuid FROM source_tracks WHERE id BETWEEN 926001 AND 926008);
                DELETE FROM norm.track_songs WHERE track_uuid='00000000-0000-0000-0000-000000000341' AND part=4;
                DELETE FROM source_tracks WHERE id BETWEEN 926001 AND 926008;
                UPDATE source_tracks SET duration=NULL WHERE id IN(925041,925042,925043);
                """
            );
        }
    }

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
                ResourceMetadata(detail)
            )
            .Should()
            .BeTrue();
        JsonNode.DeepEquals(await GetJson("/api/v4/songs/song_00000104"), detail).Should().BeTrue();

        var empty = await GetJson("/api/v4/songs/song_00000103");
        empty["matched_track_count"]!.GetValue<int>().Should().Be(0);
        empty["matched_show_count"]!.GetValue<int>().Should().Be(0);
        (await GetJson("/api/v4/songs/song_00000103/tracks"))["tracks"]!
            .AsArray()
            .Should()
            .BeEmpty();
        empty["shows"]!.AsArray().Should().BeEmpty();

        var document = await GetJson("/api-docs/v4/swagger.json");
        AssertSchemaFields(document, "SongWithShowsResource", detail);
        AssertSchemaFields(document, "SongDetailResource", songs[0]!["song"]!);
        AssertSchemaFields(document, "ArtistSongEntryResource", entries[0]!);
        EnumValues(document, "ArtistSongEntryResource", "kind")
            .Should()
            .BeEquivalentTo("song", "imported_song");
    }

    [Test]
    public async Task Song_tracks_keep_exact_parents_and_all_parts_but_return_each_file_once()
    {
        var recordingDetail = await GetJson(DiscoverySongPath + "/tracks");
        var tracks = recordingDetail["tracks"]!.AsArray();
        recordingDetail.AsObject().ContainsKey("shows").Should().BeFalse();
        var detail = await GetJson(DiscoverySongPath);
        JsonNode
            .DeepEquals(ResourceMetadata(recordingDetail), ResourceMetadata(detail))
            .Should()
            .BeTrue();
        tracks.Should().HaveCount(3);
        tracks
            .Select(track => track!["id"]!.GetValue<string>())
            .Should()
            .BeEquivalentTo(
                CatalogId("track", 341),
                CatalogId("track", 342),
                CatalogId("track", 343)
            );
        foreach (var track in tracks)
        {
            var direct = await GetJson("/api/v4/source-tracks/" + track!["id"]!.GetValue<string>());
            JsonNode.DeepEquals(track, direct).Should().BeTrue();
            track["artist_id"]!.GetValue<string>().Should().Be(CatalogId("artist", 301));
            MatchingOrdinals(track).Should().NotBeEmpty();
        }
        var medley = tracks.Single(track =>
            track!["id"]!.GetValue<string>() == CatalogId("track", 341)
        )!;
        MatchingOrdinals(medley).Should().Equal(1, 3);
        medley["source_id"]!.GetValue<string>().Should().Be(CatalogId("source", 321));
        medley["source_set_id"]!.GetValue<string>().Should().Be(CatalogId("source_set", 331));
        medley["show_id"]!.GetValue<string>().Should().Be(CatalogId("show", 311));
        medley["title"]!.GetValue<string>().Should().Be("Original Scarlet > Fire > Scarlet");
        medley["normalization"]!["parts"]!.AsArray().Should().HaveCount(3);
        var partial = tracks.Single(track =>
            track!["id"]!.GetValue<string>() == CatalogId("track", 343)
        )!;
        partial["normalization"]!["state"]!.GetValue<string>().Should().Be("partial");
        MatchingOrdinals(partial).Should().Equal(1);

        var shows = detail["shows"]!.AsArray();
        shows
            .Select(entry => entry!["show"]!["id"]!.GetValue<string>())
            .Should()
            .Equal(CatalogId("show", 311), CatalogId("show", 312));
        detail["matched_track_count"]!.GetValue<int>().Should().Be(tracks.Count);
        detail["matched_show_count"]!.GetValue<int>().Should().Be(shows.Count);
        var document = await GetJson("/api-docs/v4/swagger.json");
        AssertSchemaFields(document, "TrackResource", medley);

        IEnumerable<int> MatchingOrdinals(JsonNode track) =>
            track["normalization"]!["parts"]!
                .AsArray()
                .Where(part =>
                    part!["song"]?["id"]?.GetValue<string>()
                    == recordingDetail["id"]!.GetValue<string>()
                )
                .Select(part => part!["ordinal"]!.GetValue<int>());
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
        var detail = await GetJson(ImportedSongPath(351));
        detail["setlist_show_count"]!.GetValue<int>().Should().Be(2);
        detail["shows"]!
            .AsArray()
            .Select(show => show!["id"]!.GetValue<string>())
            .Should()
            .Equal(CatalogId("show", 311), CatalogId("show", 313));
        JsonNode.DeepEquals(ResourceMetadata(detail), imported).Should().BeTrue();
        foreach (var suffix in new[] { 353, 354 })
        {
            var empty = await GetJson(ImportedSongPath(suffix));
            empty["setlist_show_count"]!.GetValue<int>().Should().Be(0);
            empty["shows"]!.AsArray().Should().BeEmpty();
        }
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
        var detail = await GetJson(ImportedSongPath(importedSuffix));
        detail["setlist_show_count"]!.GetValue<int>().Should().Be(0);
        detail["shows"]!.AsArray().Should().BeEmpty();
    }

    [TestCase("/api/v4/songs/song_00000105", HttpStatusCode.NotFound)]
    [TestCase("/api/v4/songs/song_00000105/tracks", HttpStatusCode.NotFound)]
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
            "/api/v4/imported-songs/" + CatalogId("song", 351)
        );
        malformed.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var unknown = await http.GetAsync(ImportedSongPath(999));
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Song_recording_counts_and_queries_revalidate_after_normalization_only_correction()
    {
        var paths = new[]
        {
            DiscoverySongPath,
            DiscoverySongPath + "/tracks",
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
            (await GetJson(ImportedSongPath(351)))["setlist_show_count"]!
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
        (await GetJson(DiscoverySongPath))["shows"]!.AsArray().Should().HaveCount(2);
    }

    [Test]
    public async Task Stored_recording_counter_correction_revalidates_payloads_without_read_side_recount()
    {
        var paths = new[] { DiscoverySongPath, SongArtistPath + "/songs" };
        var validators = new Dictionary<string, string>();
        foreach (var path in paths)
        {
            using var response = await http.GetAsync(path);
            validators[path] = response.Headers.ETag!.ToString();
        }
        var trackBefore = await GetJson("/api/v4/source-tracks/" + CatalogId("track", 341));
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                """
                UPDATE norm.song_counts SET matched_track_count=9,matched_show_count=7,
                    updated_at=clock_timestamp() WHERE song_id='song_00000101'
                """
            );
            var stored = await connection.QuerySingleAsync<string>(
                "SELECT c::text FROM norm.song_counts c WHERE song_id='song_00000101'"
            );
            foreach (var path in paths)
            {
                using var response = await Conditional(path, validators[path]);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                response.Headers.ETag!.ToString().Should().NotBe(validators[path]);
            }
            var detail = await GetJson(DiscoverySongPath);
            detail["matched_track_count"]!.GetValue<int>().Should().Be(9);
            detail["matched_show_count"]!.GetValue<int>().Should().Be(7);
            var browse = (await GetJson(SongArtistPath + "/songs")).AsArray();
            JsonNode
                .DeepEquals(
                    browse.Single(entry =>
                        entry!["song"]?["id"]?.GetValue<string>() == "song_00000101"
                    )!["song"],
                    ResourceMetadata(detail)
                )
                .Should()
                .BeTrue();
            (await GetJson(DiscoverySongPath + "/tracks"))["tracks"]!
                .AsArray()
                .Should()
                .HaveCount(3);
            (await GetJson(DiscoverySongPath))["shows"]!.AsArray().Should().HaveCount(2);
            JsonNode
                .DeepEquals(
                    trackBefore,
                    await GetJson("/api/v4/source-tracks/" + CatalogId("track", 341))
                )
                .Should()
                .BeTrue();
            (
                await connection.QuerySingleAsync<string>(
                    "SELECT c::text FROM norm.song_counts c WHERE song_id='song_00000101'"
                )
            )
                .Should()
                .Be(stored);
        }
        finally
        {
            await connection.ExecuteAsync(
                "SELECT norm.refresh_song_counts('00000000-0000-0000-0000-000000000301'::uuid)"
            );
        }
    }

    [Test]
    public async Task Stored_imported_counter_correction_is_shared_by_browse_inventory_and_detail()
    {
        var path = ImportedSongPath(352);
        using var initial = await http.GetAsync(path);
        var etag = initial.Headers.ETag!.ToString();
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                """
                UPDATE setlist_song_counts SET setlist_show_count=7,updated_at=clock_timestamp()
                WHERE setlist_song_id=925052
                """
            );
            var stored = await connection.QuerySingleAsync<string>(
                "SELECT c::text FROM setlist_song_counts c WHERE setlist_song_id=925052"
            );
            using var changed = await Conditional(path, etag);
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
            var detail = JsonNode.Parse(await changed.Content.ReadAsStringAsync())!;
            detail["setlist_show_count"]!.GetValue<int>().Should().Be(7);
            var browse = (await GetJson(SongArtistPath + "/songs")).AsArray();
            browse.Single(entry =>
                entry!["imported_song"]?["id"]?.GetValue<string>()
                == CatalogId("imported_song", 352)
            )!["imported_song"]!["setlist_show_count"]!
                .GetValue<int>()
                .Should()
                .Be(7);
            var inventory = (await GetJson(SongArtistPath + "/imported-songs")).AsArray();
            inventory.Single(song =>
                song!["id"]!.GetValue<string>() == CatalogId("imported_song", 352)
            )!["setlist_show_count"]!
                .GetValue<int>()
                .Should()
                .Be(7);
            (
                await connection.QuerySingleAsync<string>(
                    "SELECT c::text FROM setlist_song_counts c WHERE setlist_song_id=925052"
                )
            )
                .Should()
                .Be(stored);
        }
        finally
        {
            await connection.ExecuteAsync(
                "SELECT norm.refresh_song_counts('00000000-0000-0000-0000-000000000301'::uuid)"
            );
        }
    }

    [Test]
    public async Task Recording_discovery_converges_with_stored_counts_only_after_explicit_refresh()
    {
        using var initial = await http.GetAsync(DiscoverySongPath);
        var etag = initial.Headers.ETag!.ToString();
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await using (var sourceWrite = await connection.BeginTransactionAsync())
            {
                await connection.ExecuteAsync(
                    """
                    SET LOCAL norm.author_type='importer';
                    SET LOCAL norm.author_ref='v4-review';
                    SET LOCAL norm.run_uuid='00000000-0000-0000-0000-000000000099';
                    UPDATE norm.track_songs SET song_id=NULL,resolved=false,basis='open'
                    WHERE track_uuid='00000000-0000-0000-0000-000000000343' AND part=1;
                    """,
                    transaction: sourceWrite
                );
                await sourceWrite.CommitAsync();
            }
            // The detail validator covers live membership even before the import's
            // post-commit recount. Stored counters retain their independent meaning.
            (await GetJson(DiscoverySongPath + "/tracks"))["tracks"]!
                .AsArray()
                .Should()
                .HaveCount(2);
            (await GetJson(DiscoverySongPath))["shows"]!.AsArray().Should().ContainSingle();
            using var stale = await Conditional(DiscoverySongPath, etag);
            stale.StatusCode.Should().Be(HttpStatusCode.OK);
            stale.Headers.ETag!.ToString().Should().NotBe(etag);
            var current = JsonNode.Parse(await stale.Content.ReadAsStringAsync())!;
            current["matched_show_count"]!.GetValue<int>().Should().Be(2);
            current["shows"]!.AsArray().Should().ContainSingle();
            var membershipEtag = stale.Headers.ETag!.ToString();

            await connection.ExecuteAsync(
                "SELECT norm.refresh_song_counts('00000000-0000-0000-0000-000000000301'::uuid)"
            );
            using var refreshed = await Conditional(DiscoverySongPath, membershipEtag);
            refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
            var detail = JsonNode.Parse(await refreshed.Content.ReadAsStringAsync())!;
            detail["matched_track_count"]!.GetValue<int>().Should().Be(2);
            detail["matched_show_count"]!.GetValue<int>().Should().Be(1);
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
                    ResourceMetadata(song)
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
            (await GetJson(ImportedSongPath(351)))["shows"]!.AsArray().Should().HaveCount(2);
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
        (await GetJson(DiscoverySongPath + "/tracks"))["tracks"]!.AsArray().Should().HaveCount(3);
    }
}
