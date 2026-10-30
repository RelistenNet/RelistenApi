using Dapper;
using FluentAssertions;

namespace RelistenApiTests.Normalization;

[Category("LocalIngestion")]
public class TestNormMigrations
{
    [SetUp]
    public void RequireDatabase() => MigratedTestDatabase.RequireLocalDatabase();

    [Test]
    public async Task MigrationsCreateTheSongCatalogue()
    {
        await using var db = await MigratedTestDatabase.OpenAsync();

        var tables = await db.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'norm' ORDER BY 1"
        );

        tables
            .Should()
            .Equal(
                "artist_venue_refresh_queue",
                "artist_venues",
                "changes",
                "model_calls",
                "review_items",
                "runs",
                "show_venues",
                "song_aliases",
                "song_evidence",
                "songs",
                "track_songs",
                "venue_evidence",
                "venue_external_ids",
                "venue_names",
                "venues"
            );
    }

    [Test]
    public async Task TheChangeLogIsAppendOnly()
    {
        await using var db = await MigratedTestDatabase.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteAsync(
            """
            INSERT INTO norm.changes (table_name, row_key, op) VALUES ('songs', '{}', 'insert')
            """,
            transaction: tx
        );

        var update = () =>
            db.ExecuteAsync("UPDATE norm.changes SET op = 'delete'", transaction: tx);

        await update.Should().ThrowAsync<Npgsql.PostgresException>().WithMessage("*append-only*");
    }

    // Read (norm_catalogue) and write (norm_venues) start off for every artist.
    [TestCase("norm_catalogue")]
    [TestCase("norm_venues")]
    public async Task EveryArtistStartsUnapproved(string flag)
    {
        await using var db = await MigratedTestDatabase.OpenAsync();

        var column = await db.QuerySingleAsync<(string Type, string Default, string Nullable)>(
            """
            SELECT data_type, column_default, is_nullable FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'features' AND column_name = @flag
            """,
            new { flag }
        );

        column.Should().Be(("boolean", "false", "NO"));
    }
}
