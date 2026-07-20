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

}
