using Dapper;
using FluentAssertions;
using Migrations;

namespace RelistenApiTests;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestReviewSearchMigration
{
    [Test]
    public async Task Migration_reuses_the_index_prebuilt_by_the_operator()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        await using var database =
            await MigrationTestDatabase.CreateBeforeAsync<ReviewSearchIndex>();
        var connection = database.Connection;

        await connection.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS pg_trgm WITH SCHEMA public");
        var assembly = typeof(ReviewSearchIndex).Assembly;
        using var sql = new StreamReader(
            assembly.GetManifestResourceStream(
                $"{assembly.GetName().Name}.Migrations.Sql.022_review_search_index.sql"
            )!
        );
        await connection.ExecuteAsync(await sql.ReadToEndAsync());

        const string indexOid =
            "SELECT 'public.source_reviews_search_trgm_idx'::regclass::oid::bigint";
        var prebuilt = await connection.ExecuteScalarAsync<long>(indexOid);
        database.ApplyMigration();

        (await connection.ExecuteScalarAsync<long>(indexOid)).Should().Be(prebuilt);
    }
}
