using Dapper;
using Npgsql;

namespace RelistenApiTests;

// Each test process owns a catalog database. Tests never read connection settings
// from the application, so a local test run cannot replace a developer's catalog.
internal static class LocalTestDatabase
{
    public static string Name { get; } = "relisten_test_" + Guid.NewGuid().ToString("N");

    public static string DatabaseUrl =>
        $"postgresql://relisten:local_dev_password@127.0.0.1:15432/{Name}";

    public static string ConnectionString =>
        $"Host=127.0.0.1;Port=15432;Database={Name};Username=relisten;Password=local_dev_password";

    private static bool created;

    public static async Task CreateAsync()
    {
        await using var admin = OpenAdminConnection();
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE DATABASE {Name} TEMPLATE template0");
        created = true;

        await using var catalog = new NpgsqlConnection(ConnectionString);
        await catalog.OpenAsync();
        var schema = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog-schema.sql")
        );
        await catalog.ExecuteAsync(schema, commandTimeout: 120);
        await catalog.ExecuteAsync(Migrations.AddArchiveIntake.SchemaSql, commandTimeout: 120);
        await catalog.ExecuteAsync(
            Migrations.AddRelistenCollections.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddArchivePerformerReviews.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(Migrations.AddShowPerformances.SchemaSql, commandTimeout: 120);
        await catalog.ExecuteAsync(
            Migrations.AddExistingArchiveArtistPublication.SchemaSql,
            commandTimeout: 120
        );
    }

    public static async Task DropAsync()
    {
        if (!created)
        {
            return;
        }

        // Fixtures have stopped their workers before this assembly-level cleanup.
        // FORCE also closes pooled connections left by the application services.
        await using var admin = OpenAdminConnection();
        await admin.OpenAsync();
        await admin.ExecuteAsync($"DROP DATABASE {Name} WITH (FORCE)");
        created = false;
    }

    private static NpgsqlConnection OpenAdminConnection() =>
        new(
            "Host=127.0.0.1;Port=15432;Database=postgres;Username=relisten;Password=local_dev_password"
        );
}
