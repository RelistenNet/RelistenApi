using Dapper;
using Npgsql;
using SimpleMigrations;
using SimpleMigrations.DatabaseProvider;

namespace RelistenApiTests;

/// <summary>Owns a disposable catalog at a chosen migration, including cleanup if setup fails.</summary>
internal sealed class MigrationTestDatabase : IAsyncDisposable
{
    private readonly string name;
    private long migrationVersion;

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
    private SimpleMigrator Migrator { get; }

    /// <summary>Creates the schema immediately before the named migration, ready for fixture data.</summary>
    public static async Task<MigrationTestDatabase> CreateBeforeAsync<TMigration>()
        where TMigration : Migration
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
            database.migrationVersion = database
                .Migrator.Migrations.Single(migration =>
                    migration.TypeInfo?.AsType() == typeof(TMigration)
                )
                .Version;
            var previousVersion = database
                .Migrator.Migrations.Where(migration =>
                    migration.Version < database.migrationVersion
                )
                .Max(migration => migration.Version);
            database.Migrator.MigrateTo(previousVersion);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    /// <summary>Applies the selected migration after the test has seeded its pre-upgrade data.</summary>
    public void ApplyMigration() => Migrator.MigrateTo(migrationVersion);

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
