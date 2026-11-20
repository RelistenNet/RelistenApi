using Microsoft.EntityFrameworkCore;
using RelistenUserService;
using RelistenUserService.Configuration;
using RelistenUserService.Persistence;
using RelistenUserService.Playlists;

if (args.FirstOrDefault() == "playlist-moderation")
{
    Environment.ExitCode = await PlaylistModerationCommand.RunAsync(
        args[1..],
        () => new AccountsDbContextFactory().CreateDbContext([]),
        Console.Out,
        Console.Error,
        CancellationToken.None
    );
    return;
}

if (args is ["backfill-playlist-durations"])
{
    var builder = WebApplication.CreateBuilder([]);
    await using var database = AccountsDbContextFactory.CreateMigrationContext(
        builder.Configuration
    );
    var catalog = builder.Configuration.GetValue(
        "Accounts:CatalogBaseUrl",
        AccountsOptions.DefaultCatalogBaseUrl
    )!;
    using var http = new HttpClient
    {
        BaseAddress = new Uri(catalog.TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromSeconds(10),
    };
    using var logging = LoggerFactory.Create(options => options.AddSimpleConsole());
    var durations = new PlaylistTrackDurations(
        http,
        logging.CreateLogger<PlaylistTrackDurations>()
    );
    var result = await new PlaylistDurationBackfill(
        database,
        durations,
        TimeProvider.System
    ).RunAsync(CancellationToken.None);
    Console.WriteLine(
        $"Updated {result.UpdatedPlaylists} playlists. {result.IncompletePlaylists} still have unknown durations."
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
