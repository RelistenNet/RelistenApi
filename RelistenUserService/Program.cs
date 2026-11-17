using Microsoft.EntityFrameworkCore;
using RelistenUserService;
using RelistenUserService.Persistence;

if (args is ["rotate-history-generation", "--traffic-stopped"])
{
    await using var database = new AccountsDbContextFactory().CreateDbContext([]);
    var generation = Guid.CreateVersion7().ToString();
    var updated = await database
        .HistoryGenerations.Where(row => row.Id == 1)
        .ExecuteUpdateAsync(update => update.SetProperty(row => row.Value, generation));
    if (updated != 1)
    {
        throw new InvalidOperationException(
            "The history-generation singleton is missing. Apply migrations before recovery."
        );
    }
    Console.WriteLine(
        $"History generation rotated to {generation}. Verify recovery before reopening traffic."
    );
    return;
}
if (args.Contains("rotate-history-generation"))
{
    throw new ArgumentException(
        "Stop User Service traffic and all writers, then pass rotate-history-generation --traffic-stopped."
    );
}

var app = AccountsApplication.Build(WebApplication.CreateBuilder(args));
await AccountsApplication.InitializeAsync(app, app.Lifetime.ApplicationStopping);
app.Run();

public partial class Program;
