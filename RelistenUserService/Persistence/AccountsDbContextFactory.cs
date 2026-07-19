using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RelistenUserService.Persistence;

public sealed class AccountsDbContextFactory : IDesignTimeDbContextFactory<AccountsDbContext>
{
    public AccountsDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddJsonFile("RelistenUserService/appsettings.json", optional: true)
            .AddJsonFile("RelistenUserService/appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var builder = new DbContextOptionsBuilder<AccountsDbContext>();
        builder.UseNpgsql(
            DatabaseConnectionString.Resolve(configuration),
            postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", "identity"));
        builder.UseOpenIddict<Guid>();

        return new AccountsDbContext(builder.Options);
    }
}
