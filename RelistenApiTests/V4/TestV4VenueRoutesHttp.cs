using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Relisten;

namespace RelistenApiTests.V4;

// Imported venues retain their complete show collections during partial normalization.
// Their counts stay consistent across browse entries, show metadata and detail.
public sealed partial class TestV4CatalogHttp
{
    private const string CanonicalVenuePath = "/api/v4/venues/venue_00000001";

    private static string ArtistPath(int suffix) =>
        "/api/v4/artists/" + CatalogId("artist", suffix);

    private static string SourcePath(int suffix) =>
        "/api/v4/sources/" + CatalogId("source", suffix);

    private static string ImportedVenuePath(int venueSuffix) =>
        "/api/v4/imported-venues/" + CatalogId("imported_venue", venueSuffix);

    [Test]
    public async Task Artist_venue_browse_keeps_full_imported_counts_during_partial_normalization()
    {
        var entries = (await GetJson(BrowseArtistPath + "/venues")).AsArray();
        entries.Should().HaveCount(2);
        var canonical = entries.Single(item =>
            item!["kind"]!.GetValue<string>() == "artist_venue"
        )!;
        canonical["artist_venue"]!["artist_show_count"]!.GetValue<int>().Should().Be(1);
        canonical["artist_venue"]!["venue"]!["id"]!
            .GetValue<string>()
            .Should()
            .Be("venue_00000001");
        canonical["artist_venue"]!["venue"]!["slug"]!
            .GetValue<string>()
            .Should()
            .Be("review-arena");
        canonical["imported_venue"].Should().BeNull();
        var imported = entries.Single(item =>
            item!["kind"]!.GetValue<string>() == "imported_venue"
        )!;
        imported.AsObject().ContainsKey("fallback_show_count").Should().BeFalse();
        imported["imported_venue"]!["show_count"]!.GetValue<int>().Should().Be(2);
        imported["imported_venue"]!["id"]!
            .GetValue<string>()
            .Should()
            .Be(CatalogId("imported_venue", 74));
        imported["artist_venue"].Should().BeNull();
        var importedDetail = await GetJson(ImportedVenuePath(74));
        importedDetail["show_count"]!.GetValue<int>().Should().Be(2);
        importedDetail["shows"]!.AsArray().Should().HaveCount(2);
        var rawInventory = (await GetJson(BrowseArtistPath + "/imported-venues")).AsArray();
        JsonNode.DeepEquals(imported["imported_venue"], rawInventory.Single()).Should().BeTrue();
        var normalizedShow = await GetJson(ShowPath(4));
        JsonNode
            .DeepEquals(imported["imported_venue"], normalizedShow["imported_venue"])
            .Should()
            .BeTrue();

        var detail = await GetJson(BrowseArtistPath + "/venues/venue_00000001");
        JsonNode.DeepEquals(canonical["artist_venue"], ResourceMetadata(detail)).Should().BeTrue();
        var global = await GetJson(CanonicalVenuePath);
        JsonNode.DeepEquals(ResourceMetadata(global), detail["venue"]).Should().BeTrue();
        var shows = detail["shows"]!.AsArray();
        var direct = await GetJson("/api/v4/artist-venues/" + detail["id"]!.GetValue<string>());
        JsonNode.DeepEquals(direct, detail).Should().BeTrue();
        JsonNode
            .DeepEquals(detail, await GetJson(BrowseArtistPath + "/venues/by-slug/review-arena"))
            .Should()
            .BeTrue();
        JsonNode
            .DeepEquals(global, await GetJson("/api/v4/venues/by-slug/review-arena"))
            .Should()
            .BeTrue();
        canonical["artist_venue"]!.AsObject().ContainsKey("shows").Should().BeFalse();
        canonical["artist_venue"]!.AsObject().ContainsKey("artists").Should().BeFalse();
        shows.Select(show => show!["id"]!.GetValue<string>()).Should().Equal(ShowId(4));
        shows[0]!["imported_venue"]!["name"]!
            .GetValue<string>()
            .Should()
            .Be("Imported Civic Center");
        JsonNode
            .DeepEquals(ResourceMetadata(global), shows[0]!["artist_venue"]!["venue"])
            .Should()
            .BeTrue();

        var document = await GetJson("/api-docs/v4/swagger.json");
        AssertSchemaFields(document, "ArtistVenueEntryResource", canonical);
        AssertSchemaFields(document, "ArtistVenueWithShowsResource", detail);
        EnumValues(document, "ArtistVenueEntryResource", "kind")
            .Should()
            .BeEquivalentTo("artist_venue", "imported_venue");
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
        entries[0]!["artist_venue"].Should().BeNull();
        entries[0]!["imported_venue"]!["show_count"]!.GetValue<int>().Should().Be(1);
        var imported = entries[0]!["imported_venue"]!;
        imported["name"]!.GetValue<string>().Should().Be("Imported Civic Center");
        imported["artist_id"]!.GetValue<string>().Should().Be(CatalogId("artist", artistSuffix));
        using var unavailable = await http.GetAsync(
            ArtistPath(artistSuffix) + "/venues/venue_00000001"
        );
        unavailable.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var unavailableBySlug = await http.GetAsync(
            ArtistPath(artistSuffix) + "/venues/by-slug/review-arena"
        );
        unavailableBySlug.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Global_venue_detail_includes_only_approved_shows()
    {
        var detail = await GetJson(CanonicalVenuePath);
        var shows = detail["shows"]!.AsArray();
        shows
            .Select(show => show!["id"]!.GetValue<string>())
            .Should()
            .BeEquivalentTo(ShowId(3), ShowId(4));
        detail.AsObject().ContainsKey("artists").Should().BeFalse();
        (await GetJson("/api/v4/venues/venue_00000003"))["shows"]!.AsArray().Should().BeEmpty();
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
            venue["shows"]!.AsArray().Should().BeEmpty();
            var entries = (await GetJson(BrowseArtistPath + "/venues")).AsArray();
            entries.Should().ContainSingle();
            entries[0]!["kind"]!.GetValue<string>().Should().Be("imported_venue");
            entries[0]!["imported_venue"]!["show_count"]!.GetValue<int>().Should().Be(2);
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
            var empty = await GetJson(ImportedVenuePath(299));
            empty["shows"]!.AsArray().Should().BeEmpty();

            var detail = await GetJson(ImportedVenuePath(74));
            detail["id"]!.GetValue<string>().Should().Be(CatalogId("imported_venue", 74));
            detail["show_count"]!.GetValue<int>().Should().Be(2);
            detail["name"]!.GetValue<string>().Should().Be("Imported Civic Center");
            var shows = detail["shows"]!.AsArray();
            shows
                .Select(show => show!["id"]!.GetValue<string>())
                .Should()
                .BeEquivalentTo(ShowId(4), ShowId(10));
            shows.Single(show => show!["id"]!.GetValue<string>() == ShowId(4))!["artist_venue"]![
                "venue"
            ]!["id"]!
                .GetValue<string>()
                .Should()
                .Be("venue_00000001");
            shows
                .Single(show => show!["id"]!.GetValue<string>() == ShowId(10))!["artist_venue"]
                .Should()
                .BeNull();

            var document = await GetJson("/api-docs/v4/swagger.json");
            AssertSchemaFields(document, "ImportedVenueResource", rows[0]!);
            AssertSchemaFields(document, "ImportedVenueWithShowsResource", detail);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM venues WHERE id=910099");
        }
    }

    [Test]
    public async Task Imported_counts_revalidate_when_normalized_show_moves_to_another_imported_venue()
    {
        var browsePath = BrowseArtistPath + "/venues";
        using var initial = await http.GetAsync(browsePath);
        var etag = initial.Headers.ETag!.ToString();
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO venues(id,uuid,artist_id,name,location,slug,updated_at)
                VALUES(910099,'00000000-0000-0000-0000-000000000299',910004,'Recording Venue','Philadelphia, PA','moved-imported-review','2026-01-01');
                UPDATE shows SET venue_id=910099 WHERE id=910004;
                """
            );
            // The normalized group and unmatched show's membership have not changed;
            // only the full imported count changes. Imported timestamps stay untouched.
            using var changed = await Conditional(browsePath, etag);
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
            changed.Headers.ETag!.ToString().Should().NotBe(etag);
            var entries = JsonNode.Parse(await changed.Content.ReadAsStringAsync())!.AsArray();
            entries.Should().HaveCount(2);
            var imported = entries.Single(entry =>
                entry!["kind"]!.GetValue<string>() == "imported_venue"
            )!["imported_venue"]!;
            imported["id"]!.GetValue<string>().Should().Be(CatalogId("imported_venue", 74));
            imported["show_count"]!.GetValue<int>().Should().Be(1);
            var detail = await GetJson(ImportedVenuePath(74));
            detail["show_count"]!.GetValue<int>().Should().Be(1);
            detail["shows"]!.AsArray().Should().ContainSingle();
            var unmatchedShow = await GetJson(ShowPath(10));
            JsonNode.DeepEquals(imported, unmatchedShow["imported_venue"]).Should().BeTrue();
            var normalizedShow = await GetJson(ShowPath(4));
            normalizedShow["artist_venue"]!["artist_show_count"]!.GetValue<int>().Should().Be(1);
            normalizedShow["imported_venue"]!["show_count"]!.GetValue<int>().Should().Be(1);
            normalizedShow["sources"]![0]!["imported_venue"]!["show_count"]!
                .GetValue<int>()
                .Should()
                .Be(1);
            var refreshedYear = await GetJson(BrowseArtistPath + "/years/by-number/1995");
            refreshedYear["shows"]!
                .AsArray()
                .Select(show => show!["imported_venue"]!["show_count"]!.GetValue<int>())
                .Should()
                .OnlyContain(count => count == 1);
        }
        finally
        {
            await connection.ExecuteAsync(
                """
                UPDATE shows SET venue_id=910004 WHERE id=910004;
                DELETE FROM venues WHERE id=910099;
                """
            );
        }
        var recovered = await GetJson(ImportedVenuePath(74));
        recovered["show_count"]!.GetValue<int>().Should().Be(2);
        recovered["shows"]!.AsArray().Should().HaveCount(2);
    }

    [Test]
    public async Task Venue_merge_lookup_and_browse_follow_the_same_survivor_and_alias_union()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        var originalPairId = await StoredArtistVenueId(connection);
        try
        {
            await ChangeNormalization(
                connection,
                """
                INSERT INTO norm.venues(id,name,slug,author_type,author_ref,run_uuid)
                VALUES('venue_00000991','Survivor Venue','review-venue-survivor','importer','v4-review','00000000-0000-0000-0000-000000000099');
                INSERT INTO norm.venue_names(venue_id,normalized,name,kind,author_type,author_ref,run_uuid)
                VALUES('venue_00000991','survivor-alias','Survivor Alias','short','importer','v4-review','00000000-0000-0000-0000-000000000099');
                UPDATE norm.venues SET merged_into='venue_00000991' WHERE id='venue_00000001';
                """
            );
            var venue = await GetJson(CanonicalVenuePath);
            venue["id"]!.GetValue<string>().Should().Be("venue_00000991");
            var direct = await GetJson("/api/v4/venues/venue_00000991");
            JsonNode.DeepEquals(venue, direct).Should().BeTrue();
            venue["slug"]!.GetValue<string>().Should().Be("review-venue-survivor");
            JsonNode
                .DeepEquals(venue, await GetJson("/api/v4/venues/by-slug/review-arena"))
                .Should()
                .BeTrue();
            JsonNode
                .DeepEquals(venue, await GetJson("/api/v4/venues/by-slug/review-venue-survivor"))
                .Should()
                .BeTrue();
            venue["names"]!
                .AsArray()
                .Should()
                .Contain(alias => alias!["name"]!.GetValue<string>() == "Historical Arena");
            venue["names"]!
                .AsArray()
                .Should()
                .Contain(alias => alias!["name"]!.GetValue<string>() == "Survivor Alias");
            var entry = await GetJson(BrowseArtistPath + "/venues/venue_00000001");
            JsonNode
                .DeepEquals(entry, await GetJson(BrowseArtistPath + "/venues/by-slug/review-arena"))
                .Should()
                .BeTrue();
            JsonNode.DeepEquals(ResourceMetadata(venue), entry["venue"]).Should().BeTrue();
            JsonNode
                .DeepEquals(entry, await GetJson("/api/v4/artist-venues/" + originalPairId))
                .Should()
                .BeTrue();
            venue["shows"]!
                .AsArray()
                .Select(show => show!["artist_venue"]!["venue"]!["id"]!.GetValue<string>())
                .Should()
                .OnlyContain(id => id == "venue_00000991");
        }
        finally
        {
            await ChangeNormalization(
                connection,
                """
                UPDATE norm.venues SET merged_into=NULL WHERE id='venue_00000001';
                DELETE FROM norm.venue_names WHERE venue_id='venue_00000991';
                DELETE FROM norm.venues WHERE id='venue_00000991';
                """
            );
        }
    }

    [TestCase("/api/v4/venues/venue_bad", HttpStatusCode.UnprocessableEntity)]
    [TestCase("/api/v4/venues/song_00000001", HttpStatusCode.UnprocessableEntity)]
    [TestCase("/api/v4/venues/venue_zzzzzzzz", HttpStatusCode.NotFound)]
    [TestCase("/api/v4/venues/by-slug/unknown-venue", HttpStatusCode.NotFound)]
    [TestCase("/api/v4/artist-venues/venue_00000001", HttpStatusCode.UnprocessableEntity)]
    [TestCase("/api/v4/artist-venues/artist_venue_zzzzzzzz", HttpStatusCode.NotFound)]
    [TestCase("/api/v4/imported-venues/venue_00000001", HttpStatusCode.UnprocessableEntity)]
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
            entries[0]!["imported_venue"]!["show_count"]!.GetValue<int>().Should().Be(2);
            var show = await GetJson(ShowPath(4));
            show["artist_venue"].Should().BeNull();
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
                BrowseArtistPath + "/venues/venue_zzzzzzzz",
                ImportedVenuePath(999),
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
            entries[0]!["artist_venue"]!["artist_show_count"]!.GetValue<int>().Should().Be(2);
            var detail = await GetJson(BrowseArtistPath + "/venues/venue_00000001");
            JsonNode
                .DeepEquals(entries[0]!["artist_venue"], ResourceMetadata(detail))
                .Should()
                .BeTrue();
            var importedDetail = await GetJson(ImportedVenuePath(74));
            importedDetail["show_count"]!.GetValue<int>().Should().Be(2);
            importedDetail["shows"]!.AsArray().Should().HaveCount(2);
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
            source["imported_venue"]!["show_count"]!.GetValue<int>().Should().Be(0);
            var sourceOnlyVenue = await GetJson(ImportedVenuePath(299));
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
            venueGroups.Single(item => item!["kind"]!.GetValue<string>() == "artist_venue")![
                "artist_venue"
            ]!["artist_show_count"]!
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
