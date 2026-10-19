using Dapper;
using FluentAssertions;

namespace RelistenApiTests.Kb;

[Category("LocalIngestion")]
public class TestKbMigrations
{
    [SetUp]
    public void RequireDatabase() => MigratedTestDatabase.RequireLocalDatabase();

    [Test]
    public async Task MigrationsCreateTheSongCatalogue()
    {
        await using var db = await MigratedTestDatabase.OpenAsync();

        var tables = await db.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'kb' ORDER BY 1"
        );

        tables
            .Should()
            .Equal("changes", "runs", "song_aliases", "song_evidence", "songs", "track_songs");
    }

    [Test]
    public async Task TheChangeLogIsAppendOnly()
    {
        await using var db = await MigratedTestDatabase.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteAsync(
            """
            INSERT INTO kb.changes (table_name, row_key, op) VALUES ('songs', '{}', 'insert')
            """,
            transaction: tx
        );

        var update = () => db.ExecuteAsync("UPDATE kb.changes SET op = 'delete'", transaction: tx);

        await update.Should().ThrowAsync<Npgsql.PostgresException>().WithMessage("*append-only*");
    }
}
