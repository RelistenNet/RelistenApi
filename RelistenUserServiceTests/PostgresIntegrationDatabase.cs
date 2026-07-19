using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;

namespace RelistenUserServiceTests;

public sealed class PostgresIntegrationDatabase
{
    private readonly string _databaseName =
        $"relisten_accounts_test_{Guid.NewGuid():N}";
    private string _adminConnectionString = "";
    private bool _databaseCreated;

    public string ConnectionString { get; private set; } = "";

    public async Task StartAsync()
    {
        _adminConnectionString = Environment.GetEnvironmentVariable(
            "RELISTEN_TEST_POSTGRES_ADMIN")
            ?? "Host=127.0.0.1;Port=15432;Database=postgres;Username=relisten;Password=local_dev_password";

        try
        {
            await using var admin = new NpgsqlConnection(_adminConnectionString);
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand(
                $"CREATE DATABASE \"{_databaseName}\"",
                admin);
            await create.ExecuteNonQueryAsync();
            _databaseCreated = true;
        }
        catch (Exception exception)
        {
            Assert.Ignore(
                $"PostgreSQL integration database is unavailable. Start local-dev or set " +
                $"RELISTEN_TEST_POSTGRES_ADMIN. {exception.Message}");
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = _databaseName
        }.ConnectionString;
        await using var dbContext = CreateContext();
        await dbContext.Database.MigrateAsync();
        await dbContext.Database.ExecuteSqlRawAsync(CatalogTablesSql);
    }

    public async Task StopAsync()
    {
        if (!_databaseCreated)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync();
        await using var terminate = new NpgsqlCommand($"""
            SELECT pg_terminate_backend(pid)
            FROM pg_stat_activity
            WHERE datname = '{_databaseName}' AND pid <> pg_backend_pid();
            """, admin);
        await terminate.ExecuteNonQueryAsync();
        await using var drop = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{_databaseName}\"",
            admin);
        await drop.ExecuteNonQueryAsync();
    }

    public AccountsDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AccountsDbContext>();
        options.UseNpgsql(ConnectionString, postgres =>
            postgres.MigrationsHistoryTable("__EFMigrationsHistory", "identity"));
        options.UseOpenIddict<Guid>();
        return new(options.Options);
    }

    public async Task<Guid> CreateUserAsync(string username)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.CreateVersion7();
        await using var dbContext = CreateContext();
        dbContext.Users.Add(new User
        {
            Id = userId,
            Status = UserStatuses.Active,
            Username = username,
            UsernameVersion = 1,
            SecurityVersion = 1,
            LifecycleGeneration = 1,
            CreatedAt = now,
            UpdatedAt = now,
            LastLoginAt = now
        });
        await dbContext.SaveChangesAsync();
        return userId;
    }

    public async Task<Guid> CreateArtistAsync()
    {
        var artistUuid = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            WITH artist AS (
                INSERT INTO artists (uuid)
                VALUES (@uuid)
                RETURNING id
            )
            INSERT INTO features (artist_id)
            SELECT id FROM artist;
            """,
            connection);
        command.Parameters.AddWithValue("uuid", artistUuid);
        await command.ExecuteNonQueryAsync();
        return artistUuid;
    }

    private const string CatalogTablesSql = """
        CREATE TABLE artists (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            uuid uuid NOT NULL UNIQUE
        );
        CREATE TABLE features (
            artist_id bigint NOT NULL UNIQUE
        );
        CREATE TABLE years (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            artist_id bigint NOT NULL,
            year text NOT NULL
        );
        CREATE TABLE shows (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            uuid uuid NOT NULL UNIQUE,
            artist_id bigint NOT NULL,
            year_id bigint NULL,
            date date NOT NULL
        );
        CREATE TABLE show_source_information (
            show_id bigint NOT NULL UNIQUE
        );
        CREATE TABLE sources (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            uuid uuid NOT NULL UNIQUE,
            artist_id bigint NOT NULL,
            show_id bigint NULL
        );
        CREATE TABLE source_sets (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            uuid uuid NOT NULL UNIQUE,
            source_id bigint NOT NULL
        );
        CREATE TABLE source_tracks (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            uuid uuid NOT NULL UNIQUE,
            source_id bigint NOT NULL,
            source_set_id bigint NOT NULL,
            mp3_url text NULL,
            flac_url text NULL,
            is_orphaned boolean NOT NULL DEFAULT false
        );
        CREATE TABLE setlist_songs (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            uuid uuid NOT NULL UNIQUE,
            artist_id bigint NOT NULL
        );
        CREATE TABLE tours (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            uuid uuid NOT NULL UNIQUE,
            artist_id bigint NOT NULL,
            start_date date NULL,
            end_date date NULL
        );
        CREATE TABLE venues (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            uuid uuid NOT NULL UNIQUE,
            artist_id bigint NULL,
            name text NULL,
            location text NULL,
            upstream_identifier text NULL,
            slug text NULL
        );
        """;
}
