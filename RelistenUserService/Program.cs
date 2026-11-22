using Microsoft.EntityFrameworkCore;
using RelistenUserService;
using RelistenUserService.Persistence;
using RelistenUserService.Playlists;
using RelistenUserService.Search;
using Temporalio.Client;

if (args.FirstOrDefault() == "playlist-moderation")
{
    var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
    using var logging = LoggerFactory.Create(builder => builder.AddConsole());
    var address = configuration["Temporal:Address"];
    var temporalNamespace = configuration["Temporal:Namespace"];
    var search =
        address is null || temporalNamespace is null
            ? null
            : new PlaylistSearchNotifier(
                TemporalClient.CreateLazy(
                    new TemporalClientConnectOptions(address) { Namespace = temporalNamespace }
                ),
                logging.CreateLogger<PlaylistSearchNotifier>()
            );
    Environment.ExitCode = await PlaylistModerationCommand.RunAsync(
        args[1..],
        () => new AccountsDbContextFactory().CreateDbContext([]),
        Console.Out,
        Console.Error,
        CancellationToken.None,
        search
    );
    return;
}

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
