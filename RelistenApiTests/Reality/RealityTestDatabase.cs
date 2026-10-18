using Npgsql;
using Relisten;
using SimpleMigrations;
using SimpleMigrations.DatabaseProvider;

namespace RelistenApiTests.Reality;

// Brings the shared local test catalog (local-dev/seed/schema.sql, which ends at migration 12)
// up to the latest migration once per test run, the way the API and workers do at startup.
internal static class RealityTestDatabase
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool migrated;

    public static void RequireLocalDatabase()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
            Assert.Ignore("Run ./test-local.sh to include local integration tests.");
    }

    public static async Task<NpgsqlConnection> OpenMigratedAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (!migrated)
            {
                await using var connection = new NpgsqlConnection(
                    LocalTestDatabase.ConnectionString
                );
                await connection.OpenAsync();
                var migrator = new SimpleMigrator(
                    typeof(Startup).Assembly,
                    new PostgresqlDatabaseProvider(connection)
                );
                migrator.Load();
                if (migrator.CurrentMigration == null || migrator.CurrentMigration.Version == 0)
                    migrator.Baseline(12);
                migrator.MigrateTo(migrator.LatestMigration.Version);
                migrated = true;
            }
        }
        finally
        {
            Gate.Release();
        }

        var opened = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await opened.OpenAsync();
        return opened;
    }
}
