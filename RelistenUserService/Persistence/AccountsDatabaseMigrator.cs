using Microsoft.EntityFrameworkCore;

namespace RelistenUserService.Persistence;

public sealed class AccountsDatabaseMigrator(IServiceProvider serviceProvider)
{
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
