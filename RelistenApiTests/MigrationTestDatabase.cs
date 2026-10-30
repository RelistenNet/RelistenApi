using Dapper;
using Npgsql;
using SimpleMigrations;
using SimpleMigrations.DatabaseProvider;

namespace RelistenApiTests;

/// <summary>Owns a disposable catalog at a chosen migration, including cleanup if setup fails.</summary>
internal sealed class MigrationTestDatabase : IAsyncDisposable
{
    private readonly string name;

    private MigrationTestDatabase(string name, NpgsqlConnection connection)
    {
        this.name = name;
        Connection = connection;
        // The provider opens the supplied connection; this owner disposes it.
        Migrator = new SimpleMigrator(
            typeof(Relisten.Startup).Assembly,
            new PostgresqlDatabaseProvider(connection)
        );
    }

    public NpgsqlConnection Connection { get; }
    public SimpleMigrator Migrator { get; }

    public static async Task<MigrationTestDatabase> CreateAsync(int version)
    {
        var name = "migration_test_" + Guid.NewGuid().ToString("N");
        await using var admin = AdminConnection();
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE DATABASE {name} TEMPLATE template0");

        var settings = new NpgsqlConnectionStringBuilder(LocalTestDatabase.ConnectionString)
        {
            Database = name,
        };
        var database = new MigrationTestDatabase(
            name,
            new NpgsqlConnection(settings.ConnectionString)
        );
        try
        {
            var schema = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog-schema.sql")
            );
            await database.Connection.ExecuteAsync(schema, commandTimeout: 120);
            database.Migrator.Load();
            database.Migrator.Baseline(12);
            database.Migrator.MigrateTo(version);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        await using var admin = AdminConnection();
        await admin.OpenAsync();
        await admin.ExecuteAsync($"DROP DATABASE {name} WITH (FORCE)");
    }

    private static NpgsqlConnection AdminConnection() =>
        new(
            new NpgsqlConnectionStringBuilder(LocalTestDatabase.ConnectionString)
            {
                Database = "postgres",
            }.ConnectionString
        );
}
