using Microsoft.EntityFrameworkCore;

namespace RelistenUserService.Persistence;

public sealed class AccountsDatabaseMigrator(IConfiguration configuration)
{
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = AccountsDbContextFactory.CreateMigrationContext(configuration);
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
