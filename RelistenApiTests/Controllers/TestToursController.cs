using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Relisten;
using Relisten.Api;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;
using Relisten.Controllers;
using Relisten.Data;

namespace RelistenApiTests.Controllers;

// Runs the real controller, artist/tour/show services, and Dapper against an isolated
// PostgreSQL schema. Set RELISTEN_TOUR_TEST_DATABASE_URL to a disposable local DB.
[TestFixture]
[NonParallelizable] // DbService connection strings are process-wide.
public class TestToursController
{
    private const string TourUuid = "11111111-1111-1111-1111-111111111111";
    private readonly string schema = "tour_tests_" + Guid.NewGuid().ToString("N");
    private NpgsqlConnection? connection;
    private string previousWrite = null!;
    private string previousRead = null!;
    private ToursController controller = null!;
    private DbService db = null!;

    [OneTimeSetUp]
    public async Task CreateSchema()
    {
        var url = Environment.GetEnvironmentVariable("RELISTEN_TOUR_TEST_DATABASE_URL");
        if (string.IsNullOrEmpty(url))
        {
            Assert.Ignore("Set RELISTEN_TOUR_TEST_DATABASE_URL to run PostgreSQL tour regression tests.");
        }

        var uri = new Uri(url!);
        if (!uri.IsLoopback)
        {
            throw new InvalidOperationException("Tour regression tests require a local disposable database.");
        }

        previousWrite = DbService.ConnStr;
        previousRead = DbService.ReadOnlyConnStr;
        db = new DbService(url!, null, null,
            new HostingEnvironment { EnvironmentName = Environments.Production },
            NullLogger<DbService>.Instance);
        connection = new NpgsqlConnection(DbService.ConnStr);
        await connection.OpenAsync();
        await connection.ExecuteAsync($"CREATE SCHEMA {schema}; SET search_path TO {schema};");
        DbService.ConnStr += $";Search Path={schema};Pooling=false";
        DbService.ReadOnlyConnStr = DbService.ConnStr;
        await connection.ExecuteAsync("""
            CREATE TABLE artists (id int PRIMARY KEY, uuid uuid, slug text);
            CREATE TABLE features (id int, artist_id int, tours bool);
            CREATE TABLE artists_upstream_sources (id int, artist_id int, upstream_source_id int);
            CREATE TABLE upstream_sources (id int);
            CREATE TABLE tours (id int PRIMARY KEY, artist_id int, uuid uuid, slug text,
                name text, start_date timestamp);
            CREATE TABLE setlist_shows (id int, tour_id int);
            CREATE TABLE shows (id int, artist_id int, uuid uuid, tour_id int, venue_id int,
                era_id int, year_id int, display_date text);
            CREATE TABLE venues (id int, uuid uuid);
            CREATE TABLE eras (id int);
            CREATE TABLE years (id int, uuid uuid);
            CREATE TABLE show_source_information (show_id int, max_updated_at timestamp,
                source_count int, has_soundboard_source bool, has_flac bool);
            CREATE TABLE venue_show_counts (id int, shows_at_venue int);
            INSERT INTO artists VALUES
                (1, 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'fugazi'),
                (2, 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'other');
            INSERT INTO features VALUES (1, 1, true), (2, 2, true);
            """);
        var tours = new TourService(db, new ShowService(db, null!, null!));
        controller = new ToursController(null!, db, new ArtistService(db, null!), tours)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [SetUp]
    public async Task SeedTours()
    {
        await connection!.ExecuteAsync("""
            TRUNCATE tours;
            INSERT INTO tours (id, artist_id, uuid, slug, name) VALUES
                (42, 1, @uuid, '1989-fall-regional-dates', '1989 Fall Regional Dates'),
                (1989, 1, '22222222-2222-2222-2222-222222222222', 'unrelated-tour', 'Unrelated'),
                (43, 2, '33333333-3333-3333-3333-333333333333', 'other-tour', 'Other Artist');
            """, new { uuid = Guid.Parse(TourUuid) });
    }

    [OneTimeTearDown]
    public async Task DropSchema()
    {
        controller?.Dispose();
        if (connection == null) return;
        try
        {
            await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE;");
        }
        finally
        {
            await connection.DisposeAsync();
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead;
        }
    }

    [Test]
    public async Task ListedYearPrefixedSlugResolvesToTheSameTourInsteadOfTheYearId()
    {
        var listing = (JsonResult)await controller.Tours("fugazi");
        var listedTour = ((IEnumerable<TourWithShowCount>)listing.Value!).Single(t => t.id == 42);

        var result = (JsonResult)await controller.ToursWithShowsV3("fugazi", listedTour.slug);

        var tour = (TourWithShows)result.Value!;
        tour.uuid.Should().Be(listedTour.uuid);
        tour.name.Should().Be(listedTour.name);
        tour.shows.Should().BeEmpty(); // A known tour with no recordings is still a successful lookup.
    }

    [TestCase("42")]
    [TestCase(TourUuid)]
    [TestCase("42-1989-fall-regional-dates")]
    [TestCase("42-stale-suffix")]
    public async Task V3PreservesNumericUuidAndLegacyIdentifiers(string identifier)
    {
        var result = (JsonResult)await controller.ToursWithShowsV3("fugazi", identifier);
        ((TourWithShows)result.Value!).uuid.Should().Be(Guid.Parse(TourUuid));
    }

    [TestCase("42")]
    [TestCase("42-1989-fall-regional-dates")]
    [TestCase("42-stale-suffix")]
    public async Task V2PreservesIdLookup(string identifier)
    {
        var result = (JsonResult)await controller.ToursWithShows("fugazi", identifier);
        ((TourWithShows)result.Value!).id.Should().Be(42);
    }

    [TestCase("fugazi", "missing-tour")]
    [TestCase("fugazi", "999999-missing-tour")]
    [TestCase("fugazi", "999999")]
    [TestCase("fugazi", "99999999-9999-9999-9999-999999999999")]
    [TestCase("fugazi", "43")]
    [TestCase("fugazi", "33333333-3333-3333-3333-333333333333")]
    [TestCase("fugazi", "other-tour")]
    [TestCase("missing-artist", "42")]
    public async Task V3MissingOrWrongArtistTourReturnsExistingNotFoundEnvelope(string artist, string identifier)
    {
        AssertNotFound(await controller.ToursWithShowsV3(artist, identifier));
    }

    [TestCase("999999")]
    [TestCase("43-other-tour")]
    [TestCase("missing-tour")]
    public async Task V2MissingTourReturnsExistingNotFoundEnvelope(string identifier)
    {
        AssertNotFound(await controller.ToursWithShows("fugazi", identifier));
    }

    [Test]
    public async Task MissingTourDoesNotLoadShows()
    {
        // No ShowService is supplied: a miss must return before touching that dependency.
        var service = new TourService(db, null!);
        (await service.ForIdWithShows(new Artist { id = 1 }, 999999)).Should().BeNull();
    }

    [Test]
    public async Task DuplicateSlugsRemainAnErrorInsteadOfReturningAnArbitraryTour()
    {
        await connection!.ExecuteAsync("""
            INSERT INTO tours (id, artist_id, uuid, slug, name)
            VALUES (44, 1, '44444444-4444-4444-4444-444444444444',
                '1989-fall-regional-dates', 'Duplicate');
            """);
        Func<Task> request = () => controller.ToursWithShowsV3("fugazi", "1989-fall-regional-dates");
        await request.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task NumericSlugResolvesWhenNoNumericIdMatches()
    {
        await connection!.ExecuteAsync("UPDATE tours SET slug = '2012' WHERE id = 42");
        var result = (JsonResult)await controller.ToursWithShowsV3("fugazi", "2012");
        ((TourWithShows)result.Value!).id.Should().Be(42);
    }

    [Test]
    public async Task BareNumericIdKeepsPriorityOverANumericSlug()
    {
        await connection!.ExecuteAsync("UPDATE tours SET slug = '1989' WHERE id = 42");
        var result = (JsonResult)await controller.ToursWithShowsV3("fugazi", "1989");
        ((TourWithShows)result.Value!).id.Should().Be(1989);
    }

    [Test]
    public async Task DatabaseErrorsAreNotConvertedToNotFound()
    {
        await connection!.ExecuteAsync("ALTER TABLE tours RENAME TO unavailable_tours");
        try
        {
            Func<Task> request = () => controller.ToursWithShowsV3("fugazi", "missing-tour");
            var error = await request.Should().ThrowAsync<Exception>();
            error.Which.InnerException.Should().BeOfType<PostgresException>();
        }
        finally
        {
            await connection!.ExecuteAsync("ALTER TABLE unavailable_tours RENAME TO tours");
        }
    }

    private void AssertNotFound(IActionResult result)
    {
        var notFound = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.StatusCode.Should().Be(404);
        var envelope = notFound.Value.Should().BeOfType<ResponseEnvelope<bool>>().Subject;
        envelope.success.Should().BeFalse();
        envelope.error_code.Should().Be(ApiErrorCode.NotFound);
        envelope.data.Should().BeFalse();

        controller.HttpContext.Request.Method = "GET";
        controller.HttpContext.Request.Path = "/api/v3/artists/fugazi/tours/missing-tour";
        var context = new ResultExecutingContext(
            new ActionContext(controller.HttpContext, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()),
            new List<IFilterMetadata>(), result, controller);
        new ApiCacheControlAttribute().OnResultExecuting(context);
        controller.HttpContext.Response.Headers.CacheControl.ToString().Should().Be("no-store");
    }
}
