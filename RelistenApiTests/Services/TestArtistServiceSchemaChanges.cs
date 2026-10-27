using Dapper;
using FluentAssertions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Relisten;
using Relisten.Api.Models;
using Relisten.Data;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public class TestArtistServiceSchemaChanges
{
    private string schema = "";
    private string previousWrite = null!;
    private string previousRead = null!;
    private bool schemaCreated;
    private bool connectionStringsSaved;
    private DbService db = null!;
    private ArtistService artists = null!;
    private Artist expected = null!;

    [SetUp]
    public async Task CreateIsolatedCatalog()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        previousWrite = DbService.ConnStr;
        previousRead = DbService.ReadOnlyConnStr;
        connectionStringsSaved = true;
        schema = "artist_schema_change_" + Guid.NewGuid().ToString("N");
        schemaCreated = false;
        SqlMapper.AddTypeHandler(new PersistentIdentifierHandler());
        SqlMapper.AddTypeHandler(new DateTimeHandler());
        db = new DbService(
            LocalTestDatabase.DatabaseUrl,
            null,
            null,
            new ProductionEnvironment(),
            NullLogger<DbService>.Instance
        );
        var options = new NpgsqlConnectionStringBuilder(DbService.ConnStr)
        {
            SearchPath = schema,
            ApplicationName = schema,
            MaxAutoPrepare = 100,
            AutoPrepareMinUsages = 5,
            // Artist lookup keeps its connection while loading upstream mappings.
            MaxPoolSize = 2,
        };
        DbService.ConnStr = options.ConnectionString;
        options.ApplicationName += "_read";
        DbService.ReadOnlyConnStr = options.ConnectionString;
        artists = new ArtistService(db, null!);

        expected = new Artist
        {
            id = 42,
            musicbrainz_id = "fixture-musicbrainz-id",
            created_at = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            updated_at = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc),
            name = "The Fixture Artist",
            featured = 1,
            slug = "fixture-artist",
            sort_name = "Fixture Artist",
            uuid = Guid.NewGuid(),
            features = new Features { id = 77, artist_id = 42 },
            upstream_sources = [],
        };
        // Every mapped flag is true so omitting any projected feature is observable.
        var flags = typeof(Features)
            .GetProperties()
            .Where(property => property.PropertyType == typeof(bool))
            .ToArray();
        foreach (var flag in flags)
        {
            flag.SetValue(expected.features, true);
        }

        await using var connection = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync($"CREATE SCHEMA {schema}");
        schemaCreated = true;
        await connection.ExecuteAsync(
            $"""
            SET search_path TO {schema};
            CREATE TABLE artists (LIKE public.artists INCLUDING DEFAULTS);
            CREATE TABLE features (LIKE public.features INCLUDING DEFAULTS);
            CREATE TABLE artists_upstream_sources (LIKE public.artists_upstream_sources INCLUDING DEFAULTS);
            CREATE TABLE upstream_sources (LIKE public.upstream_sources INCLUDING DEFAULTS);
            CREATE TABLE shows (artist_id integer);
            CREATE TABLE show_source_information (artist_id integer, source_count integer);
            INSERT INTO artists (id, musicbrainz_id, created_at, updated_at, name, featured, slug, sort_name, uuid)
                VALUES (@id, @musicbrainz_id, @created_at, @updated_at, @name, @featured, @slug, @sort_name, @uuid);
            INSERT INTO features (id, artist_id) VALUES (77, 42);
            UPDATE features SET {string.Join(", ", flags.Select(flag => flag.Name + " = true"))};
            INSERT INTO shows VALUES (42);
            INSERT INTO show_source_information VALUES (42, 3);
            """,
            expected
        );
    }

    [TearDown]
    public async Task DropIsolatedCatalog()
    {
        if (connectionStringsSaved)
        {
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead;
            connectionStringsSaved = false;
        }

        if (!schemaCreated)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
        schemaCreated = false;
    }

    [TestCase(false)]
    [TestCase(true)]
    public Task PreparedArtistLookupSurvivesAddedColumns(bool forImport)
    {
        return AssertStableAcrossAddedColumns(
            () =>
                forImport
                    ? artists.FindArtistForImport(expected.slug)
                    : artists.FindArtistWithIdOrSlug(expected.slug),
            readOnly: !forImport,
            nestedEnrichment: true
        );
    }

    [Test]
    public Task PreparedArtistCountsSurviveAddedColumns()
    {
        return AssertStableAcrossAddedColumns(
            async () =>
            {
                var result = (
                    await artists.AllWithCounts<ArtistWithCounts>([expected.slug])
                ).Single();
                result.show_count.Should().Be(1);
                result.source_count.Should().Be(3);
                return result;
            },
            readOnly: true,
            nestedEnrichment: false
        );
    }

    [Test]
    public async Task ArtistWithoutFeaturesStillResolves()
    {
        await db.WithWriteConnection(connection => connection.ExecuteAsync("DELETE FROM features"));

        var artist = await artists.FindArtistWithIdOrSlug(expected.slug);

        artist.Should().NotBeNull();
        artist!.id.Should().Be(expected.id);
        artist.features.Should().BeNull();
        artist.upstream_sources.Should().BeEmpty();
    }

    private async Task AssertStableAcrossAddedColumns(
        Func<Task<Artist?>> load,
        bool readOnly,
        bool nestedEnrichment
    )
    {
        for (var i = 0; i < 16; i++)
        {
            (await load()).Should().BeEquivalentTo(expected);
        }

        // Read the same pool used by the service, proving the regression runs against
        // a prepared result shape rather than merely staying below the threshold.
        const string preparedQueryCountSql = """
            SELECT count(*) FROM pg_prepared_statements
            WHERE statement LIKE '%LEFT JOIN features f%'
            """;
        await using (var first = db.CreateConnection(longTimeout: false, readOnly))
        {
            await first.OpenAsync();
            var prepared = await first.QuerySingleAsync<int>(preparedQueryCountSql);
            if (nestedEnrichment)
            {
                // Lookup and enrichment can occupy either connector in this two-slot pool.
                // Counts release their connection before enrichment, so they use one slot.
                await using var second = db.CreateConnection(longTimeout: false, readOnly);
                await second.OpenAsync();
                prepared += await second.QuerySingleAsync<int>(preparedQueryCountSql);
            }

            prepared.Should().BeGreaterThan(0);
        }

        // A separate connection represents DDL from a release while existing clients
        // retain their prepared statements. Only this fixture's schema is changed.
        await using (var migration = new NpgsqlConnection(LocalTestDatabase.ConnectionString))
        {
            await migration.OpenAsync();
            await migration.ExecuteAsync(
                $"""
                ALTER TABLE {schema}.artists ADD COLUMN internal_artist_flag boolean DEFAULT false;
                ALTER TABLE {schema}.features ADD COLUMN internal_feature_flag boolean DEFAULT false;
                """
            );
        }

        for (var i = 0; i < 16; i++)
        {
            (await load()).Should().BeEquivalentTo(expected);
        }
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = nameof(TestArtistServiceSchemaChanges);
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
