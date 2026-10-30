using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Relisten;

namespace RelistenApiTests.V4;

// The same imported venue can contain normalized and unmatched shows. Following
// these resources must preserve that distinction all the way to a playable source.
public sealed partial class TestV4CatalogHttp
{
    private const string CanonicalVenuePath = "/api/v4/venues/venue_00000001";

    private static string ArtistPath(int suffix) =>
        "/api/v4/artists/" + CatalogId("artist", suffix);

    private static string SourcePath(int suffix) =>
        "/api/v4/sources/" + CatalogId("source", suffix);

    private static string ImportedVenuePath(int artistSuffix, int venueSuffix) =>
        ArtistPath(artistSuffix) + "/imported-venues/" + CatalogId("imported_venue", venueSuffix);

    [Test]
    public async Task Artist_venue_groups_follow_show_assignments_and_preserve_unmatched_imported_groups()
    {
        var entries = (await GetJson(BrowseArtistPath + "/venues")).AsArray();
        entries.Should().HaveCount(2);
        var canonical = entries.Single(item => item!["kind"]!.GetValue<string>() == "venue")!;
        canonical["show_count"]!.GetValue<int>().Should().Be(1);
        canonical["venue"]!["id"]!.GetValue<string>().Should().Be("venue_00000001");
        canonical["imported_venue"].Should().BeNull();
        var imported = entries.Single(item =>
            item!["kind"]!.GetValue<string>() == "imported_venue"
        )!;
        imported["show_count"]!.GetValue<int>().Should().Be(1);
        imported["imported_venue"]!["id"]!
            .GetValue<string>()
            .Should()
            .Be(CatalogId("imported_venue", 74));
        imported["venue"].Should().BeNull();

        var detail = await GetJson(BrowseArtistPath + "/venues/venue_00000001");
        JsonNode.DeepEquals(canonical, detail).Should().BeTrue();
        var global = await GetJson(CanonicalVenuePath);
        JsonNode.DeepEquals(global, detail["venue"]).Should().BeTrue();
        var shows = (await GetJson(BrowseArtistPath + "/venues/venue_00000001/shows")).AsArray();
        shows.Select(show => show!["id"]!.GetValue<string>()).Should().Equal(ShowId(4));
        shows[0]!["imported_venue"]!["name"]!
            .GetValue<string>()
            .Should()
            .Be("Imported Civic Center");
        JsonNode.DeepEquals(global, shows[0]!["venue"]).Should().BeTrue();

        var document = await GetJson("/api-docs/v4/swagger.json");
        AssertSchemaFields(document, "ArtistVenueEntryResource", detail);
        EnumValues(document, "ArtistVenueEntryResource", "kind")
            .Should()
            .BeEquivalentTo("venue", "imported_venue");
    }

    [TestCase(11)]
    [TestCase(12)]
    public async Task Artists_without_approved_normalization_browse_imported_venues(
        int artistSuffix
    )
    {
        var entries = (await GetJson(ArtistPath(artistSuffix) + "/venues")).AsArray();
        entries.Should().ContainSingle();
        entries[0]!["kind"]!.GetValue<string>().Should().Be("imported_venue");
        entries[0]!["venue"].Should().BeNull();
        entries[0]!["show_count"]!.GetValue<int>().Should().Be(1);
        var imported = entries[0]!["imported_venue"]!;
        imported["name"]!.GetValue<string>().Should().Be("Imported Civic Center");
        imported["artist_id"]!.GetValue<string>().Should().Be(CatalogId("artist", artistSuffix));
        (await GetJson(ArtistPath(artistSuffix) + "/venues/venue_00000001/shows"))
            .AsArray()
            .Should()
            .BeEmpty();
        using var unavailable = await http.GetAsync(
            ArtistPath(artistSuffix) + "/venues/venue_00000001"
        );
        unavailable.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Global_venue_shows_include_only_approved_assignments_and_support_artist_filtering()
    {
        var global = (await GetJson(CanonicalVenuePath + "/shows")).AsArray();
        global
            .Select(show => show!["id"]!.GetValue<string>())
            .Should()
            .BeEquivalentTo(ShowId(3), ShowId(4));
        var filtered = (
            await GetJson(CanonicalVenuePath + "/shows?artist_id=" + BrowseArtistId)
        ).AsArray();
        filtered.Select(show => show!["id"]!.GetValue<string>()).Should().Equal(ShowId(4));
        var scoped = await GetJson(BrowseArtistPath + "/venues/venue_00000001/shows");
        JsonNode.DeepEquals(filtered, scoped).Should().BeTrue();
        (await GetJson(CanonicalVenuePath + "/shows?artist_id=" + CatalogId("artist", 12)))
            .AsArray()
            .Should()
            .BeEmpty();
        (await GetJson("/api/v4/venues/venue_00000003/shows")).AsArray().Should().BeEmpty();
        (await GetJson(ArtistPath(20) + "/venues")).AsArray().Should().BeEmpty();
    }

    [Test]
    public async Task Global_venue_metadata_remains_resolvable_when_artist_normalization_is_disabled()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                "UPDATE features SET norm_catalogue=false WHERE artist_id IN(910003,910004)"
            );
            var venue = await GetJson(CanonicalVenuePath);
            venue["id"]!.GetValue<string>().Should().Be("venue_00000001");
            venue["names"]!.AsArray().Should().HaveCount(5);
            (await GetJson(CanonicalVenuePath + "/shows")).AsArray().Should().BeEmpty();
            var entries = (await GetJson(BrowseArtistPath + "/venues")).AsArray();
            entries.Should().ContainSingle();
            entries[0]!["kind"]!.GetValue<string>().Should().Be("imported_venue");
            entries[0]!["show_count"]!.GetValue<int>().Should().Be(2);
        }
        finally
        {
            await connection.ExecuteAsync(
                "UPDATE features SET norm_catalogue=true WHERE artist_id IN(910003,910004)"
            );
        }
    }

    [Test]
    public async Task Imported_venue_detail_keeps_original_show_scope_and_includes_unused_rows()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO venues(id,uuid,artist_id,name,location,slug,updated_at)
                VALUES(910099,'00000000-0000-0000-0000-000000000299',910004,'Unused Venue','Philadelphia, PA','unused-review','2026-01-01');
                """
            );
            var rows = (await GetJson(BrowseArtistPath + "/imported-venues")).AsArray();
            rows.Should().HaveCount(2);
            rows.Single(row => row!["id"]!.GetValue<string>() == CatalogId("imported_venue", 74))![
                "show_count"
            ]!
                .GetValue<int>()
                .Should()
                .Be(2);
            var unused = rows.Single(row =>
                row!["id"]!.GetValue<string>() == CatalogId("imported_venue", 299)
            )!;
            unused["show_count"]!.GetValue<int>().Should().Be(0);
            var empty = await GetJson(ImportedVenuePath(14, 299));
            empty["shows"]!.AsArray().Should().BeEmpty();

            var detail = await GetJson(ImportedVenuePath(14, 74));
            detail["id"]!.GetValue<string>().Should().Be(CatalogId("imported_venue", 74));
            detail["show_count"]!.GetValue<int>().Should().Be(2);
            detail["name"]!.GetValue<string>().Should().Be("Imported Civic Center");
            var shows = detail["shows"]!.AsArray();
            shows
                .Select(show => show!["id"]!.GetValue<string>())
                .Should()
                .BeEquivalentTo(ShowId(4), ShowId(10));
            shows.Single(show => show!["id"]!.GetValue<string>() == ShowId(4))!["venue"]!["id"]!
                .GetValue<string>()
                .Should()
                .Be("venue_00000001");
            shows
                .Single(show => show!["id"]!.GetValue<string>() == ShowId(10))!["venue"]
                .Should()
                .BeNull();
            using var foreign = await http.GetAsync(ImportedVenuePath(13, 74));
            foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);

            var document = await GetJson("/api-docs/v4/swagger.json");
            AssertSchemaFields(document, "ImportedVenueWithShowCountResource", rows[0]!);
            AssertSchemaFields(document, "ImportedVenueWithShowsResource", detail);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM venues WHERE id=910099");
        }
    }

    [Test]
    public async Task Venue_merge_lookup_and_browse_follow_the_same_survivor_and_alias_union()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await ChangeNormalization(
                connection,
                """
                INSERT INTO norm.venues(id,name,slug,author_type,author_ref,run_uuid)
                VALUES('venue_00000002','Survivor Venue','review-venue-survivor','importer','v4-review','00000000-0000-0000-0000-000000000099');
                INSERT INTO norm.venue_names(venue_id,normalized,name,kind,author_type,author_ref,run_uuid)
                VALUES('venue_00000002','survivor-alias','Survivor Alias','short','importer','v4-review','00000000-0000-0000-0000-000000000099');
                UPDATE norm.venues SET merged_into='venue_00000002' WHERE id='venue_00000001';
                """
            );
            var venue = await GetJson(CanonicalVenuePath);
            venue["id"]!.GetValue<string>().Should().Be("venue_00000002");
            var direct = await GetJson("/api/v4/venues/venue_00000002");
            JsonNode.DeepEquals(venue, direct).Should().BeTrue();
            venue["names"]!
                .AsArray()
                .Should()
                .Contain(alias => alias!["name"]!.GetValue<string>() == "Historical Arena");
            venue["names"]!
                .AsArray()
                .Should()
                .Contain(alias => alias!["name"]!.GetValue<string>() == "Survivor Alias");
            var entry = await GetJson(BrowseArtistPath + "/venues/venue_00000001");
            JsonNode.DeepEquals(venue, entry["venue"]).Should().BeTrue();
            (await GetJson(CanonicalVenuePath + "/shows"))
                .AsArray()
                .Select(show => show!["venue"]!["id"]!.GetValue<string>())
                .Should()
                .OnlyContain(id => id == "venue_00000002");
        }
        finally
        {
            await ChangeNormalization(
                connection,
                """
                UPDATE norm.venues SET merged_into=NULL WHERE id='venue_00000001';
                DELETE FROM norm.venue_names WHERE venue_id='venue_00000002';
                DELETE FROM norm.venues WHERE id='venue_00000002';
                """
            );
        }
    }

    [TestCase("/api/v4/venues/venue_bad", HttpStatusCode.UnprocessableEntity)]
    [TestCase("/api/v4/venues/song_00000001", HttpStatusCode.UnprocessableEntity)]
    [TestCase("/api/v4/venues/venue_zzzzzzzz", HttpStatusCode.NotFound)]
    [TestCase("/api/v4/venues/venue_00000001/shows?artist_id=", HttpStatusCode.UnprocessableEntity)]
    [TestCase(
        "/api/v4/venues/venue_00000001/shows?artist_id=show_00000000000000000000000000",
        HttpStatusCode.UnprocessableEntity
    )]
    public async Task Venue_routes_distinguish_invalid_and_unknown_references(
        string path,
        HttpStatusCode expected
    )
    {
        using var response = await http.GetAsync(path);
        response.StatusCode.Should().Be(expected);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Test]
    public async Task Broken_venue_merge_chains_preserve_imported_browsing_and_recover_after_correction()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await ChangeNormalization(
                connection,
                """
                UPDATE norm.venues SET merged_into='venue_00000003' WHERE id='venue_00000001';
                UPDATE norm.venues SET merged_into='venue_00000001' WHERE id='venue_00000003';
                """
            );
            using var broken = await http.GetAsync(CanonicalVenuePath);
            broken.StatusCode.Should().Be(HttpStatusCode.NotFound);
            var entries = (await GetJson(BrowseArtistPath + "/venues")).AsArray();
            entries.Should().ContainSingle();
            entries[0]!["kind"]!.GetValue<string>().Should().Be("imported_venue");
            entries[0]!["show_count"]!.GetValue<int>().Should().Be(2);
            var show = await GetJson(ShowPath(4));
            show["venue"].Should().BeNull();
            show["imported_venue"]!["name"]!
                .GetValue<string>()
                .Should()
                .Be("Imported Civic Center");
        }
        finally
        {
            await ChangeNormalization(
                connection,
                """
                UPDATE norm.venues SET merged_into=NULL WHERE id IN('venue_00000001','venue_00000003');
                """
            );
        }
        (await GetJson(CanonicalVenuePath))["id"]!.GetValue<string>().Should().Be("venue_00000001");
    }

    [Test]
    public async Task Venue_collections_distinguish_unknown_owners_from_known_empty_results()
    {
        foreach (
            var path in new[]
            {
                ArtistPath(999) + "/venues",
                ArtistPath(999) + "/imported-venues",
                CanonicalVenuePath + "/shows?artist_id=" + CatalogId("artist", 999),
                BrowseArtistPath + "/venues/venue_zzzzzzzz/shows",
            }
        )
        {
            using var response = await http.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, path);
        }
        (await GetJson(ArtistPath(20) + "/imported-venues")).AsArray().Should().BeEmpty();
    }

    [Test]
    public async Task Venue_browse_counts_revalidate_after_assignment_changes()
    {
        var path = BrowseArtistPath + "/venues";
        using var initial = await http.GetAsync(path);
        initial.StatusCode.Should().Be(HttpStatusCode.OK);
        var etag = initial.Headers.ETag!.ToString();
        using var unchanged = await Conditional(path, etag);
        unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await ChangeNormalization(
                connection,
                """
                INSERT INTO norm.show_venues(show_uuid,venue_id,basis,author_type,author_ref,run_uuid)
                VALUES('00000000-0000-0000-0000-000000000010','venue_00000001','ruling','importer','v4-review','00000000-0000-0000-0000-000000000099');
                """
            );
            using var response = await Conditional(path, etag);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.ETag!.ToString().Should().NotBe(etag);
            var entries = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
            entries.Should().ContainSingle();
            entries[0]!["show_count"]!.GetValue<int>().Should().Be(2);
            var detail = await GetJson(BrowseArtistPath + "/venues/venue_00000001");
            JsonNode.DeepEquals(entries[0], detail).Should().BeTrue();
        }
        finally
        {
            await ChangeNormalization(
                connection,
                "DELETE FROM norm.show_venues WHERE show_uuid='00000000-0000-0000-0000-000000000010'"
            );
        }
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    public async Task Direct_source_matches_nested_show_source_and_its_direct_tracks(int scenario)
    {
        var source = await GetJson(SourcePath(20 + scenario));
        var show = await GetJson(ShowPath(scenario));
        JsonNode.DeepEquals(source, show["sources"]![0]).Should().BeTrue();
        foreach (
            var track in source["sets"]!.AsArray().SelectMany(set => set!["tracks"]!.AsArray())
        )
        {
            var direct = await GetJson("/api/v4/source-tracks/" + track!["id"]!.GetValue<string>());
            JsonNode.DeepEquals(track, direct).Should().BeTrue();
            track["source_id"]!.GetValue<string>().Should().Be(source["id"]!.GetValue<string>());
            track["show_id"]!.GetValue<string>().Should().Be(show["id"]!.GetValue<string>());
        }
        var document = await GetJson("/api-docs/v4/swagger.json");
        AssertSchemaFields(document, "SourceResource", source);
    }

    [Test]
    public async Task Direct_source_uses_its_own_imported_venue_and_excludes_sibling_and_orphan_tracks()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO venues(id,uuid,artist_id,name,location,slug,updated_at)
                VALUES(910099,'00000000-0000-0000-0000-000000000299',910004,'Recording Venue','Philadelphia, PA','recording-venue-review','2026-01-01');
                UPDATE sources SET venue_id=910099 WHERE id=910004;
                INSERT INTO sources(id,uuid,show_id,artist_id,venue_id,is_soundboard,is_remaster,has_jamcharts,upstream_identifier,display_date,updated_at)
                VALUES(910099,'00000000-0000-0000-0000-000000000220',910004,910004,910004,false,false,false,'sibling-review','1995-12-31','2026-01-01');
                INSERT INTO source_sets(id,uuid,source_id,index,name,updated_at)
                VALUES(910099,'00000000-0000-0000-0000-000000000230',910099,1,'Sibling Set','2026-01-01');
                INSERT INTO source_tracks(id,uuid,artist_id,source_id,source_set_id,track_position,title,slug,mp3_url,updated_at)
                VALUES(910099,'00000000-0000-0000-0000-000000000240',910004,910099,910099,1,'Sibling Track','sibling-track','https://example.invalid/sibling.mp3','2026-01-01');
                UPDATE source_tracks SET is_orphaned=true WHERE id=910006;
                """
            );
            var source = await GetJson(SourcePath(24));
            source["imported_venue"]!["name"]!.GetValue<string>().Should().Be("Recording Venue");
            var sourceOnlyVenue = await GetJson(ImportedVenuePath(14, 299));
            sourceOnlyVenue["show_count"]!.GetValue<int>().Should().Be(0);
            sourceOnlyVenue["shows"]!.AsArray().Should().BeEmpty();
            var show = await GetJson(ShowPath(4));
            show["imported_venue"]!["name"]!
                .GetValue<string>()
                .Should()
                .Be("Imported Civic Center");
            var nested = show["sources"]!
                .AsArray()
                .Single(item => item!["id"]!.GetValue<string>() == CatalogId("source", 24));
            JsonNode.DeepEquals(source, nested).Should().BeTrue();
            var tracks = source["sets"]!
                .AsArray()
                .SelectMany(set => set!["tracks"]!.AsArray())
                .ToArray();
            tracks
                .Select(track => track!["id"]!.GetValue<string>())
                .Should()
                .BeEquivalentTo(TrackId(4), TrackId(5));
            using var orphan = await http.GetAsync(TrackPath(6));
            orphan.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await GetJson(SourcePath(220)))["sets"]![0]!["tracks"]![0]!["title"]!
                .GetValue<string>()
                .Should()
                .Be("Sibling Track");
            var venueGroups = (await GetJson(BrowseArtistPath + "/venues")).AsArray();
            venueGroups.Single(item => item!["kind"]!.GetValue<string>() == "venue")!["show_count"]!
                .GetValue<int>()
                .Should()
                .Be(1);
        }
        finally
        {
            await connection.ExecuteAsync(
                """
                UPDATE sources SET venue_id=910004 WHERE id=910004;
                UPDATE source_tracks SET is_orphaned=false WHERE id=910006;
                DELETE FROM source_tracks WHERE id=910099;
                DELETE FROM source_sets WHERE id=910099;
                DELETE FROM sources WHERE id=910099;
                DELETE FROM venues WHERE id=910099;
                """
            );
        }
    }

    [Test]
    public async Task Direct_source_revalidates_normalization_only_changes_and_recovers_after_dependency_failure()
    {
        var path = SourcePath(24);
        using var initial = await http.GetAsync(path);
        initial.StatusCode.Should().Be(HttpStatusCode.OK);
        var etag = initial.Headers.ETag!.ToString();
        using var unchanged = await Conditional(path, etag);
        unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await ChangeNormalization(
                connection,
                "UPDATE norm.track_songs SET notes=ARRAY['reviewed source detail'] WHERE track_uuid='00000000-0000-0000-0000-000000000044' AND part=1"
            );
            using var changed = await Conditional(path, etag);
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
            changed.Headers.ETag!.ToString().Should().NotBe(etag);
        }
        finally
        {
            await ChangeNormalization(
                connection,
                "UPDATE norm.track_songs SET notes='{}'::text[] WHERE track_uuid='00000000-0000-0000-0000-000000000044' AND part=1"
            );
        }

        var previous = DbService.ConnStr;
        try
        {
            DbService.ConnStr =
                "Host=127.0.0.1;Port=1;Database=unavailable;Username=relisten;Timeout=1";
            using var failed = await http.GetAsync(path);
            failed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            failed.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
        finally
        {
            DbService.ConnStr = previous;
        }
        (await GetJson(path))["id"]!.GetValue<string>().Should().Be(CatalogId("source", 24));
    }

    [Test]
    public async Task Direct_source_rejects_wrong_kinds_and_distinguishes_unknown_sources()
    {
        using var invalid = await http.GetAsync("/api/v4/sources/" + ShowId(4));
        invalid.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var missing = await http.GetAsync(SourcePath(999));
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
