using Dapper;
using FluentAssertions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Relisten;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Vendor.PhantasyTour;

namespace RelistenApiTests.Services;

// Opt-in locally; CI supplies a disposable PostgreSQL service. Never use application configuration.
[TestFixture, NonParallelizable, Category("PostgreSQL")]
public class TestCatalogRepairs
{
    private DbService db = null!;
    private string? schema;
    private string originalWrite = null!;
    private string originalRead = null!;

    [SetUp]
    public async Task Start()
    {
        var url = Environment.GetEnvironmentVariable("RELISTEN_TEST_DATABASE_URL");
        if (string.IsNullOrEmpty(url)) Assert.Ignore("Set RELISTEN_TEST_DATABASE_URL to a local relisten_test database.");
        var uri = new Uri(url!);
        if (!uri.IsLoopback || uri.AbsolutePath != "/relisten_test")
            throw new InvalidOperationException("Catalog repair tests require a loopback relisten_test database.");

        originalWrite = DbService.ConnStr;
        originalRead = DbService.ReadOnlyConnStr;
        SqlMapper.AddTypeHandler(new DateTimeHandler());
        db = new DbService(url!, null, null, new TestEnvironment(), NullLogger<DbService>.Instance);
        schema = "catalog_test_" + Guid.NewGuid().ToString("N");
        await db.WithWriteConnection(c => c.ExecuteAsync($"CREATE SCHEMA {schema}"));
        DbService.ConnStr += $";Search Path={schema}";
        DbService.ReadOnlyConnStr = DbService.ConnStr;
        await db.WithWriteConnection(c => c.ExecuteAsync("""
            CREATE TABLE source_tracks (
                id serial PRIMARY KEY, source_id integer, source_set_id integer,
                track_position integer, duration integer, title text, slug text,
                mp3_url text, mp3_md5 text, flac_url text, flac_md5 text,
                created_at timestamptz DEFAULT now(), updated_at timestamptz, artist_id integer,
                uuid uuid CONSTRAINT source_tracks_uuid_key UNIQUE,
                is_orphaned boolean NOT NULL DEFAULT false
            );
            CREATE TABLE setlist_shows (
                id serial PRIMARY KEY, artist_id integer, upstream_identifier text,
                date date, venue_id integer, updated_at timestamptz DEFAULT now(), uuid uuid,
                UNIQUE (artist_id, upstream_identifier)
            );
            """));
    }

    [TearDown]
    public async Task Stop()
    {
        if (schema == null) return;
        try { await db.WithWriteConnection(c => c.ExecuteAsync($"DROP SCHEMA {schema} CASCADE")); }
        finally
        {
            schema = null;
            DbService.ConnStr = originalWrite;
            DbService.ReadOnlyConnStr = originalRead;
        }
    }

    [Test]
    public async Task ReturningTracksBecomeVisibleWithoutChangingTheirIdentity()
    {
        var service = new SourceTrackService(db);
        var source = new Source { id = 1, artist_id = 189 };
        var tracks = Enumerable.Range(1, 15).Select(n => new SourceTrack
        {
            source_id = source.id, source_set_id = 1, artist_id = source.artist_id,
            track_position = n, duration = 100, title = $"Track {n}", slug = $"track-{n}",
            mp3_url = $"https://archive.org/download/fixture/{n:D3}.mp3", updated_at = DateTime.UtcNow
        }).ToArray();
        var before = (await service.InsertAll(source, tracks)).OrderBy(t => t.track_position).ToArray();

        await service.InsertAll(source, tracks.Take(13));
        (await VisibleCount()).Should().Be(13);
        tracks[14].title = "Returned title";
        var after = (await service.InsertAll(source, tracks)).OrderBy(t => t.track_position).ToArray();

        (await VisibleCount()).Should().Be(15);
        after.Select(t => (t.id, t.uuid)).Should().Equal(before.Select(t => (t.id, t.uuid)));
        after[14].title.Should().Be("Returned title");
        (await db.WithWriteConnection(c => c.QuerySingleAsync<int>(
            "SELECT sum(duration)::int FROM source_tracks WHERE is_orphaned=false"))).Should().Be(1500);
        await service.InsertAll(source, tracks);
        (await VisibleCount()).Should().Be(15);
    }

    [Test]
    public async Task ListingRepairsAdjacentDatesByProviderIdentityAndPreservesVenues()
    {
        await db.WithWriteConnection(c => c.ExecuteAsync("""
            INSERT INTO setlist_shows(id,artist_id,upstream_identifier,date,venue_id,uuid) VALUES
                (75486,17,'pt:5237','2002-04-25',106628,'670fbace-cda9-9102-98aa-c44ae09f962e'),
                (75485,17,'pt:5238','2002-04-26',106816,'a5f8d36c-d26b-4bd4-8c87-7f8f21b7987c'),
                (1,18,'pt:5238','2002-04-26',999,'00000000-0000-0000-0000-000000000001');
            """));
        var listings = JsonConvert.DeserializeObject<PhantasyTourShowListing[]>("""
            [{"id":5237,"dateTime":"2002-04-24T20:00:00-05:00"},
             {"id":5238,"dateTime":"2002-04-25T20:00:00-05:00"}]
            """)!;
        var dates = listings.ToDictionary(s => "pt:" + s.id, s => s.dateTime.Date);
        var service = new SetlistShowService(db);
        var artist = new Artist { id = 17 };
        (await service.UpdateDates(artist, dates)).Should().Be(2);
        (await service.UpdateDates(artist, dates)).Should().Be(0);
        var rows = (await db.WithWriteConnection(c => c.QueryAsync<SetlistShow>(
            "SELECT * FROM setlist_shows ORDER BY id"))).ToArray();

        rows.Single(s => s.id == 75486).Should().BeEquivalentTo(new
        {
            date = new DateTime(2002, 4, 24), venue_id = 106628,
            uuid = Guid.Parse("670fbace-cda9-9102-98aa-c44ae09f962e")
        });
        rows.Single(s => s.id == 75485).Should().BeEquivalentTo(new
        {
            date = new DateTime(2002, 4, 25), venue_id = 106816,
            uuid = Guid.Parse("a5f8d36c-d26b-4bd4-8c87-7f8f21b7987c")
        });
        rows.Single(s => s.artist_id == 18).date.Should().Be(new DateTime(2002, 4, 26));
        rows.Where(s => s.artist_id == 17).Select(s => s.date).Should().OnlyHaveUniqueItems();
    }

    private Task<int> VisibleCount() => db.WithWriteConnection(c => c.QuerySingleAsync<int>(
        "SELECT count(*)::int FROM source_tracks WHERE is_orphaned=false"));

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "RelistenApiTests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
