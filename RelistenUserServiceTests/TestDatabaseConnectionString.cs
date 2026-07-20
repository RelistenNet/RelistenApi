using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NUnit.Framework;
using RelistenUserService.Persistence;

namespace RelistenUserServiceTests;

[TestFixture]
public sealed class TestDatabaseConnectionString
{
    [Test]
    public void PreservesSecurityAndPrimaryRoutingOptionsFromUri()
    {
        var configuration = BuildConfiguration(
            "postgresql://accounts:p%40ss@db.internal:5433/relisten" +
            "?sslmode=verify-full" +
            "&sslrootcert=%2Fcerts%2Froot.pem" +
            "&channel_binding=require" +
            "&target_session_attrs=read-write" +
            "&application_name=relisten-users" +
            "&connect_timeout=7");

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
            $"postgresql://accounts:secret@db.internal/relisten?{query}");

        var resolve = () => DatabaseConnectionString.Resolve(configuration);

        resolve.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void AccountsDatabaseUrlOverridesFileConnectionString()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Accounts"] =
                    "Host=localhost;Database=local_accounts;Username=local;Password=local",
                ["ACCOUNTS_DATABASE_URL"] =
                    "postgresql://production:secret@accounts-db.internal/production_accounts"
            })
            .Build();

        var resolved = DatabaseConnectionString.Resolve(configuration);
        var builder = new NpgsqlConnectionStringBuilder(resolved);

        builder.Host.Should().Be("accounts-db.internal");
        builder.Database.Should().Be("production_accounts");
    }

    private static IConfiguration BuildConfiguration(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Accounts"] = connectionString
            })
            .Build();
}
