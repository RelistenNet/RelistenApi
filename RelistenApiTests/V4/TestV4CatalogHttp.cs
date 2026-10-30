using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Relisten;
using Relisten.Controllers.V4;
using Relisten.Identifiers;

namespace RelistenApiTests.V4;

/// <summary>
/// Exercises the native catalog through real HTTP, production controllers and SQL.
/// Synthetic edge cases run only in a disposable test database; no upstream calls run.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestV4CatalogHttp
{
    private WebApplication app = null!;
    private HttpClient http = null!;
    private string previousPrimary = null!;
    private string previousReplica = null!;

    [OneTimeSetUp]
    public async Task Start()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        await using var connection = await MigratedTestDatabase.OpenAsync();
        await V4CatalogFixture.EnsureLoaded(connection);
        previousPrimary = DbService.ConnStr;
        previousReplica = DbService.ReadOnlyConnStr;
        var db = new DbService(
            LocalTestDatabase.DatabaseUrl,
            null,
            null,
            new HostingEnvironment { EnvironmentName = Environments.Production },
            NullLogger<DbService>.Instance
        );
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(db);
        builder.Services.AddTransient<
            IConfigureOptions<MvcNewtonsoftJsonOptions>,
            RelistenApiJsonOptionsWrapper
        >();
        builder
            .Services.AddControllers(options => options.EnableEndpointRouting = false)
            .AddApplicationPart(typeof(CatalogReadController).Assembly)
            .AddNewtonsoftJson();
        builder.Services.AddV4Catalog();
        builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v2", new OpenApiInfo { Title = "Legacy", Version = "v2" });
            options.SwaggerDoc("v3", new OpenApiInfo { Title = "Legacy", Version = "v3" });
        });
        builder.Services.AddSwaggerGenNewtonsoftSupport();
        app = builder.Build();
        app.UseDeveloperExceptionPage();
        app.UseResponseCompression();
        app.UseV4CatalogCors();
        app.UseCors(policy =>
            policy.WithMethods("GET", "POST", "OPTIONS", "HEAD").WithOrigins("*").AllowAnyMethod()
        );
        app.UseMvc();
        app.UseSwagger(options => options.RouteTemplate = "api-docs/{documentName}/swagger.json");
        await app.StartAsync();
        var address = app
            .Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        http = new HttpClient { BaseAddress = new Uri(address) };
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        http?.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync();
        }
        DbService.ConnStr = previousPrimary;
        DbService.ReadOnlyConnStr = previousReplica;
    }

    [TestCase(1, "unavailable", "disabled")]
    [TestCase(2, "unavailable", "disabled")]
    [TestCase(3, "partial", null)]
    [TestCase(4, "interpreted", null)]
    public async Task Track_and_nested_show_preserve_playback_and_publish_the_same_interpretation(
        int scenario,
        string state,
        string? reason
    )
    {
        var track = await GetJson(TrackPath(scenario));
        var show = await GetJson(ShowPath(scenario));
        track["id"]!.GetValue<string>().Should().Be(TrackId(scenario));
        track["source_id"]!.GetValue<string>().Should().StartWith("source_");
        track["show_id"]!.GetValue<string>().Should().Be(ShowId(scenario));
        track["title"]!.GetValue<string>().Should().Be("Scarlet Begonias > Fire on the Mountain");
        track["mp3_url"]!.GetValue<string>().Should().EndWith($"/{scenario}.mp3");
        track["normalization"]!["state"]!.GetValue<string>().Should().Be(state);
        (track["normalization"]!["reason"]?.GetValue<string>()).Should().Be(reason);
        var nested = show["sources"]![0]!["sets"]![0]!["tracks"]![0]!;
        JsonNode.DeepEquals(track, nested).Should().BeTrue();
        show.AsObject().ContainsKey("meta").Should().BeFalse();
        show.AsObject().ContainsKey("venue_display").Should().BeFalse();
        show.AsObject().ContainsKey("normalized_venue").Should().BeFalse();
        show["imported_venue"]!["name"]!.GetValue<string>().Should().Be("Imported Civic Center");
        if (scenario >= 3)
        {
            var parts = track["normalization"]!["parts"]!.AsArray();
            parts.Should().HaveCount(2);
            parts[0]!["song"]!["id"]!.GetValue<string>().Should().StartWith("song_");
            parts[1]!["state"]!
                .GetValue<string>()
                .Should()
                .Be(scenario == 3 ? "unresolved" : "matched");
        }
        else
        {
            show["venue"].Should().BeNull();
            track["normalization"]!["parts"]!.AsArray().Should().BeEmpty();
        }
    }

    [Test]
    public async Task Conditional_reads_detect_normalization_only_changes_and_work_with_compression_and_Cors()
    {
        using var initial = await http.GetAsync(ShowPath(4));
        initial
            .StatusCode.Should()
            .Be(HttpStatusCode.OK, await initial.Content.ReadAsStringAsync());
        var etag = initial.Headers.ETag!;
        etag.IsWeak.Should().BeTrue();
        var before = JsonNode.Parse(await initial.Content.ReadAsStringAsync())!;
        using var unchanged = await Conditional(ShowPath(4), etag.ToString());
        unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await unchanged.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        unchanged.Headers.ETag.Should().Be(etag);
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await ChangeNormalization(
                connection,
                "UPDATE norm.track_songs SET notes=ARRAY['Reviewed normalization correction'] WHERE track_uuid='00000000-0000-0000-0000-000000000044' AND part=1"
            );
            using var changed = await Conditional(ShowPath(4), etag.ToString());
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
            changed.Headers.ETag.Should().NotBe(etag);
            var after = JsonNode.Parse(await changed.Content.ReadAsStringAsync())!;
            after["updated_at"]!.ToJsonString().Should().Be(before["updated_at"]!.ToJsonString());
            after["sources"]![0]!["sets"]![0]!["tracks"]![0]!["normalization"]!["parts"]![0]![
                "notes"
            ]![0]!
                .GetValue<string>()
                .Should()
                .Be("Reviewed normalization correction");
            using var compressed = new HttpRequestMessage(HttpMethod.Get, ShowPath(4));
            compressed.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            compressed.Headers.Add("Origin", "https://review.example");
            using var response = await http.SendAsync(compressed);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.ETag.Should().Be(changed.Headers.ETag);
            response.Content.Headers.ContentEncoding.Should().Contain("gzip");
            response.Headers.Vary.Should().Contain("Accept-Encoding");
            response
                .Headers.GetValues("Access-Control-Expose-Headers")
                .Should()
                .Contain(value => value.Contains("ETag", StringComparison.OrdinalIgnoreCase));
            using var gzipConditional = await Conditional(
                ShowPath(4),
                changed.Headers.ETag!.ToString(),
                gzip: true
            );
            gzipConditional.StatusCode.Should().Be(HttpStatusCode.NotModified);
        }
        finally
        {
            await ChangeNormalization(
                connection,
                "UPDATE norm.track_songs SET notes='{}'::text[] WHERE track_uuid='00000000-0000-0000-0000-000000000044' AND part=1"
            );
        }
    }

    [Test]
    public async Task Canonical_merges_follow_survivors_without_rewriting_imported_playback_titles()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await ChangeNormalization(
                connection,
                """
                INSERT INTO norm.venues (id,name,slug,author_type,author_ref,run_uuid)
                VALUES ('venue_00000002','Survivor Arena','survivor-review-arena','importer','v4-review','00000000-0000-0000-0000-000000000099');
                INSERT INTO norm.venue_names (venue_id,normalized,name,kind,valid_from,valid_to,author_type,author_ref,run_uuid)
                VALUES ('venue_00000002','survivor-official','Survivor Official','official','1996-01-01',NULL,'importer','v4-review','00000000-0000-0000-0000-000000000099'),
                       ('venue_00000002','historical-arena','Historical Arena','official','1990-01-01','2000-01-01','importer','v4-review','00000000-0000-0000-0000-000000000099'),
                       ('venue_00000002','historical-arena','Historical Arena','spelling','-infinity',NULL,'importer','v4-review','00000000-0000-0000-0000-000000000099');
                UPDATE norm.venues SET merged_into='venue_00000002' WHERE id='venue_00000001';
                UPDATE norm.songs SET merged_into='song_00000004' WHERE id='song_00000003';
                """
            );
            var show = await GetJson(ShowPath(4));
            show["venue"]!["id"]!.GetValue<string>().Should().Be("venue_00000002");
            show["imported_venue"]!["name"]!
                .GetValue<string>()
                .Should()
                .Be("Imported Civic Center");
            var aliases = show["venue"]!["names"]!.AsArray();
            aliases.Should().HaveCount(7);
            aliases
                .Should()
                .Contain(item => item!["name"]!.GetValue<string>() == "Survivor Official");
            aliases
                .Where(item => item!["name"]!.GetValue<string>() == "Historical Arena")
                .Select(item => item!["kind"]!.GetValue<string>())
                .Should()
                .BeEquivalentTo("official", "spelling");
            var track = await GetJson(TrackPath(4));
            track["title"]!
                .GetValue<string>()
                .Should()
                .Be("Scarlet Begonias > Fire on the Mountain");
            track["normalization"]!["parts"]![0]!["song"]!["id"]!
                .GetValue<string>()
                .Should()
                .Be("song_00000004");
        }
        finally
        {
            await ChangeNormalization(
                connection,
                """
                UPDATE norm.songs SET merged_into=NULL WHERE id='song_00000003';
                UPDATE norm.venues SET merged_into=NULL WHERE id='venue_00000001';
                DELETE FROM norm.venue_names WHERE venue_id='venue_00000002';
                DELETE FROM norm.venues WHERE id='venue_00000002';
                """
            );
        }
    }

    [Test]
    public async Task Resolved_non_song_and_uninterpreted_tracks_remain_distinct()
    {
        var nonSong = await GetJson(TrackPath(5));
        nonSong["normalization"]!["state"]!.GetValue<string>().Should().Be("interpreted");
        var part = nonSong["normalization"]!["parts"]![0]!;
        part["state"]!.GetValue<string>().Should().Be("non_song");
        part["song"].Should().BeNull();
        var unknown = await GetJson(TrackPath(6));
        unknown["normalization"]!["state"]!.GetValue<string>().Should().Be("unavailable");
        unknown["normalization"]!["reason"]!.GetValue<string>().Should().Be("not_interpreted");
        unknown["normalization"]!["parts"]!.AsArray().Should().BeEmpty();
    }

    [Test]
    public async Task Database_failure_returns_a_retryable_problem_instead_of_partial_data()
    {
        var primary = DbService.ConnStr;
        try
        {
            DbService.ConnStr =
                "Host=127.0.0.1;Port=1;Database=unavailable;Username=relisten;Timeout=1";
            using var response = await http.GetAsync(ShowPath(4));
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            problem["code"]!.GetValue<string>().Should().Be("catalog_unavailable");
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
        finally
        {
            DbService.ConnStr = primary;
        }
        (await GetJson(ShowPath(4)))["id"]!.GetValue<string>().Should().Be(ShowId(4));
    }

    [Test]
    public async Task Invalid_kind_and_unknown_resource_have_distinct_problem_responses()
    {
        using var invalid = await http.GetAsync("/api/v4/source-tracks/" + ShowId(4));
        invalid.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        JsonNode.Parse(await invalid.Content.ReadAsStringAsync())!["code"]!
            .GetValue<string>()
            .Should()
            .Be("invalid_id");
        invalid.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var missing = await http.GetAsync(
            "/api/v4/shows/"
                + UuidTypeId.Format("show", Guid.Parse("00000000-0000-0000-0000-000000000009"))
        );
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        JsonNode.Parse(await missing.Content.ReadAsStringAsync())!["code"]!
            .GetValue<string>()
            .Should()
            .Be("not_found");
    }

    [Test]
    public async Task Nullable_resource_references_serialize_as_strings_or_null()
    {
        var show = await GetJson(ShowPath(4));
        show["year_id"]!.GetValue<string>().Should().StartWith("year_");
        show["tour_id"]!.GetValue<string>().Should().StartWith("tour_");
        show["venue"]!["parent_id"]!.GetValue<string>().Should().Be("venue_00000003");
        var withoutTour = await GetJson(ShowPath(1));
        withoutTour["tour_id"].Should().BeNull();
        withoutTour["venue"].Should().BeNull();
    }

    [Test]
    public async Task Browser_preflight_permits_conditional_catalog_reads()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, ShowPath(4));
        request.Headers.Add("Origin", "https://review.example");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "if-none-match");
        using var response = await http.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response
            .Headers.GetValues("Access-Control-Allow-Headers")
            .Should()
            .Contain(value => value.Contains("if-none-match", StringComparison.OrdinalIgnoreCase));
        response.Headers.GetValues("Access-Control-Allow-Methods").Should().Contain("GET");
    }

    [Test]
    public async Task Artist_listing_and_detail_preserve_provider_identity_without_internal_keys()
    {
        var artists = (await GetJson("/api/v4/artists")).AsArray();
        artists
            .Select(artist => artist!["name"]!.GetValue<string>())
            .Should()
            .Equal("Raw Artist", "Matched Artist", "Partial Artist");
        var listed = artists.Single(artist => artist!["id"]!.GetValue<string>() == BrowseArtistId)!;
        var detail = await GetJson(BrowseArtistPath);
        JsonNode.DeepEquals(listed, detail).Should().BeTrue();
        detail["show_count"]!.GetValue<int>().Should().Be(2);
        detail["source_count"]!.GetValue<int>().Should().Be(2);
        detail["capabilities"]!["normalization"]!.GetValue<bool>().Should().BeTrue();
        detail["capabilities"]!.AsObject().ContainsKey("id").Should().BeFalse();
        var assignment = detail["upstream_sources"]![0]!;
        assignment["provider"]!.GetValue<string>().Should().Be("review.provider");
        assignment["upstream_identifier"]!.GetValue<string>().Should().Be("matched-upstream");
        assignment.AsObject().ContainsKey("upstream_source_id").Should().BeFalse();
        assignment.AsObject().ContainsKey("artist_id").Should().BeFalse();

        var includingAutoCreated = (
            await GetJson("/api/v4/artists?include_autocreated=true")
        ).AsArray();
        includingAutoCreated
            .Select(artist => artist!["name"]!.GetValue<string>())
            .Should()
            .Equal("Disabled Artist", "Raw Artist", "Matched Artist", "Partial Artist");
    }

    [Test]
    public async Task Artist_year_show_and_track_resources_form_one_navigable_catalog()
    {
        var years = (await GetJson(BrowseArtistPath + "/years")).AsArray();
        years.Select(year => year!["year"]!.GetValue<string>()).Should().Equal("1994", "1995");
        foreach (var year in years)
        {
            year!["artist_id"]!.GetValue<string>().Should().Be(BrowseArtistId);
            var yearId = year["id"]!.GetValue<string>();
            yearId.Should().StartWith("year_");
            var byId = await GetJson(BrowseArtistPath + "/years/" + yearId);
            var byNumber = await GetJson(
                BrowseArtistPath + "/years/by-number/" + year["year"]!.GetValue<string>()
            );
            JsonNode.DeepEquals(byId, byNumber).Should().BeTrue();
            foreach (var property in year.AsObject())
            {
                JsonNode
                    .DeepEquals(property.Value, byId[property.Key])
                    .Should()
                    .BeTrue(property.Key);
            }
            var shows = byId["shows"]!.AsArray();
            shows.Should().HaveCount(year["show_count"]!.GetValue<int>());
            foreach (var summary in shows)
            {
                summary!["artist_id"]!.GetValue<string>().Should().Be(BrowseArtistId);
                summary["year_id"]!.GetValue<string>().Should().Be(yearId);
                summary.AsObject().ContainsKey("sources").Should().BeFalse();
                var full = await GetJson("/api/v4/shows/" + summary["id"]!.GetValue<string>());
                foreach (var property in summary.AsObject())
                {
                    JsonNode
                        .DeepEquals(property.Value, full[property.Key])
                        .Should()
                        .BeTrue(property.Key);
                }
                var track = full["sources"]![0]!["sets"]![0]!["tracks"]![0]!;
                var direct = await GetJson(
                    "/api/v4/source-tracks/" + track["id"]!.GetValue<string>()
                );
                JsonNode.DeepEquals(track, direct).Should().BeTrue();
            }
        }

        var allShows = (await GetJson(BrowseArtistPath + "/shows")).AsArray();
        allShows
            .Select(show => show!["display_date"]!.GetValue<string>())
            .Should()
            .Equal("1994-07-01", "1995-12-31");
        allShows[0]!["imported_venue"]!["name"]!
            .GetValue<string>()
            .Should()
            .Be("Imported Civic Center");
        allShows[1]!["imported_venue"]!["name"]!
            .GetValue<string>()
            .Should()
            .Be("Imported Civic Center");
    }

    [Test]
    public async Task Calendar_year_lookup_preserves_imported_leading_zero_labels()
    {
        var artistPath = "/api/v4/artists/" + CatalogId("artist", 11);
        var byId = await GetJson(artistPath + "/years/" + CatalogId("year", 23));
        var byNumber = await GetJson(artistPath + "/years/by-number/23");
        var byPaddedNumber = await GetJson(artistPath + "/years/by-number/0023");
        byId["year"]!.GetValue<string>().Should().Be("0023");
        byId["shows"]!.AsArray().Should().BeEmpty();
        JsonNode.DeepEquals(byId, byNumber).Should().BeTrue();
        JsonNode.DeepEquals(byId, byPaddedNumber).Should().BeTrue();
    }

    [Test]
    public async Task Known_empty_artist_collections_are_arrays_and_unknown_artists_are_not_found()
    {
        var emptyPath = "/api/v4/artists/" + CatalogId("artist", 20);
        (await GetJson(emptyPath))["show_count"]!.GetValue<int>().Should().Be(0);
        foreach (var collection in new[] { "/years", "/shows" })
        {
            (await GetJson(emptyPath + collection)).AsArray().Should().BeEmpty();
            using var unknown = await http.GetAsync(
                "/api/v4/artists/" + CatalogId("artist", 99) + collection
            );
            unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [TestCase("years/show_00000000000000000000000000", HttpStatusCode.UnprocessableEntity)]
    [TestCase("years/by-number/0", HttpStatusCode.UnprocessableEntity)]
    [TestCase("years/by-number/10000", HttpStatusCode.UnprocessableEntity)]
    [TestCase("years/by-number/not-a-year", HttpStatusCode.UnprocessableEntity)]
    public async Task Browse_routes_reject_malformed_year_identity_and_calendar_values(
        string suffix,
        HttpStatusCode status
    )
    {
        using var response = await http.GetAsync(BrowseArtistPath + "/" + suffix);
        response.StatusCode.Should().Be(status);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Test]
    public async Task Artist_listing_rejects_invalid_boolean_filters()
    {
        using var response = await http.GetAsync("/api/v4/artists?include_autocreated=maybe");
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Test]
    public async Task Year_lookup_does_not_cross_artist_ownership()
    {
        using var foreignYear = await http.GetAsync(
            BrowseArtistPath + "/years/" + CatalogId("year", 61)
        );
        foreignYear.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var unknownYear = await http.GetAsync(
            BrowseArtistPath + "/years/" + CatalogId("year", 99)
        );
        unknownYear.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var missingCalendarYear = await http.GetAsync(
            BrowseArtistPath + "/years/by-number/1993"
        );
        missingCalendarYear.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var malformedArtist = await http.GetAsync("/api/v4/artists/" + ShowId(4) + "/years");
        malformedArtist.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Test]
    public async Task Show_list_revalidation_detects_venue_alias_corrections_without_recording_updates()
    {
        using var initial = await http.GetAsync(BrowseArtistPath + "/shows");
        initial.StatusCode.Should().Be(HttpStatusCode.OK);
        var before = JsonNode.Parse(await initial.Content.ReadAsStringAsync())!.AsArray();
        var etag = initial.Headers.ETag!.ToString();
        using var unchanged = await Conditional(BrowseArtistPath + "/shows", etag);
        unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await ChangeNormalization(
                connection,
                "UPDATE norm.venue_names SET name='Reviewed Historical Arena' WHERE venue_id='venue_00000001' AND normalized='historical-arena'"
            );
            using var changed = await Conditional(BrowseArtistPath + "/shows", etag);
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
            changed.Headers.ETag!.ToString().Should().NotBe(etag);
            var after = JsonNode.Parse(await changed.Content.ReadAsStringAsync())!.AsArray();
            after[1]!["venue"]!["names"]!
                .AsArray()
                .Should()
                .Contain(item => item!["name"]!.GetValue<string>() == "Reviewed Historical Arena");
            after[1]!["imported_venue"]!["name"]!
                .GetValue<string>()
                .Should()
                .Be(before[1]!["imported_venue"]!["name"]!.GetValue<string>());
            after[1]!["updated_at"]!
                .ToJsonString()
                .Should()
                .Be(before[1]!["updated_at"]!.ToJsonString());
        }
        finally
        {
            await ChangeNormalization(
                connection,
                "UPDATE norm.venue_names SET name='Historical Arena' WHERE venue_id='venue_00000001' AND normalized='historical-arena'"
            );
        }
    }

    [Test]
    public async Task Venue_aliases_are_distinct_typed_names_shared_across_navigation()
    {
        var show = await GetJson(ShowPath(4));
        var venue = show["venue"]!;
        venue["name"]!.GetValue<string>().Should().Be("Current Arena");
        show["imported_venue"]!["name"]!.GetValue<string>().Should().Be("Imported Civic Center");
        var names = venue["names"]!.AsArray();
        names
            .Select(item => (item!["name"]!.GetValue<string>(), item["kind"]!.GetValue<string>()))
            .Should()
            .Equal(
                ("Current Arena", "official"),
                ("Historical Arena", "official"),
                ("Original Arena", "official"),
                ("Arena", "short"),
                ("Historical Areena", "spelling")
            );
        foreach (var name in names)
        {
            name!
                .AsObject()
                .Select(property => property.Key)
                .Should()
                .BeEquivalentTo("name", "kind");
        }

        var summary = (await GetJson(BrowseArtistPath + "/shows"))
            .AsArray()
            .Single(item => item!["id"]!.GetValue<string>() == ShowId(4))!;
        var year = await GetJson(BrowseArtistPath + "/years/by-number/1995");
        JsonNode.DeepEquals(venue, summary["venue"]).Should().BeTrue();
        JsonNode.DeepEquals(venue, year["shows"]![0]!["venue"]).Should().BeTrue();
        summary["imported_venue"]!["name"]!.GetValue<string>().Should().Be("Imported Civic Center");
        summary.AsObject().ContainsKey("venue_display").Should().BeFalse();
        year["shows"]![0]!.AsObject().ContainsKey("venue_display").Should().BeFalse();

        var document = await GetJson("/api-docs/v4/swagger.json");
        AssertSchemaFields(document, "VenueResource", venue);
        AssertSchemaFields(document, "VenueNameResource", names[0]!);
        EnumValues(document, "VenueNameResource", "kind")
            .Should()
            .BeEquivalentTo("official", "short", "spelling");
    }

    [Test]
    public async Task Date_only_alias_corrections_do_not_invalidate_catalog_responses()
    {
        var paths = new[]
        {
            ShowPath(4),
            BrowseArtistPath + "/shows",
            BrowseArtistPath + "/years/by-number/1995",
        };
        var validators = new Dictionary<string, string>();
        foreach (var path in paths)
        {
            using var initial = await http.GetAsync(path);
            initial.StatusCode.Should().Be(HttpStatusCode.OK);
            validators[path] = initial.Headers.ETag!.ToString();
        }
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await ChangeNormalization(
                connection,
                "UPDATE norm.venue_names SET valid_to='2001-01-01' WHERE venue_id='venue_00000001' AND normalized='historical-arena' AND valid_from='1990-01-01'"
            );
            foreach (var path in paths)
            {
                using var unchanged = await Conditional(path, validators[path]);
                unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);
                unchanged.Headers.ETag!.ToString().Should().Be(validators[path]);
            }
        }
        finally
        {
            await ChangeNormalization(
                connection,
                "UPDATE norm.venue_names SET valid_to='2000-01-01' WHERE venue_id='venue_00000001' AND normalized='historical-arena' AND valid_from='1990-01-01'"
            );
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Venue_alias_name_or_kind_corrections_refresh_metadata_without_changing_imported_display(
        bool rename
    )
    {
        using var initial = await http.GetAsync(ShowPath(4));
        initial.StatusCode.Should().Be(HttpStatusCode.OK);
        var before = JsonNode.Parse(await initial.Content.ReadAsStringAsync())!;
        var etag = initial.Headers.ETag!.ToString();
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await ChangeNormalization(
                connection,
                rename
                    ? "UPDATE norm.venue_names SET name='Reviewed Areena' WHERE venue_id='venue_00000001' AND normalized='historical-areena'"
                    : "UPDATE norm.venue_names SET kind='short' WHERE venue_id='venue_00000001' AND normalized='historical-areena'"
            );
            using var response = await Conditional(ShowPath(4), etag);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.ETag!.ToString().Should().NotBe(etag);
            var after = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            after["updated_at"]!.ToJsonString().Should().Be(before["updated_at"]!.ToJsonString());
            after["venue"]!["name"]!.GetValue<string>().Should().Be("Current Arena");
            JsonNode
                .DeepEquals(after["imported_venue"], before["imported_venue"])
                .Should()
                .BeTrue();
            after["venue"]!["names"]!
                .AsArray()
                .Should()
                .Contain(item =>
                    item!["name"]!.GetValue<string>()
                        == (rename ? "Reviewed Areena" : "Historical Areena")
                    && item["kind"]!.GetValue<string>() == (rename ? "spelling" : "short")
                );
        }
        finally
        {
            await ChangeNormalization(
                connection,
                "UPDATE norm.venue_names SET name='Historical Areena',kind='spelling' WHERE venue_id='venue_00000001' AND normalized='historical-areena'"
            );
        }
    }

    [Test]
    public async Task Imported_venue_remains_displayable_when_normalized_venue_has_no_aliases()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await ChangeNormalization(
                connection,
                "UPDATE norm.show_venues SET venue_id='venue_00000003' WHERE show_uuid='00000000-0000-0000-0000-000000000004'"
            );
            var show = await GetJson(ShowPath(4));
            show["venue"]!["names"]!.AsArray().Should().BeEmpty();
            show["venue"]!["id"]!.GetValue<string>().Should().Be("venue_00000003");
            show["imported_venue"]!["name"]!
                .GetValue<string>()
                .Should()
                .Be("Imported Civic Center");
        }
        finally
        {
            await ChangeNormalization(
                connection,
                "UPDATE norm.show_venues SET venue_id='venue_00000001' WHERE show_uuid='00000000-0000-0000-0000-000000000004'"
            );
        }
    }

    private static string BrowseArtistId => CatalogId("artist", 14);
    private static string BrowseArtistPath => "/api/v4/artists/" + BrowseArtistId;

    private static string CatalogId(string prefix, int suffix) =>
        UuidTypeId.Format(prefix, Guid.Parse($"00000000-0000-0000-0000-{suffix:000000000000}"));

    [Test]
    public async Task Openapi_property_names_and_id_types_match_the_actual_json()
    {
        var document = await GetJson("/api-docs/v4/swagger.json");
        var show = await GetJson(ShowPath(4));
        var track = await GetJson(TrackPath(4));
        AssertSchemaFields(document, "ShowResource", show);
        AssertSchemaFields(document, "TrackResource", track);
        AssertSchemaFields(document, "TrackNormalizationResource", track["normalization"]!);
        AssertSchemaFields(document, "TrackPartResource", track["normalization"]!["parts"]![0]!);
        AssertSchemaFields(
            document,
            "SongResource",
            track["normalization"]!["parts"]![0]!["song"]!
        );
        var trackProperties = document["components"]!["schemas"]!["TrackResource"]!["properties"]!;
        foreach (var field in new[] { "id", "artist_id", "show_id", "source_id", "source_set_id" })
        {
            trackProperties[field]!["type"]!.GetValue<string>().Should().Be("string");
            trackProperties[field]!["format"]?.GetValue<string>().Should().NotBe("uuid");
        }
    }

    [Test]
    public async Task Openapi_nullable_object_references_match_raw_and_partial_responses()
    {
        var document = await GetJson("/api-docs/v4/swagger.json");
        var raw = await GetJson(ShowPath(1));
        raw["venue"].Should().BeNull();
        raw["era"].Should().BeNull();
        var partial = await GetJson(TrackPath(3));
        partial["normalization"]!["parts"]![1]!["song"].Should().BeNull();
        foreach (
            var (resource, property) in new[]
            {
                ("ShowResource", "venue"),
                ("ShowResource", "era"),
                ("ShowSummaryResource", "venue"),
                ("ShowSummaryResource", "era"),
                ("TrackPartResource", "song"),
            }
        )
        {
            var schema = document["components"]!["schemas"]![resource]!["properties"]![property]!;
            schema["type"]!.GetValue<string>().Should().Be("object");
            schema["nullable"]!.GetValue<bool>().Should().BeTrue();
            schema["$ref"]
                .Should()
                .BeNull("nullable reference siblings are ignored in OpenAPI 3.0");
        }
        foreach (
            var (resource, property) in new[]
            {
                ("TrackResource", "normalization"),
                ("ArtistResource", "capabilities"),
            }
        )
        {
            var schema = document["components"]!["schemas"]![resource]!["properties"]![property]!;
            if (schema["$ref"] is { } reference)
            {
                schema = document["components"]!["schemas"]![
                    reference.GetValue<string>().Split('/').Last()
                ]!;
            }
            (schema["nullable"]?.GetValue<bool>() ?? false).Should().BeFalse();
        }
        foreach (var name in new[] { "VenueResource", "EraResource", "SongResource" })
        {
            var definition = document["components"]!["schemas"]![name]!;
            (definition["nullable"]?.GetValue<bool>() ?? false).Should().BeFalse();
        }
    }

    [Test]
    public async Task Openapi_closed_vocabularies_admit_the_unchanged_wire_strings()
    {
        var document = await GetJson("/api-docs/v4/swagger.json");
        EnumValues(document, "TrackNormalizationResource", "state")
            .Should()
            .BeEquivalentTo("unavailable", "partial", "interpreted");
        EnumValues(document, "TrackNormalizationResource", "reason")
            .Should()
            .BeEquivalentTo("disabled", "not_interpreted", "inconsistent");
        EnumValues(document, "TrackPartResource", "state")
            .Should()
            .BeEquivalentTo("matched", "non_song", "unresolved");
        EnumValues(document, "SongResource", "kind")
            .Should()
            .BeEquivalentTo("song", "jam", "segment", "tuning", "banter", "other");
        EnumValues(document, "VenueResource", "kind").Should().BeEquivalentTo("site", "room");
        EnumValues(document, "VenueResource", "coordinate_precision")
            .Should()
            .BeEquivalentTo("venue", "city");
        EnumValues(document, "SourceResource", "flac_type")
            .Should()
            .BeEquivalentTo("NoFlac", "Flac16Bit", "Flac24Bit", "NoPlayableFlac", "Unknown");
        EnumValues(document, "TrackPartResource", "qualifiers", arrayItems: true)
            .Should()
            .BeEquivalentTo(
                "reprise",
                "partial",
                "unfinished",
                "instrumental",
                "acoustic",
                "with_guest",
                "continued",
                "tape_flip",
                "soundcheck",
                "tease",
                "intro",
                "outro",
                "jam"
            );

        foreach (var scenario in new[] { 1, 3, 4, 5, 6 })
        {
            var track = await GetJson(TrackPath(scenario));
            var normalization = track["normalization"]!;
            EnumValues(document, "TrackNormalizationResource", "state")
                .Should()
                .Contain(normalization["state"]!.GetValue<string>());
            if (normalization["reason"] is { } reason)
            {
                EnumValues(document, "TrackNormalizationResource", "reason")
                    .Should()
                    .Contain(reason.GetValue<string>());
            }
            foreach (var part in normalization["parts"]!.AsArray())
            {
                EnumValues(document, "TrackPartResource", "state")
                    .Should()
                    .Contain(part!["state"]!.GetValue<string>());
                if (part["song"] is { } song)
                {
                    EnumValues(document, "SongResource", "kind")
                        .Should()
                        .Contain(song["kind"]!.GetValue<string>());
                }
            }
        }
        var show = await GetJson(ShowPath(4));
        EnumValues(document, "VenueResource", "kind")
            .Should()
            .Contain(show["venue"]!["kind"]!.GetValue<string>());
        EnumValues(document, "SourceResource", "flac_type")
            .Should()
            .Contain(show["sources"]![0]!["flac_type"]!.GetValue<string>());
        show["venue"]!["coordinate_precision"].Should().BeNull();
    }

    [Test]
    public async Task Nullable_categories_remain_null_and_qualifiers_preserve_underscores()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await ChangeNormalization(
                connection,
                """
                UPDATE norm.songs SET kind=NULL WHERE id='song_00000003';
                UPDATE norm.track_songs SET qualifiers=ARRAY['with_guest','tape_flip']
                WHERE track_uuid='00000000-0000-0000-0000-000000000044' AND part=1;
                """
            );
            var track = await GetJson(TrackPath(4));
            var normalization = track["normalization"]!;
            normalization["reason"].Should().BeNull();
            var part = normalization["parts"]![0]!;
            part["song"]!["kind"].Should().BeNull();
            var qualifiers = part["qualifiers"]!
                .AsArray()
                .Select(value => value!.GetValue<string>())
                .ToArray();
            qualifiers.Should().Equal("with_guest", "tape_flip");
            var document = await GetJson("/api-docs/v4/swagger.json");
            EnumValues(document, "TrackPartResource", "qualifiers", arrayItems: true)
                .Should()
                .Contain(qualifiers);
            var show = await GetJson(ShowPath(4));
            show["venue"]!["coordinate_precision"].Should().BeNull();
            foreach (
                var (resource, property) in new[]
                {
                    ("TrackNormalizationResource", "reason"),
                    ("SongResource", "kind"),
                    ("VenueResource", "coordinate_precision"),
                }
            )
            {
                var schema = document["components"]!["schemas"]![resource]!["properties"]![
                    property
                ]!;
                // OpenAPI 3.0 must permit null both through its type flag and enum.
                schema["nullable"]!
                    .GetValue<bool>()
                    .Should()
                    .BeTrue($"{resource}.{property} returns null");
                schema["enum"]!.AsArray().Should().Contain(value => value == null);
            }
        }
        finally
        {
            await ChangeNormalization(
                connection,
                """
                UPDATE norm.songs SET kind='song' WHERE id='song_00000003';
                UPDATE norm.track_songs SET qualifiers='{}'::text[]
                WHERE track_uuid='00000000-0000-0000-0000-000000000044' AND part=1;
                """
            );
        }
    }

    private static string[] EnumValues(
        JsonNode document,
        string resource,
        string property,
        bool arrayItems = false
    )
    {
        var schema = document["components"]!["schemas"]![resource]!["properties"]![property]!;
        if (arrayItems)
        {
            schema = schema["items"]!;
        }
        // Nullable references can wrap the enum schema; follow the public OpenAPI graph.
        while (schema["$ref"] is not null || schema["allOf"] is not null)
        {
            schema = schema["$ref"] is { } reference
                ? document["components"]!["schemas"]![
                    reference.GetValue<string>().Split('/').Last()
                ]!
                : schema["allOf"]![0]!;
        }
        schema["type"]!.GetValue<string>().Should().Be("string");
        return schema["enum"]!
            .AsArray()
            .Where(value => value is not null)
            .Select(value => value!.GetValue<string>())
            .ToArray();
    }

    private static void AssertSchemaFields(JsonNode document, string schema, JsonNode response)
    {
        var properties = document["components"]!["schemas"]![schema]!["properties"]!.AsObject();
        properties
            .Select(field => field.Key)
            .Should()
            .BeEquivalentTo(response.AsObject().Select(field => field.Key));
    }

    [Test]
    public async Task V4_openapi_exposes_the_checkpoint_browse_and_playback_reads()
    {
        var document = await GetJson("/api-docs/v4/swagger.json");
        var paths = document["paths"]!.AsObject();
        paths
            .Select(entry => entry.Key)
            .Should()
            .BeEquivalentTo(
                "/api/v4/artists",
                "/api/v4/artists/{artistId}",
                "/api/v4/artists/{artistId}/years",
                "/api/v4/artists/{artistId}/years/{yearId}",
                "/api/v4/artists/{artistId}/years/by-number/{year}",
                "/api/v4/artists/{artistId}/shows",
                "/api/v4/shows/{showId}",
                "/api/v4/source-tracks/{sourceTrackId}"
            );
        var legacy = await GetJson("/api-docs/v3/swagger.json");
        legacy["paths"]!
            .AsObject()
            .Select(entry => entry.Key)
            .Should()
            .NotContain(path => path.StartsWith("/api/v4/"));
    }

    private async Task<JsonNode> GetJson(string path)
    {
        using var response = await http.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!;
    }

    private async Task<HttpResponseMessage> Conditional(string path, string etag, bool gzip = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        if (gzip)
        {
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        }
        return await http.SendAsync(request);
    }

    private static async Task ChangeNormalization(Npgsql.NpgsqlConnection connection, string sql)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            """
            SET LOCAL norm.author_type='importer';
            SET LOCAL norm.author_ref='v4-review';
            SET LOCAL norm.run_uuid='00000000-0000-0000-0000-000000000099';
            """,
            transaction: transaction
        );
        await connection.ExecuteAsync(sql, transaction: transaction);
        await transaction.CommitAsync();
    }

    private static string ShowId(int scenario) =>
        UuidTypeId.Format("show", Guid.Parse($"00000000-0000-0000-0000-{scenario:000000000000}"));

    private static string TrackId(int scenario) =>
        UuidTypeId.Format(
            "track",
            Guid.Parse($"00000000-0000-0000-0000-{40 + scenario:000000000000}")
        );

    private static string ShowPath(int scenario) => "/api/v4/shows/" + ShowId(scenario);

    private static string TrackPath(int scenario) => "/api/v4/source-tracks/" + TrackId(scenario);
}
