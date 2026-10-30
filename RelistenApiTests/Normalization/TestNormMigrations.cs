using Dapper;
using FluentAssertions;

namespace RelistenApiTests.Normalization;

[Category("LocalIngestion")]
public class TestNormMigrations
{
    [SetUp]
    public void RequireDatabase() => MigratedTestDatabase.RequireLocalDatabase();

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

    [Test]
    public async Task NewArtistsStartWithNormalizationDisabled()
    {
        await using var db = await MigratedTestDatabase.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        var uuid = Guid.NewGuid();
        var artist = await db.QuerySingleAsync<int>(
            "INSERT INTO artists(uuid,name,slug,updated_at) VALUES(@uuid,'Approval test',@slug,now()) RETURNING id",
            new { uuid, slug = "approval-" + uuid },
            tx
        );
        var flags = await db.QuerySingleAsync<(bool Catalogue, bool Venues)>(
            "INSERT INTO features(artist_id) VALUES(@artist) RETURNING norm_catalogue,norm_venues",
            new { artist },
            tx
        );

        flags.Should().Be((false, false));
    }
}
