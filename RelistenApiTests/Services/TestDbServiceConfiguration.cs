using FluentAssertions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace RelistenApiTests.Services;

[TestFixture]
[NonParallelizable]
public class TestDbServiceConfiguration
{
    [Test]
    public void UsesExplicitWriteAndReadOnlyHosts()
    {
        _ = new Relisten.DbService(
            "postgresql://app:password@relisten-db-rw.default:5432/app",
            "relisten-db-pgbouncer-rw.default",
            "relisten-db-pgbouncer-ro.default",
            new ProductionHostEnvironment(),
            NullLogger<Relisten.DbService>.Instance);

        Relisten.DbService.ConnStr.Should().Contain("Host=relisten-db-pgbouncer-rw.default;");
        Relisten.DbService.ReadOnlyConnStr.Should()
            .Contain("Host=relisten-db-pgbouncer-ro.default,relisten-db-pgbouncer-rw.default;")
            .And.Contain("Target Session Attributes=prefer-standby");
    }

    [Test]
    public void UsesDatabaseUrlDirectlyWithoutPoolerHosts()
    {
        _ = new Relisten.DbService(
            "postgresql://relisten:password@localhost:15432/relisten_db",
            null,
            null,
            new ProductionHostEnvironment(),
            NullLogger<Relisten.DbService>.Instance);

        Relisten.DbService.ConnStr.Should().Contain("Host=localhost;Port=15432;");
        Relisten.DbService.ReadOnlyConnStr.Should().Contain("Host=localhost,localhost;Port=15432;");
    }

    private sealed class ProductionHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "RelistenApiTests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
