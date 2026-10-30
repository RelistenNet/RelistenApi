using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Relisten;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Normalization.Songs;
using Relisten.Services.Normalization;

namespace RelistenApiTests.Services;

/// <summary>
/// Exercises source date handling through the public rebuild operation and its
/// persisted show/source relationships, using the disposable local test database.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public class TestCatalogRebuildDates
{
    private DbService db = null!;
    private RedisService redis = null!;
    private CatalogRebuildService rebuild = null!;
    private string? previousWrite;
    private string? previousRead;

    [OneTimeSetUp]
    public void Start()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run ./test-local.sh to include local integration tests.");
        }

        previousWrite = DbService.ConnStr;
        previousRead = DbService.ReadOnlyConnStr;
        db = new DbService(
            LocalTestDatabase.DatabaseUrl,
            null,
            null,
            Host.CreateApplicationBuilder().Environment,
            NullLogger<DbService>.Instance
        );
        redis = new RedisService(RedisService.BuildConfiguration("redis://127.0.0.1:16379"));
        rebuild = new CatalogRebuildService(
            db,
            redis,
            new SongCounts(new DbServiceNormalizationDatabase(db)),
            NullLogger<CatalogRebuildService>.Instance
        );
    }

    [OneTimeTearDown]
    public void Stop()
    {
        redis?.connection.Dispose();

        if (previousWrite != null)
        {
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead!;
        }
    }

    [TestCase("2000-XX-XX", "2000-01-01")]
    [TestCase("2000-xx-xx", "2000-01-01")]
    [TestCase("2000-xx-15", "2000-01-01")]
    [TestCase("2000-02-xx", "2000-02-01")]
    [TestCase("2000-02-XX", "2000-02-01")]
    [TestCase("2000-02-29T12:00:00Z", "2000-02-29")]
    [TestCase("2001-02-29", null)]
    [TestCase("2000-13-01", null)]
    [TestCase("unknown", null)]
    [TestCase("yesterday", null)]
    public async Task RebuildPreservesPartialDatesAndSkipsInvalidDatesWithoutLosingOtherShows(
        string displayDate,
        string? expectedDate
    )
    {
        var artistUuid = Guid.NewGuid();
        var artistId = await db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<int>(
                """
                INSERT INTO artists(name, slug, uuid, updated_at)
                VALUES ('Date fixture', @slug, @artistUuid, now()) RETURNING id
                """,
                new { slug = "dates-" + artistUuid, artistUuid }
            )
        );
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                INSERT INTO sources(artist_id, upstream_identifier, display_date, uuid,
                    updated_at, is_soundboard, is_remaster, has_jamcharts)
                VALUES (@artistId, @displayDate, @displayDate, @uuid, now(), false, false, false)
                """,
                new[]
                {
                    new
                    {
                        artistId,
                        displayDate,
                        uuid = Guid.NewGuid(),
                    },
                    new
                    {
                        artistId,
                        displayDate = "2000-06-01",
                        uuid = Guid.NewGuid(),
                    },
                }
            )
        );
        var artist = new Artist { id = artistId, features = new Features() };

        await rebuild.RebuildShows(artist);
        await rebuild.RebuildShows(artist);

        var sources = await db.WithWriteConnection(connection =>
            connection.QueryAsync<RebuiltSource>(
                """
                SELECT src.display_date AS DisplayDate, to_char(sh.date, 'YYYY-MM-DD') AS Date, sh.id AS ShowId
                FROM sources src LEFT JOIN shows sh ON sh.id = src.show_id
                WHERE src.artist_id = @artistId
                """,
                new { artistId }
            )
        );
        sources.Should().HaveCount(2, "an invalid date must not delete its upstream recording");
        sources.Single(source => source.DisplayDate == "2000-06-01").Date.Should().Be("2000-06-01");

        var selected = sources.Single(source => source.DisplayDate == displayDate);
        selected.Date.Should().Be(expectedDate);
        selected.ShowId.HasValue.Should().Be(expectedDate != null);
    }

    private sealed record RebuiltSource(string DisplayDate, string? Date, long? ShowId);
}
