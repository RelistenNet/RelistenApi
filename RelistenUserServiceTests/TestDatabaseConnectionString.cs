using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NUnit.Framework;
using RelistenUserService.Persistence;

namespace RelistenUserServiceTests;

[TestFixture]
public sealed class TestDatabaseConnectionString
{
    [Test]
    public void Pooler_override_changes_only_runtime_routing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ACCOUNTS_DATABASE_URL"] =
                        "postgresql://accounts:p%40ss@primary.internal:5433/relisten?sslmode=require&application_name=relisten-users",
                    ["PGBOUNCER_WRITE_HOST"] = "pooler.internal",
                    ["ACCOUNTS_MIGRATION_DATABASE_URL"] =
                        "postgresql://migrator:ddl@migration-primary.internal:5433/relisten?sslmode=require",
                    ["ACCOUNTS_LOCK_DATABASE_URL"] =
                        "postgresql://accounts:locks@lock-primary.internal:5433/relisten?sslmode=require",
                }
            )
            .Build();
        var runtime = new NpgsqlConnectionStringBuilder(
            DatabaseConnectionString.Resolve(configuration)
        );
        runtime.Host.Should().Be("pooler.internal");
        runtime.Port.Should().Be(5433);
        runtime.Database.Should().Be("relisten");
        runtime.Username.Should().Be("accounts");
        runtime.Password.Should().Be("p@ss");
        runtime.SslMode.Should().Be(SslMode.Require);
        runtime.ApplicationName.Should().Be("relisten-users");

        // Exercise the context used by startup migrations and EF/restore tools, not just the selector.
        using var migration = AccountsDbContextFactory.CreateMigrationContext(configuration);
        new NpgsqlConnectionStringBuilder(migration.Database.GetDbConnection().ConnectionString)
            .Host.Should()
            .Be("migration-primary.internal");
        new NpgsqlConnectionStringBuilder(
            DatabaseConnectionString.ResolveRefreshTokenLocks(configuration, null!)
        )
            .Host.Should()
            .Be("lock-primary.internal");
    }

    [Test]
    public void Direct_fallbacks_do_not_inherit_runtime_pooler_override()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ACCOUNTS_DATABASE_URL"] =
                        "postgresql://accounts:local@primary.internal/relisten",
                    ["PGBOUNCER_WRITE_HOST"] = "pooler.internal",
                }
            )
            .Build();
        new NpgsqlConnectionStringBuilder(DatabaseConnectionString.ResolveMigrations(configuration))
            .Host.Should()
            .Be("primary.internal");
        var environment = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment
        {
            EnvironmentName = Environments.Development,
        };
        new NpgsqlConnectionStringBuilder(
            DatabaseConnectionString.ResolveRefreshTokenLocks(configuration, environment)
        )
            .Host.Should()
            .Be("primary.internal");
    }

    [TestCase("0")]
    [TestCase("-1")]
    [TestCase("invalid")]
    public void Rejects_invalid_refresh_capacity_during_configuration(string capacity)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ACCOUNTS_LOCK_DATABASE_URL"] = "Host=primary.internal;Database=accounts",
                    ["ACCOUNTS_REFRESH_MAX_POOL_SIZE"] = capacity,
                }
            )
            .Build();

        var resolve = () => DatabaseConnectionString.ResolveRefreshTokenLocks(configuration, null!);

        resolve
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*ACCOUNTS_REFRESH_MAX_POOL_SIZE*");
    }

    [TestCase("postgresql://accounts:secret@primary.internal/accounts", 100)]
    [TestCase("Host=primary.internal;Database=accounts;MaxPoolSize=7", 7)]
    public void Runtime_pool_serializes_default_without_overwriting_explicit_capacity(
        string connectionString,
        int expectedCapacity
    )
    {
        var configuration = BuildConfiguration(connectionString);

        var resolved = new NpgsqlConnectionStringBuilder(
            DatabaseConnectionString.Resolve(configuration)
        );

        resolved.MaxPoolSize.Should().Be(expectedCapacity);
        resolved.ShouldSerialize("Maximum Pool Size").Should().BeTrue();
    }

    [Test]
    public void PreservesSecurityAndPrimaryRoutingOptionsFromUri()
    {
        var configuration = BuildConfiguration(
            "postgresql://accounts:p%40ss@db.internal:5433/relisten"
                + "?sslmode=verify-full"
                + "&sslrootcert=%2Fcerts%2Froot.pem"
                + "&channel_binding=require"
                + "&target_session_attrs=read-write"
                + "&application_name=relisten-users"
                + "&connect_timeout=7"
        );

        var resolved = DatabaseConnectionString.Resolve(configuration);
        var builder = new NpgsqlConnectionStringBuilder(resolved);

        builder.Host.Should().Be("db.internal");
        builder.Port.Should().Be(5433);
        builder.Database.Should().Be("relisten");
        builder.Username.Should().Be("accounts");
        builder.Password.Should().Be("p@ss");
        builder.SslMode.ToString().Should().Be("VerifyFull");
        builder.RootCertificate.Should().Be("/certs/root.pem");
        builder.ChannelBinding.ToString().Should().Be("Require");
        builder.TargetSessionAttributes.Should().Be("read-write");
        builder.ApplicationName.Should().Be("relisten-users");
        builder.Timeout.Should().Be(7);
    }

    [TestCase("statement_timeout=30")]
    [TestCase("sslmode=require&sslmode=disable")]
    public void RejectsQueryOptionsThatCannotBeAppliedUnambiguously(string query)
    {
        var configuration = BuildConfiguration(
            $"postgresql://accounts:secret@db.internal/relisten?{query}"
        );

        var resolve = () => DatabaseConnectionString.Resolve(configuration);

        resolve.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void AccountsDatabaseUrlOverridesFileConnectionString()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Accounts"] =
                        "Host=localhost;Database=local_accounts;Username=local;Password=local",
                    ["ACCOUNTS_DATABASE_URL"] =
                        "postgresql://production:secret@accounts-db.internal/production_accounts",
                }
            )
            .Build();

        var resolved = DatabaseConnectionString.Resolve(configuration);
        var builder = new NpgsqlConnectionStringBuilder(resolved);

        builder.Host.Should().Be("accounts-db.internal");
        builder.Database.Should().Be("production_accounts");
    }

    [Test]
    public void AccountsLockDatabaseUrlOverridesFileConnectionString()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:AccountsLock"] =
                        "Host=stale.example;Database=stale_accounts;Username=stale;Password=stale",
                    ["ACCOUNTS_LOCK_DATABASE_URL"] =
                        "Host=127.0.0.1;Database=local_accounts;Username=local;Password=local",
                }
            )
            .Build();

        var resolved = DatabaseConnectionString.ResolveRefreshTokenLocks(configuration, null!);
        var builder = new NpgsqlConnectionStringBuilder(resolved);

        builder.Host.Should().Be("127.0.0.1");
        builder.Database.Should().Be("local_accounts");
    }

    private static IConfiguration BuildConfiguration(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Accounts"] = connectionString,
                }
            )
            .Build();
}
