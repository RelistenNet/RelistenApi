using Dapper;
using FluentAssertions;
using Migrations;
using Npgsql;

namespace RelistenApiTests;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestReviewSearchMigration
{
    private const string IndexOid =
        "SELECT 'public.source_reviews_search_trgm_idx'::regclass::oid::bigint";

    [Test]
    public async Task Migration_reuses_the_index_prebuilt_by_the_operator()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        await using var database =
            await MigrationTestDatabase.CreateBeforeAsync<ReviewSearchIndex>();
        var connection = database.Connection;
        await PrebuildIndex(connection);

        var prebuilt = await connection.ExecuteScalarAsync<long>(IndexOid);
        database.ApplyMigration();

        (await connection.ExecuteScalarAsync<long>(IndexOid)).Should().Be(prebuilt);
    }

    [Test]
    public async Task Migration_rebuilds_an_index_left_invalid_by_an_interrupted_build()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        await using var database =
            await MigrationTestDatabase.CreateBeforeAsync<ReviewSearchIndex>();
        var connection = database.Connection;
        await PrebuildIndex(connection);

        // A canceled CREATE INDEX CONCURRENTLY leaves the same catalog state.
        await connection.ExecuteAsync(
            "UPDATE pg_index SET indisvalid=false WHERE indexrelid='public.source_reviews_search_trgm_idx'::regclass"
        );
        database.ApplyMigration();

        (
            await connection.ExecuteScalarAsync<bool>(
                """
                SELECT indisready AND indisvalid FROM pg_index
                WHERE indexrelid='public.source_reviews_search_trgm_idx'::regclass
                """
            )
        ).Should().BeTrue();
    }

    private static async Task PrebuildIndex(NpgsqlConnection connection)
    {
        await connection.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS pg_trgm WITH SCHEMA public");
        var assembly = typeof(ReviewSearchIndex).Assembly;
        using var sql = new StreamReader(
            assembly.GetManifestResourceStream(
                $"{assembly.GetName().Name}.Migrations.Sql.022_review_search_index.sql"
            )!
        );
        await connection.ExecuteAsync(await sql.ReadToEndAsync());
    }
}
